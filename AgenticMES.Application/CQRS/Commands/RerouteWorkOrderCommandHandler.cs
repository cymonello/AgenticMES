using AgenticMES.Application.Common.Interfaces;
using AgenticMES.Domain.Entities;
using AgenticMES.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AgenticMES.Application.CQRS.Commands;

public sealed class RerouteWorkOrderCommandHandler(
    IEquipmentRepository equipmentRepository,
    IWorkOrderRepository workOrderRepository,
    IHitlApprovalService hitlApprovalService,
    IEquipmentStateMachineFactory stateMachineFactory,
    ILogger<RerouteWorkOrderCommandHandler> logger)
    : ICommandHandler<RerouteWorkOrderCommand, RerouteWorkOrderResult>
{
    public async Task<RerouteWorkOrderResult> HandleAsync(
        RerouteWorkOrderCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var auditError = CommandGuards.ValidateAudit(command.Reason, command.TriggeredBy, command.ConfidenceScore);
        if (auditError is not null)
        {
            return RerouteWorkOrderResult.Failure(auditError, command.Reason, command.TriggeredBy);
        }

        if (command.SourceEquipmentId == command.TargetEquipmentId)
        {
            return RerouteWorkOrderResult.Failure(
                "Source and target equipment must be different.",
                command.Reason,
                command.TriggeredBy);
        }

        var workOrder = await workOrderRepository
            .GetByIdAsync(command.WorkOrderId, cancellationToken)
            .ConfigureAwait(false);

        if (workOrder is null)
        {
            return RerouteWorkOrderResult.Failure(
                $"Work order {command.WorkOrderId} was not found.",
                command.Reason,
                command.TriggeredBy);
        }

        var source = await equipmentRepository
            .GetByIdAsync(command.SourceEquipmentId, cancellationToken)
            .ConfigureAwait(false);

        var target = await equipmentRepository
            .GetByIdAsync(command.TargetEquipmentId, cancellationToken)
            .ConfigureAwait(false);

        if (source is null)
        {
            return RerouteWorkOrderResult.Failure(
                $"Source equipment {command.SourceEquipmentId} was not found.",
                command.Reason,
                command.TriggeredBy);
        }

        if (target is null)
        {
            return RerouteWorkOrderResult.Failure(
                $"Target equipment {command.TargetEquipmentId} was not found.",
                command.Reason,
                command.TriggeredBy);
        }

        var validationError = ValidateReroute(workOrder, source, target);
        if (validationError is not null)
        {
            return RerouteWorkOrderResult.Failure(validationError, command.Reason, command.TriggeredBy);
        }

        if (!CommandGuards.IsApproved(command.ApprovalStatus))
        {
            logger.LogInformation(
                "RerouteWorkOrder pending approval for {WorkOrderNumber}: {Remaining} remaining units from {Source} to {Target}. Reason: {Reason}. TriggeredBy: {TriggeredBy}",
                workOrder.WorkOrderNumber,
                workOrder.RemainingQuantity,
                source.EquipmentCode,
                target.EquipmentCode,
                command.Reason,
                command.TriggeredBy);

            var description = $"Reroute {workOrder.WorkOrderNumber}: {source.EquipmentCode} → {target.EquipmentCode} ({workOrder.RemainingQuantity} units)";
            hitlApprovalService.RegisterPendingAction(new PendingApprovalRequest(
                RequestId: Guid.NewGuid(),
                ActionType: ActionType.RerouteWorkOrder,
                Description: description,
                Reason: command.Reason,
                DecisionReasoning: command.DecisionReasoning ?? "Not provided",
                ConfidenceScore: command.ConfidenceScore ?? 0m,
                RequestedAt: DateTimeOffset.UtcNow,
                CommandData: command));

            return RerouteWorkOrderResult.Pending(
                workOrder.WorkOrderNumber,
                source.EquipmentCode,
                target.EquipmentCode,
                workOrder.RemainingQuantity,
                workOrder.ProducedQuantity,
                command.Reason,
                command.TriggeredBy);
        }

        if (workOrder.Status is WorkOrderStatus.InProgress)
        {
            var hold = workOrder.Hold();
            if (!hold.IsSuccess)
            {
                return RerouteWorkOrderResult.Failure(hold.Error!, command.Reason, command.TriggeredBy);
            }
        }

        var sourceStateMachine = stateMachineFactory.Create(source);
        var releaseSource = source.State is EquipmentState.Running
            ? sourceStateMachine.Fire(EquipmentTrigger.Stop)
            : source.ClearWorkOrderAssignment();

        if (!releaseSource.IsSuccess)
        {
            return RerouteWorkOrderResult.Failure(releaseSource.Error!, command.Reason, command.TriggeredBy);
        }

        var dispatch = workOrder.DispatchTo(target.Id);
        if (!dispatch.IsSuccess)
        {
            return RerouteWorkOrderResult.Failure(dispatch.Error!, command.Reason, command.TriggeredBy);
        }

        var assign = target.AssignWorkOrder(workOrder.Id);
        if (!assign.IsSuccess)
        {
            return RerouteWorkOrderResult.Failure(assign.Error!, command.Reason, command.TriggeredBy);
        }

        await workOrderRepository.UpdateAsync(workOrder, cancellationToken).ConfigureAwait(false);
        await equipmentRepository.UpdateAsync(source, cancellationToken).ConfigureAwait(false);
        await equipmentRepository.UpdateAsync(target, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Rerouted work order {WorkOrderNumber}: {Remaining} remaining units from {Source} to {Target}. Produced so far: {Produced}. Reason: {Reason}. TriggeredBy: {TriggeredBy}. Confidence: {ConfidenceScore}. Decision: {DecisionReasoning}",
            workOrder.WorkOrderNumber,
            workOrder.RemainingQuantity,
            source.EquipmentCode,
            target.EquipmentCode,
            workOrder.ProducedQuantity,
            command.Reason,
            command.TriggeredBy,
            command.ConfidenceScore,
            command.DecisionReasoning);

        return RerouteWorkOrderResult.Success(
            workOrder.WorkOrderNumber,
            source.EquipmentCode,
            target.EquipmentCode,
            workOrder.RemainingQuantity,
            workOrder.ProducedQuantity,
            command.Reason,
            command.TriggeredBy);
    }

    private static string? ValidateReroute(WorkOrder workOrder, Equipment source, Equipment target)
    {
        if (workOrder.Status is WorkOrderStatus.Completed or WorkOrderStatus.Cancelled or WorkOrderStatus.Created)
        {
            return $"Work order {workOrder.WorkOrderNumber} is {workOrder.Status} and cannot be rerouted.";
        }

        if (workOrder.AssignedEquipmentId != source.Id)
        {
            return $"Work order {workOrder.WorkOrderNumber} is not dispatched to {source.EquipmentCode}.";
        }

        if (workOrder.RemainingQuantity <= 0m)
        {
            return $"Work order {workOrder.WorkOrderNumber} has no remaining quantity to reroute.";
        }

        if (target.State is EquipmentState.Faulted or EquipmentState.Maintenance)
        {
            return $"{target.EquipmentCode} is {target.State} and cannot accept a work order.";
        }

        if (target.CurrentWorkOrderId is { } busy && busy != workOrder.Id)
        {
            return $"{target.EquipmentCode} already has another work order assigned.";
        }

        return null;
    }
}
