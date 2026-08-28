using System.ComponentModel;
using AgenticMES.Application.Common.Interfaces;
using AgenticMES.Application.CQRS;
using AgenticMES.Application.CQRS.Commands;
using AgenticMES.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace AgenticMES.Application.AiTools;

/// <summary>
/// Semantic Kernel AI toolset for ISA-95 machine state control and real-time telemetry inspection.
/// Enables LLM agents to monitor equipment health and coordinate production state transitions.
/// </summary>
public sealed class MachineControlTools(
    ICommandHandler<ChangeMachineStateCommand, ChangeMachineStateResult> machineStateHandler,
    IEquipmentRepository equipmentRepository,
    ILogger<MachineControlTools> logger)
{
    /// <summary>
    /// Requests an ISA-95 equipment state transition (e.g., Idle → Running, Running → Faulted, Idle → Maintenance).
    /// High-risk actions (stopping active machines, triggering faults, entering maintenance) require human approval
    /// before execution and will return a PendingApproval status.
    /// </summary>
    /// <param name="equipmentCode">Plant-floor ISA-95 equipment identifier (e.g., CNC-03, LATHE-01, ROBOT-ARM-05).</param>
    /// <param name="targetState">Desired equipment state: Idle, Running, Faulted, Maintenance, or Setup.</param>
    /// <param name="reason">Clear operational justification for the state change (e.g., 'Scheduled preventive maintenance', 'Vibration sensor alarm threshold exceeded', 'Production shift change').</param>
    /// <param name="decisionReasoning">AI agent's internal reasoning chain explaining why this action was chosen (e.g., 'Detected anomalous vibration pattern exceeding 3-sigma threshold for 5 consecutive minutes').</param>
    /// <param name="confidenceScore">AI confidence level for this decision (0.0 to 1.0). Values below 0.7 should trigger human review.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A result object indicating success/failure, approval status (Executed, PendingApproval, or Approved),
    /// previous and new states, and execution timestamp for audit trail.
    /// </returns>
    /// <remarks>
    /// Use this tool when:
    /// - Equipment must be stopped due to detected faults, quality issues, or safety conditions.
    /// - A machine needs to transition from Idle to Running to start production.
    /// - Preventive or corrective maintenance requires locking out equipment.
    /// - Setup operations (changeovers, calibration) require temporary state changes.
    /// Do NOT use if you are uncertain about the equipment code or if the reason is vague.
    /// Always provide detailed reasoning and an honest confidence score to enable proper human oversight.
    /// </remarks>
    [KernelFunction]
    [Description(
        "Transitions an ISA-95 industrial equipment to a new operational state (Idle, Running, Faulted, Maintenance, Setup). " +
        "High-risk actions (stopping active production, entering maintenance, triggering faults) require human approval. " +
        "Use when equipment must start/stop production, enter maintenance mode, or handle fault conditions. " +
        "Always provide clear operational reasoning and an honest AI confidence score (0.0-1.0) for audit compliance.")]
    public async Task<string> SetMachineStateAsync(
        [Description("ISA-95 equipment code (e.g., CNC-03, LATHE-01)")] string equipmentCode,
        [Description("Target state: Idle, Running, Faulted, Maintenance, or Setup")] string targetState,
        [Description("Clear operational reason for the state change (e.g., 'Scheduled preventive maintenance', 'Detected anomalous vibration')")] string reason,
        [Description("AI agent's internal reasoning explaining why this action was chosen")] string decisionReasoning,
        [Description("AI confidence level (0.0 to 1.0). Values < 0.7 should trigger review.")] decimal confidenceScore,
        CancellationToken cancellationToken = default)
    {
        // Input validation (Guardrails)
        if (string.IsNullOrWhiteSpace(equipmentCode))
        {
            return "Error: equipmentCode is required and cannot be empty.";
        }

        if (string.IsNullOrWhiteSpace(targetState))
        {
            return "Error: targetState is required and cannot be empty.";
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

        // Parse target state
        if (!Enum.TryParse<EquipmentState>(targetState, ignoreCase: true, out var parsedState))
        {
            return $"Error: Invalid targetState '{targetState}'. Valid values: Idle, Running, Faulted, Maintenance, Setup.";
        }

        // Resolve equipment by code
        var equipment = await equipmentRepository
            .GetByCodeAsync(equipmentCode.Trim(), cancellationToken)
            .ConfigureAwait(false);

        if (equipment is null)
        {
            return $"Error: Equipment with code '{equipmentCode}' was not found in the system.";
        }

        // Execute command
        var command = new ChangeMachineStateCommand(
            EquipmentId: equipment.Id,
            TargetState: parsedState,
            Reason: reason.Trim(),
            TriggeredBy: "AI-Agent",
            ApprovalStatus: ApprovalStatus.PendingApproval,
            DecisionReasoning: decisionReasoning.Trim(),
            ConfidenceScore: confidenceScore);

        var result = await machineStateHandler
            .HandleAsync(command, cancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "AI SetMachineState: Equipment={EquipmentCode}, PreviousState={PreviousState}, TargetState={TargetState}, " +
            "Status={ApprovalStatus}, Confidence={Confidence}, Success={IsSuccess}",
            equipmentCode,
            result.PreviousState,
            result.NewState ?? parsedState,
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
                $"Success: State transition '{result.EquipmentCode}' from {result.PreviousState} to {result.NewState} is PENDING HUMAN APPROVAL. " +
                $"This is a high-risk action. Reason: {result.Reason}. Timestamp: {result.ExecutionTimestamp:O}.",

            ApprovalStatus.Executed =>
                $"Success: Equipment '{result.EquipmentCode}' transitioned from {result.PreviousState} to {result.NewState}. " +
                $"Reason: {result.Reason}. Timestamp: {result.ExecutionTimestamp:O}.",

            _ =>
                $"Success: State change recorded with status {result.ApprovalStatus}. Equipment: {result.EquipmentCode}."
        };
    }

    /// <summary>
    /// Retrieves the current operational state and latest telemetry snapshot for a specified equipment.
    /// Provides real-time visibility into machine health, production status, and work order assignment.
    /// </summary>
    /// <param name="equipmentCode">Plant-floor ISA-95 equipment identifier (e.g., CNC-03, LATHE-01, ROBOT-ARM-05).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A formatted status report including current operational state, assigned work order (if any),
    /// time since last state change, and fault reason (if applicable).
    /// </returns>
    /// <remarks>
    /// Use this tool when:
    /// - You need to verify a machine's current operational state before making decisions.
    /// - Investigating production delays or equipment availability issues.
    /// - Checking if a machine is assigned to a work order before rerouting production.
    /// - Monitoring fault conditions or understanding equipment downtime reasons.
    /// This is a read-only, low-risk operation that does not require approval.
    /// </remarks>
    [KernelFunction]
    [Description(
        "Retrieves the current operational state and status of an ISA-95 industrial equipment. " +
        "Returns the equipment's state (Idle, Running, Faulted, Maintenance, Setup), assigned work order, " +
        "time since last state change, and fault reason if applicable. " +
        "Use this to verify equipment availability, diagnose production issues, or check machine health before making control decisions.")]
    public async Task<string> GetMachineTelemetryStatusAsync(
        [Description("ISA-95 equipment code (e.g., CNC-03, LATHE-01)")] string equipmentCode,
        CancellationToken cancellationToken = default)
    {
        // Input validation (Guardrails)
        if (string.IsNullOrWhiteSpace(equipmentCode))
        {
            return "Error: equipmentCode is required and cannot be empty.";
        }

        // Resolve equipment by code
        var equipment = await equipmentRepository
            .GetByCodeAsync(equipmentCode.Trim(), cancellationToken)
            .ConfigureAwait(false);

        if (equipment is null)
        {
            return $"Error: Equipment with code '{equipmentCode}' was not found in the system.";
        }

        var timeSinceStateChange = DateTimeOffset.UtcNow - equipment.StateChangedAt;
        var stateChangeDuration = timeSinceStateChange.TotalMinutes switch
        {
            < 1 => $"{timeSinceStateChange.TotalSeconds:F0} seconds",
            < 60 => $"{timeSinceStateChange.TotalMinutes:F1} minutes",
            _ => $"{timeSinceStateChange.TotalHours:F1} hours"
        };

        var statusReport = $"Equipment: {equipment.EquipmentCode} ({equipment.Name})\n" +
                          $"Current State: {equipment.State}\n" +
                          $"State Changed: {stateChangeDuration} ago (at {equipment.StateChangedAt:yyyy-MM-dd HH:mm:ss} UTC)\n" +
                          $"Hierarchy: {equipment.Level} under Parent ID: {equipment.ParentEquipmentId?.ToString() ?? "None"}\n";

        if (equipment.CurrentWorkOrderId.HasValue)
        {
            statusReport += $"Assigned Work Order ID: {equipment.CurrentWorkOrderId}\n";
        }
        else
        {
            statusReport += "Assigned Work Order: None (equipment is available)\n";
        }

        if (!string.IsNullOrWhiteSpace(equipment.LastFaultReason))
        {
            statusReport += $"Last Fault Reason: {equipment.LastFaultReason}\n";
        }

        statusReport += $"Hierarchy Path: {equipment.Hierarchy.EnterpriseId ?? "N/A"} > {equipment.Hierarchy.SiteId ?? "N/A"} > " +
                       $"{equipment.Hierarchy.AreaId ?? "N/A"} > {equipment.Hierarchy.WorkCenterId ?? "N/A"}";

        logger.LogInformation(
            "AI GetMachineTelemetryStatus: Equipment={EquipmentCode}, State={State}, WorkOrder={WorkOrderId}",
            equipmentCode,
            equipment.State,
            equipment.CurrentWorkOrderId);

        return statusReport;
    }
}
