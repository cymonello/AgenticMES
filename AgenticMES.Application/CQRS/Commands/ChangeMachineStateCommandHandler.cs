using AgenticMES.Application.Common.Interfaces;
using AgenticMES.Domain.Entities;
using AgenticMES.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AgenticMES.Application.CQRS.Commands;

public sealed class ChangeMachineStateCommandHandler(
    IEquipmentRepository equipmentRepository,
    IWorkOrderRepository workOrderRepository,
    IHitlApprovalService hitlApprovalService,
    IEquipmentStateMachineFactory stateMachineFactory,
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

            var description = $"Set {equipment.EquipmentCode} state: {equipment.State} → {command.TargetState}";
            hitlApprovalService.RegisterPendingAction(new PendingApprovalRequest(
                RequestId: Guid.NewGuid(),
                ActionType: ActionType.SetMachineState,
                Description: description,
                Reason: command.Reason,
                DecisionReasoning: command.DecisionReasoning ?? "Not provided",
                ConfidenceScore: command.ConfidenceScore ?? 0m,
                RequestedAt: DateTimeOffset.UtcNow,
                CommandData: command));

            return ChangeMachineStateResult.Pending(
                equipment.EquipmentCode,
                equipment.State,
                command.TargetState,
                command.Reason,
                command.TriggeredBy);
        }

        var stateMachine = stateMachineFactory.Create(equipment);
        var trigger = MapStateToTrigger(equipment.State, command.TargetState);
        
        if (trigger is null || !stateMachine.CanFire(trigger.Value))
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

        var transition = trigger.Value is EquipmentTrigger.Fault
            ? stateMachine.Fire(trigger.Value, command.Reason)
            : stateMachine.Fire(trigger.Value);
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

    private static EquipmentTrigger? MapStateToTrigger(EquipmentState currentState, EquipmentState targetState) =>
        (currentState, targetState) switch
        {
            (_, _) when currentState == targetState => null,
            (EquipmentState.Running, EquipmentState.Idle) => EquipmentTrigger.Stop,
            (_, EquipmentState.Idle) => EquipmentTrigger.Reset,
            (EquipmentState.Setup, EquipmentState.Running) => EquipmentTrigger.CompleteSetup,
            (_, EquipmentState.Running) => EquipmentTrigger.Start,
            (_, EquipmentState.Faulted) => EquipmentTrigger.Fault,
            (_, EquipmentState.Maintenance) => EquipmentTrigger.EnterMaintenance,
            (_, EquipmentState.Setup) => EquipmentTrigger.EnterSetup,
            _ => null
        };

    /// <summary>Stopping, faulting, or locking out a machine is a high-risk plant-floor action.</summary>
    private static bool RequiresHitl(EquipmentState target) =>
        target is EquipmentState.Idle or EquipmentState.Faulted or EquipmentState.Maintenance;
}
