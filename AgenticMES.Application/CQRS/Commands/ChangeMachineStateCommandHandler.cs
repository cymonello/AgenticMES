using AgenticMES.Application.Common.Interfaces;
using AgenticMES.Domain.Common;
using AgenticMES.Domain.Entities;
using AgenticMES.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AgenticMES.Application.CQRS.Commands;

public sealed class ChangeMachineStateCommandHandler(
    IEquipmentRepository equipmentRepository,
    IWorkOrderRepository workOrderRepository,
    ILogger<ChangeMachineStateCommandHandler> logger)
    : ICommandHandler<ChangeMachineStateCommand, ChangeMachineStateResult>
{
    public async Task<ChangeMachineStateResult> HandleAsync(
        ChangeMachineStateCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var auditError = CommandGuards.ValidateAudit(command.Reason, command.TriggeredBy, command.ConfidenceScore);
        if (auditError is not null)
        {
            return ChangeMachineStateResult.Failure(auditError, command.Reason, command.TriggeredBy);
        }

        var equipment = await equipmentRepository
            .GetByIdAsync(command.EquipmentId, cancellationToken)
            .ConfigureAwait(false);

        if (equipment is null)
        {
            return ChangeMachineStateResult.Failure(
                $"Equipment {command.EquipmentId} was not found.",
                command.Reason,
                command.TriggeredBy);
        }

        if (RequiresHitl(command.TargetState) && !CommandGuards.IsApproved(command.ApprovalStatus))
        {
            logger.LogInformation(
                "ChangeMachineState pending approval for {EquipmentCode}: {PreviousState} → {TargetState}. Reason: {Reason}. TriggeredBy: {TriggeredBy}",
                equipment.EquipmentCode,
                equipment.State,
                command.TargetState,
                command.Reason,
                command.TriggeredBy);

            return ChangeMachineStateResult.Pending(
                equipment.EquipmentCode,
                equipment.State,
                command.TargetState,
                command.Reason,
                command.TriggeredBy);
        }

        if (!equipment.CanTransitionTo(command.TargetState))
        {
            return ChangeMachineStateResult.Failure(
                $"Illegal ISA-95 equipment transition: {equipment.State} → {command.TargetState} on {equipment.EquipmentCode}.",
                command.Reason,
                command.TriggeredBy);
        }

        var previousState = equipment.State;
        WorkOrder? heldWorkOrder = null;
        if (command.TargetState is not EquipmentState.Running
            && equipment.CurrentWorkOrderId is { } workOrderId)
        {
            var holdResult = await HoldIfInProgressAsync(workOrderId, cancellationToken).ConfigureAwait(false);
            if (!holdResult.IsSuccess)
            {
                return ChangeMachineStateResult.Failure(holdResult.Error!, command.Reason, command.TriggeredBy);
            }

            heldWorkOrder = holdResult.WorkOrder;
        }

        var transition = ApplyTransition(equipment, command.TargetState, command.Reason);
        if (!transition.IsSuccess)
        {
            return ChangeMachineStateResult.Failure(transition.Error!, command.Reason, command.TriggeredBy);
        }

        if (heldWorkOrder is not null)
        {
            await workOrderRepository.UpdateAsync(heldWorkOrder, cancellationToken).ConfigureAwait(false);
        }

        await equipmentRepository.UpdateAsync(equipment, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Equipment {EquipmentCode} state changed {PreviousState} → {NewState}. Reason: {Reason}. TriggeredBy: {TriggeredBy}. Confidence: {ConfidenceScore}. Decision: {DecisionReasoning}",
            equipment.EquipmentCode,
            previousState,
            equipment.State,
            command.Reason,
            command.TriggeredBy,
            command.ConfidenceScore,
            command.DecisionReasoning);

        return ChangeMachineStateResult.Success(
            equipment.EquipmentCode,
            previousState,
            equipment.State,
            command.Reason,
            command.TriggeredBy);
    }

    private async Task<(bool IsSuccess, string? Error, WorkOrder? WorkOrder)> HoldIfInProgressAsync(
        Guid workOrderId,
        CancellationToken cancellationToken)
    {
        var workOrder = await workOrderRepository
            .GetByIdAsync(workOrderId, cancellationToken)
            .ConfigureAwait(false);

        if (workOrder is null || workOrder.Status is not WorkOrderStatus.InProgress)
        {
            return (true, null, null);
        }

        var hold = workOrder.Hold();
        return hold.IsSuccess
            ? (true, null, workOrder)
            : (false, hold.Error, null);
    }

    private static DomainResult ApplyTransition(Equipment equipment, EquipmentState target, string reason) =>
        target switch
        {
            EquipmentState.Idle when equipment.State is EquipmentState.Running => equipment.Stop(),
            EquipmentState.Idle => equipment.Reset(),
            EquipmentState.Running => equipment.Start(equipment.CurrentWorkOrderId),
            EquipmentState.Faulted => equipment.Fault(reason),
            EquipmentState.Maintenance => equipment.EnterMaintenance(),
            EquipmentState.Setup => equipment.EnterSetup(),
            _ => DomainResult.Failure($"Unsupported equipment state '{target}'.")
        };

    /// <summary>Stopping, faulting, or locking out a machine is a high-risk plant-floor action.</summary>
    private static bool RequiresHitl(EquipmentState target) =>
        target is EquipmentState.Idle or EquipmentState.Faulted or EquipmentState.Maintenance;
}
