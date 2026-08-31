using AgenticMES.Application.Common.Interfaces;
using AgenticMES.Application.CQRS;
using AgenticMES.Application.CQRS.Commands;
using AgenticMES.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AgenticMES.Application.Services;

/// <summary>
/// Application service for ISA-95 production scheduling and work order dispatch optimization.
/// Framework-agnostic; can be consumed by AI agents, APIs, or operator interfaces.
/// </summary>
public sealed class SchedulingService(
    ICommandHandler<RerouteWorkOrderCommand, RerouteWorkOrderResult> rerouteHandler,
    IEquipmentRepository equipmentRepository,
    IWorkOrderRepository workOrderRepository,
    ILogger<SchedulingService> logger) : ISchedulingService
{
    public async Task<string> RerouteWorkOrderAsync(
        string workOrderNumber,
        string sourceEquipmentCode,
        string targetEquipmentCode,
        string reason,
        string decisionReasoning,
        decimal confidenceScore,
        CancellationToken cancellationToken = default)
    {
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
            "RerouteWorkOrder: WorkOrder={WorkOrderNumber}, Source={SourceCode}, Target={TargetCode}, " +
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

    public async Task<string> GetAvailableAlternativeMachinesAsync(
        string equipmentCode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(equipmentCode))
        {
            return "Error: equipmentCode is required and cannot be empty.";
        }

        var referenceEquipment = await equipmentRepository
            .GetByCodeAsync(equipmentCode.Trim(), cancellationToken)
            .ConfigureAwait(false);

        if (referenceEquipment is null)
        {
            return $"Error: Equipment with code '{equipmentCode}' was not found in the system.";
        }

        var allEquipment = await equipmentRepository
            .ListAsync(cancellationToken)
            .ConfigureAwait(false);

        var alternatives = allEquipment
            .Where(e =>
                e.Id != referenceEquipment.Id &&
                e.Level == referenceEquipment.Level &&
                e.ParentEquipmentId == referenceEquipment.ParentEquipmentId &&
                e.State is not (EquipmentState.Faulted or EquipmentState.Maintenance))
            .OrderBy(e => e.State == EquipmentState.Idle ? 0 : 1)
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
            "GetAvailableAlternativeMachines: Reference={EquipmentCode}, AlternativesFound={Count}",
            equipmentCode,
            alternatives.Count);

        return report;
    }
}
