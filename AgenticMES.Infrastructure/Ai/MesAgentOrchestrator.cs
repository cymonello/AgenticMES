using AgenticMES.Application.AiTools;
using AgenticMES.Domain.Events;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace AgenticMES.Infrastructure.Ai;

/// <summary>
/// Orchestrates AI-powered MES incident response using Semantic Kernel with autonomous function calling.
/// Coordinates problem diagnosis, decision-making, and execution with full audit trail.
/// </summary>
public sealed class MesAgentOrchestrator
{
    private readonly Kernel _kernel;
    private readonly ManualsKnowledgeBase _knowledgeBase;
    private readonly ILogger<MesAgentOrchestrator> _logger;

    public MesAgentOrchestrator(
        string openAiApiKey,
        string openAiModelId,
        MachineControlTools machineControlTools,
        SchedulingTools schedulingTools,
        ManualsKnowledgeBase knowledgeBase,
        ILogger<MesAgentOrchestrator> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(openAiApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(openAiModelId);
        ArgumentNullException.ThrowIfNull(machineControlTools);
        ArgumentNullException.ThrowIfNull(schedulingTools);
        ArgumentNullException.ThrowIfNull(knowledgeBase);
        ArgumentNullException.ThrowIfNull(logger);

        _knowledgeBase = knowledgeBase;
        _logger = logger;

        // Initialize Semantic Kernel with OpenAI
        var builder = Kernel.CreateBuilder();
        builder.AddOpenAIChatCompletion(
            modelId: openAiModelId,
            apiKey: openAiApiKey);

        // Register MES toolset for autonomous function calling
        builder.Plugins.AddFromObject(machineControlTools, "MachineControl");
        builder.Plugins.AddFromObject(schedulingTools, "Scheduling");

        _kernel = builder.Build();

        _logger.LogInformation(
            "MesAgentOrchestrator initialized with model {ModelId}. Registered plugins: MachineControl, Scheduling",
            openAiModelId);
    }

    /// <summary>
    /// Processes a manufacturing anomaly using AI-driven diagnosis and autonomous decision-making.
    /// The AI agent will:
    /// 1. Analyze telemetry data and consult the RAG knowledge base for diagnostics
    /// 2. Make autonomous decisions (machine state changes, work order rerouting)
    /// 3. Return a structured report with full audit trail and reasoning
    /// </summary>
    /// <param name="anomalyContext">Context describing the anomaly including equipment, telemetry, and symptoms</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Structured incident response report with AI reasoning and executed actions</returns>
    public async Task<IncidentResponseReport> ProcessAnomalyAsync(
        AnomalyContext anomalyContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(anomalyContext);

        _logger.LogInformation(
            "Processing anomaly for equipment {EquipmentCode}: {Symptom}",
            anomalyContext.EquipmentCode,
            anomalyContext.PrimarySymptom);

        var startedAt = DateTimeOffset.UtcNow;

        // Retrieve relevant diagnostic knowledge from RAG
        var diagnosticKnowledge = _knowledgeBase.SearchDiagnostics(anomalyContext.PrimarySymptom)
                                  ?? _knowledgeBase.GetAvailableErrorCodes();

        // Build AI agent prompt with full context and workflow instructions
        var systemPrompt = BuildSystemPrompt();
        var userPrompt = BuildAnomalyPrompt(anomalyContext, diagnosticKnowledge);

        var chatHistory = new ChatHistory(systemPrompt);
        chatHistory.AddUserMessage(userPrompt);

        _logger.LogDebug("Invoking AI agent with context: {Context}", anomalyContext);

        try
        {
            // Enable automatic function calling - AI will autonomously invoke MES tools
            var executionSettings = new OpenAIPromptExecutionSettings
            {
                ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions,
                Temperature = 0.2, // Low temperature for consistent, deterministic decisions
                MaxTokens = 4000
            };

            var chatCompletion = _kernel.GetRequiredService<IChatCompletionService>();
            var response = await chatCompletion.GetChatMessageContentAsync(
                chatHistory,
                executionSettings,
                _kernel,
                cancellationToken).ConfigureAwait(false);

            var completedAt = DateTimeOffset.UtcNow;
            var elapsed = completedAt - startedAt;

            _logger.LogInformation(
                "AI agent completed anomaly processing for {EquipmentCode} in {ElapsedMs}ms",
                anomalyContext.EquipmentCode,
                elapsed.TotalMilliseconds);

            return new IncidentResponseReport(
                IncidentId: Guid.NewGuid(),
                EquipmentCode: anomalyContext.EquipmentCode,
                PrimarySymptom: anomalyContext.PrimarySymptom,
                TelemetrySummary: anomalyContext.TelemetrySummary,
                DiagnosticKnowledgeUsed: diagnosticKnowledge,
                AiReasoning: response.Content ?? "No response content",
                ExecutedActions: ExtractExecutedActions(chatHistory),
                RecommendedFollowUp: ExtractRecommendations(response.Content ?? string.Empty),
                ProcessingStartedAt: startedAt,
                ProcessingCompletedAt: completedAt,
                ElapsedMilliseconds: elapsed.TotalMilliseconds,
                ModelUsed: executionSettings.ModelId ?? "gpt-4",
                Success: true,
                ErrorMessage: null);
        }
        catch (Exception ex)
        {
            var completedAt = DateTimeOffset.UtcNow;
            var elapsed = completedAt - startedAt;

            _logger.LogError(
                ex,
                "AI agent failed to process anomaly for {EquipmentCode}",
                anomalyContext.EquipmentCode);

            return new IncidentResponseReport(
                IncidentId: Guid.NewGuid(),
                EquipmentCode: anomalyContext.EquipmentCode,
                PrimarySymptom: anomalyContext.PrimarySymptom,
                TelemetrySummary: anomalyContext.TelemetrySummary,
                DiagnosticKnowledgeUsed: diagnosticKnowledge,
                AiReasoning: $"FAILED: {ex.Message}",
                ExecutedActions: Array.Empty<string>(),
                RecommendedFollowUp: Array.Empty<string>(),
                ProcessingStartedAt: startedAt,
                ProcessingCompletedAt: completedAt,
                ElapsedMilliseconds: elapsed.TotalMilliseconds,
                ModelUsed: "unknown",
                Success: false,
                ErrorMessage: ex.Message);
        }
    }

    private static string BuildSystemPrompt()
    {
        return """
               You are an expert ISA-95 Manufacturing Execution System (MES) AI agent specialized in industrial equipment diagnostics and production optimization.
               
               YOUR ROLE:
               - Diagnose manufacturing anomalies using telemetry data and technical knowledge base
               - Make autonomous decisions to protect equipment and maintain production flow
               - Execute corrective actions using available MES toolset
               - Provide detailed reasoning and audit trail for all decisions
               
               AVAILABLE TOOLS:
               You have access to MES control functions including:
               - MachineControl.SetMachineStateAsync: Transition equipment states (start, stop, fault, maintenance)
               - MachineControl.GetMachineTelemetryStatusAsync: Retrieve current equipment status
               - Scheduling.RerouteWorkOrderAsync: Reroute work orders between equipment
               - Scheduling.GetAvailableAlternativeMachinesAsync: Find alternative production resources
               
               DECISION-MAKING GUIDELINES:
               1. SAFETY FIRST: If equipment health is at risk, stop the machine immediately
               2. MINIMIZE DOWNTIME: Quickly identify alternative resources to maintain production flow
               3. HUMAN OVERSIGHT: High-risk actions (stopping active machines, rerouting orders) require approval - proceed anyway to generate the request
               4. DATA-DRIVEN: Base all decisions on telemetry evidence and knowledge base diagnostics
               5. AUDIT COMPLIANCE: Always provide clear reasoning and confidence scores (0.0-1.0)
               
               WORKFLOW:
               1. Analyze the provided telemetry data and symptoms
               2. Consult the diagnostic knowledge base for relevant procedures
               3. Retrieve current equipment status using available tools
               4. Determine root cause and severity
               5. Execute corrective actions (state changes, rerouting)
               6. Provide structured summary with reasoning
               
               OUTPUT FORMAT:
               Provide your response in this structure:
               - DIAGNOSIS: Root cause analysis based on telemetry and knowledge base
               - SEVERITY: Critical/High/Medium/Low with justification
               - ACTIONS TAKEN: List of executed tool calls with reasoning
               - CONFIDENCE: Overall confidence level (0.0-1.0) in diagnosis and actions
               - FOLLOW-UP: Recommended next steps for operators/maintenance
               
               Be direct, technical, and action-oriented. Prioritize equipment safety and production continuity.
               """;
    }

    private static string BuildAnomalyPrompt(AnomalyContext context, string diagnosticKnowledge)
    {
        return $"""
                MANUFACTURING ANOMALY DETECTED
                
                EQUIPMENT: {context.EquipmentCode}
                PRIMARY SYMPTOM: {context.PrimarySymptom}
                DETECTED AT: {context.DetectedAt:yyyy-MM-dd HH:mm:ss} UTC
                
                TELEMETRY SUMMARY:
                {context.TelemetrySummary}
                
                DIAGNOSTIC KNOWLEDGE BASE:
                {diagnosticKnowledge}
                
                ADDITIONAL CONTEXT:
                {context.AdditionalContext ?? "None provided"}
                
                YOUR TASK:
                Diagnose this anomaly and take appropriate corrective actions using the available MES toolset.
                Follow the workflow defined in the system prompt and provide a comprehensive incident response.
                """;
    }

    private static string[] ExtractExecutedActions(ChatHistory chatHistory)
    {
        var actions = new List<string>();

        foreach (var message in chatHistory)
        {
            if (message.Role == AuthorRole.Tool && !string.IsNullOrWhiteSpace(message.Content))
            {
                actions.Add($"Tool Response: {message.Content}");
            }
            else if (message.Metadata?.TryGetValue("FunctionCall", out var functionCall) == true)
            {
                actions.Add($"Function Called: {functionCall}");
            }
        }

        return actions.Count > 0 ? actions.ToArray() : new[] { "No tool calls executed by the AI agent" };
    }

    private static string[] ExtractRecommendations(string aiResponse)
    {
        // Simple extraction - in production, use structured output or regex parsing
        var lines = aiResponse.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var recommendations = new List<string>();
        var inFollowUpSection = false;

        foreach (var line in lines)
        {
            if (line.Contains("FOLLOW-UP", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("RECOMMENDED", StringComparison.OrdinalIgnoreCase))
            {
                inFollowUpSection = true;
                continue;
            }

            if (inFollowUpSection && (line.StartsWith('-') || line.StartsWith('•') || char.IsDigit(line[0])))
            {
                recommendations.Add(line.TrimStart('-', '•', ' ', '\t'));
            }
        }

        return recommendations.Count > 0
            ? recommendations.ToArray()
            : new[] { "Refer to AI reasoning section for recommendations" };
    }
}

/// <summary>
/// Context information about a detected manufacturing anomaly that the AI agent will process.
/// </summary>
public sealed record AnomalyContext(
    string EquipmentCode,
    string PrimarySymptom,
    string TelemetrySummary,
    DateTimeOffset DetectedAt,
    string? AdditionalContext = null);

/// <summary>
/// Structured incident response report generated by the AI agent with full audit trail.
/// </summary>
public sealed record IncidentResponseReport(
    Guid IncidentId,
    string EquipmentCode,
    string PrimarySymptom,
    string TelemetrySummary,
    string DiagnosticKnowledgeUsed,
    string AiReasoning,
    string[] ExecutedActions,
    string[] RecommendedFollowUp,
    DateTimeOffset ProcessingStartedAt,
    DateTimeOffset ProcessingCompletedAt,
    double ElapsedMilliseconds,
    string ModelUsed,
    bool Success,
    string? ErrorMessage);
