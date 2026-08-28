using System.ComponentModel;
using AgenticMES.Application.Common.Interfaces;
using AgenticMES.Application.CQRS;
using AgenticMES.Application.CQRS.Commands;
using AgenticMES.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace AgenticMES.Application.AiTools;

/// <summary>
/// Semantic Kernel AI toolset for ISA-95 production scheduling and work order dispatch optimization.
/// Enables LLM agents to dynamically reroute production and identify alternative manufacturing resources.
/// </summary>
public sealed class SchedulingTools(
    ICommandHandler<RerouteWorkOrderCommand, RerouteWorkOrderResult> rerouteHandler,
    IEquipmentRepository equipmentRepository,
    IWorkOrderRepository workOrderRepository,
    ILogger<SchedulingTools> logger)
{
    /// <summary>
    /// Reroutes an active work order from one equipment to another, enabling dynamic production optimization
    /// in response to equipment faults, maintenance needs, or capacity constraints.
    /// This is a high-risk operation that ALWAYS requires human approval before execution.
    /// </summary>
    /// <param name="workOrderNumber">Plant-floor operations request identifier (e.g., WO-2024-001234).</param>
    /// <param name="sourceEquipmentCode">Current equipment code where the work order is assigned (e.g., CNC-03).</param>
    /// <param name="targetEquipmentCode">Destination equipment code to receive the work order (e.g., CNC-05).</param>
    /// <param name="reason">Clear operational justification for the reroute (e.g., 'Source machine faulted with spindle error', 'Optimize utilization due to priority change').</param>
    /// <param name="decisionReasoning">AI agent's internal reasoning chain explaining why this reroute was chosen (e.g., 'Target machine has 40% lower utilization and compatible tooling').</param>
    /// <param name="confidenceScore">AI confidence level for this decision (0.0 to 1.0). Values below 0.7 should trigger human review.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A result object indicating success/failure, approval status (always PendingApproval initially),
    /// remaining quantity to produce, already produced quantity, and execution timestamp for audit trail.
    /// </returns>
    /// <remarks>
    /// Use this tool when:
    /// - Source equipment becomes unavailable (Faulted, entering Maintenance).
    /// - Production priority changes require shifting work to higher-capacity resources.
    /// - Load balancing optimization identifies underutilized alternative equipment.
    /// - Quality issues on source equipment necessitate moving production to verified machines.
    /// Do NOT use if:
    /// - Work order is already Completed, Cancelled, or not yet Released.
    /// - Target equipment is Faulted or in Maintenance.
    /// - You are uncertain about equipment codes or compatibility.
    /// Always provide detailed reasoning and an honest confidence score to enable proper human oversight.
    /// </remarks>
    [KernelFunction]
    [Description(
        "Reroutes an ISA-95 work order from one equipment to another to handle faults, maintenance, or optimize production flow. " +
        "This is a HIGH-RISK operation that ALWAYS requires human approval. " +
        "Stops the work order on the source equipment and dispatches remaining quantity to the target equipment. " +
        "Use when source equipment fails, enters maintenance, or when load balancing requires shifting production. " +
        "Always provide clear operational reasoning and an honest AI confidence score (0.0-1.0) for audit compliance.")]
    public async Task<string> RerouteWorkOrderAsync(
        [Description("Work order number (e.g., WO-2024-001234)")] string workOrderNumber,
        [Description("Current equipment code where work order is assigned (e.g., CNC-03)")] string sourceEquipmentCode,
        [Description("Destination equipment code to receive the work order (e.g., CNC-05)")] string targetEquipmentCode,
        [Description("Clear operational reason for the reroute (e.g., 'Source machine faulted', 'Optimize utilization')")] string reason,
        [Description("AI agent's internal reasoning explaining why this reroute was chosen")] string decisionReasoning,
        [Description("AI confidence level (0.0 to 1.0). Values < 0.7 should trigger review.")] decimal confidenceScore,
        CancellationToken cancellationToken = default)
    {
        // Input validation (Guardrails)
        if (string.IsNullOrWhiteSpace(workOrderNumber))
        {
            return "Error: workOrderNumber is required and cannot be empty.";
        }

        if (string.IsNullOrWhiteSpace(sourceEquipmentCode))
        {
            return "Error: sourceEquipmentCode is required and cannot be empty.";
        }

        if (string.IsNullOrWhiteSpace(targetEquipmentCode))
        {
            return "Error: targetEquipmentCode is required and cannot be empty.";
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return "Error: reason is required for audit compliance. Provide a clear operational justification.";
        }

        if (string.IsNullOrWhiteSpace(decisionReasoning))
        {
            return "Error: decisionReasoning is required. Explain the AI agent's reasoning chain.";
        }

        if (confidenceScore is < 0m or > 1m)
        {
            return "Error: confidenceScore must be between 0.0 and 1.0.";
        }

        if (sourceEquipmentCode.Trim().Equals(targetEquipmentCode.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return "Error: sourceEquipmentCode and targetEquipmentCode must be different.";
        }

        // Resolve entities
        var workOrder = await workOrderRepository
            .GetByNumberAsync(workOrderNumber.Trim(), cancellationToken)
            .ConfigureAwait(false);

        if (workOrder is null)
        {
            return $"Error: Work order '{workOrderNumber}' was not found in the system.";
        }

        var sourceEquipment = await equipmentRepository
            .GetByCodeAsync(sourceEquipmentCode.Trim(), cancellationToken)
            .ConfigureAwait(false);

        if (sourceEquipment is null)
        {
            return $"Error: Source equipment with code '{sourceEquipmentCode}' was not found in the system.";
        }

        var targetEquipment = await equipmentRepository
            .GetByCodeAsync(targetEquipmentCode.Trim(), cancellationToken)
            .ConfigureAwait(false);

        if (targetEquipment is null)
        {
            return $"Error: Target equipment with code '{targetEquipmentCode}' was not found in the system.";
        }

        // Execute command
        var command = new RerouteWorkOrderCommand(
            WorkOrderId: workOrder.Id,
            SourceEquipmentId: sourceEquipment.Id,
            TargetEquipmentId: targetEquipment.Id,
            Reason: reason.Trim(),
            TriggeredBy: "AI-Agent",
            ApprovalStatus: ApprovalStatus.PendingApproval,
            DecisionReasoning: decisionReasoning.Trim(),
            ConfidenceScore: confidenceScore);

        var result = await rerouteHandler
            .HandleAsync(command, cancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "AI RerouteWorkOrder: WorkOrder={WorkOrderNumber}, Source={SourceCode}, Target={TargetCode}, " +
            "Status={ApprovalStatus}, Confidence={Confidence}, Success={IsSuccess}",
            workOrderNumber,
            sourceEquipmentCode,
            targetEquipmentCode,
            result.ApprovalStatus,
            confidenceScore,
            result.IsSuccess);

        if (!result.IsSuccess)
        {
            return $"Failed: {result.Error}";
        }

        return result.ApprovalStatus switch
        {
            ApprovalStatus.PendingApproval =>
                $"Success: Work order '{result.WorkOrderNumber}' reroute from {result.SourceEquipmentCode} to {result.TargetEquipmentCode} " +
                $"is PENDING HUMAN APPROVAL (high-risk operation). " +
                $"Remaining quantity: {result.RemainingQuantity}, Already produced: {result.ProducedQuantity}. " +
                $"Reason: {result.Reason}. Timestamp: {result.ExecutionTimestamp:O}.",

            ApprovalStatus.Executed =>
                $"Success: Work order '{result.WorkOrderNumber}' rerouted from {result.SourceEquipmentCode} to {result.TargetEquipmentCode}. " +
                $"Remaining quantity: {result.RemainingQuantity}, Already produced: {result.ProducedQuantity}. " +
                $"Reason: {result.Reason}. Timestamp: {result.ExecutionTimestamp:O}.",

            _ =>
                $"Success: Reroute recorded with status {result.ApprovalStatus}. Work order: {result.WorkOrderNumber}."
        };
    }

    /// <summary>
    /// Identifies equipment that can serve as alternative production resources for work order rerouting.
    /// Filters for equipment that is operationally available (Idle or Running without conflicts),
    /// at the same hierarchical level, and physically located in the same area or work center.
    /// </summary>
    /// <param name="equipmentCode">Reference equipment code to find alternatives for (e.g., CNC-03).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A formatted list of alternative equipment codes with their current operational state,
    /// work order assignment status, and hierarchical location.
    /// </returns>
    /// <remarks>
    /// Use this tool when:
    /// - Planning a work order reroute and need to identify suitable target equipment.
    /// - Equipment faults or maintenance events require finding backup machines.
    /// - Performing capacity planning or load balancing analysis.
    /// - Investigating production flexibility and redundancy options.
    /// This is a read-only, low-risk operation that does not require approval.
    /// The tool prioritizes Idle equipment (no active work orders) over Running equipment
    /// and excludes Faulted or Maintenance equipment entirely.
    /// </remarks>
    [KernelFunction]
    [Description(
        "Identifies alternative ISA-95 equipment suitable for work order rerouting. " +
        "Returns a list of equipment at the same hierarchical level and location that are operationally available (Idle or Running without conflicts). " +
        "Excludes Faulted or Maintenance equipment. " +
        "Use this to plan reroutes, analyze production capacity, or find backup machines when equipment becomes unavailable.")]
    public async Task<string> GetAvailableAlternativeMachinesAsync(
        [Description("Reference equipment code to find alternatives for (e.g., CNC-03)")] string equipmentCode,
        CancellationToken cancellationToken = default)
    {
        // Input validation (Guardrails)
        if (string.IsNullOrWhiteSpace(equipmentCode))
        {
            return "Error: equipmentCode is required and cannot be empty.";
        }

        // Resolve reference equipment
        var referenceEquipment = await equipmentRepository
            .GetByCodeAsync(equipmentCode.Trim(), cancellationToken)
            .ConfigureAwait(false);

        if (referenceEquipment is null)
        {
            return $"Error: Equipment with code '{equipmentCode}' was not found in the system.";
        }

        // Get all equipment in the system
        var allEquipment = await equipmentRepository
            .ListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Filter for alternatives:
        // 1. Same hierarchical level (e.g., both are Equipment-level machines, not WorkCenters)
        // 2. Same parent (same work center or area)
        // 3. Not the reference equipment itself
        // 4. Operationally available (not Faulted or in Maintenance)
        var alternatives = allEquipment
            .Where(e =>
                e.Id != referenceEquipment.Id &&
                e.Level == referenceEquipment.Level &&
                e.ParentEquipmentId == referenceEquipment.ParentEquipmentId &&
                e.State is not (EquipmentState.Faulted or EquipmentState.Maintenance))
            .OrderBy(e => e.State == EquipmentState.Idle ? 0 : 1) // Idle machines first
            .ThenBy(e => e.EquipmentCode)
            .ToList();

        if (alternatives.Count == 0)
        {
            return $"No alternative equipment found for '{equipmentCode}' " +
                   $"(Level: {referenceEquipment.Level}, Parent: {referenceEquipment.ParentEquipmentId?.ToString() ?? "None"}).";
        }

        var report = $"Alternative equipment for '{equipmentCode}' " +
                    $"(Level: {referenceEquipment.Level}, Hierarchy: {referenceEquipment.Hierarchy.AreaId ?? "N/A"} > {referenceEquipment.Hierarchy.WorkCenterId ?? "N/A"}):\n\n";

        foreach (var alt in alternatives)
        {
            var availability = alt.State == EquipmentState.Idle
                ? "AVAILABLE (Idle, no work order)"
                : alt.CurrentWorkOrderId.HasValue
                    ? $"BUSY (Running with work order {alt.CurrentWorkOrderId})"
                    : $"Available but {alt.State}";

            report += $"- {alt.EquipmentCode} ({alt.Name}): State={alt.State}, {availability}\n";
        }

        report += $"\nTotal alternatives found: {alternatives.Count}";

        logger.LogInformation(
            "AI GetAvailableAlternativeMachines: Reference={EquipmentCode}, AlternativesFound={Count}",
            equipmentCode,
            alternatives.Count);

        return report;
    }
}
