using AgenticMES.Application.Common.Interfaces;
using AgenticMES.Application.CQRS;
using AgenticMES.Application.CQRS.Commands;
using AgenticMES.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AgenticMES.Application.Services;

/// <summary>
/// Application service for ISA-95 machine state control and telemetry inspection.
/// Framework-agnostic; can be consumed by AI agents, APIs, or operator interfaces.
/// </summary>
public sealed class MachineControlService(
    ICommandHandler<ChangeMachineStateCommand, ChangeMachineStateResult> machineStateHandler,
    IEquipmentRepository equipmentRepository,
    ILogger<MachineControlService> logger) : IMachineControlService
{
    public async Task<string> SetMachineStateAsync(
        string equipmentCode,
        string targetState,
        string reason,
        string decisionReasoning,
        decimal confidenceScore,
        CancellationToken cancellationToken = default)
    {
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

        if (!Enum.TryParse<EquipmentState>(targetState, ignoreCase: true, out var parsedState))
        {
            return $"Error: Invalid targetState '{targetState}'. Valid values: Idle, Running, Faulted, Maintenance, Setup.";
        }

        var equipment = await equipmentRepository
            .GetByCodeAsync(equipmentCode.Trim(), cancellationToken)
            .ConfigureAwait(false);

        if (equipment is null)
        {
            return $"Error: Equipment with code '{equipmentCode}' was not found in the system.";
        }

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
            "SetMachineState: Equipment={EquipmentCode}, PreviousState={PreviousState}, TargetState={TargetState}, " +
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

    public async Task<string> GetMachineTelemetryStatusAsync(
        string equipmentCode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(equipmentCode))
        {
            return "Error: equipmentCode is required and cannot be empty.";
        }

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
            "GetMachineTelemetryStatus: Equipment={EquipmentCode}, State={State}, WorkOrder={WorkOrderId}",
            equipmentCode,
            equipment.State,
            equipment.CurrentWorkOrderId);

        return statusReport;
    }
}
