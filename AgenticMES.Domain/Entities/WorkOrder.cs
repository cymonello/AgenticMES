using AgenticMES.Domain.Common;
using AgenticMES.Domain.Enums;

namespace AgenticMES.Domain.Entities;

/// <summary>
/// ISA-95 Operations Request / production work order dispatched to a WorkCenter or Equipment.
/// </summary>
public sealed class WorkOrder(
    Guid id,
    string workOrderNumber,
    string productCode,
    decimal plannedQuantity,
    DateTimeOffset dueAt,
    int priority = 0)
{
    public Guid Id { get; } = id;

    /// <summary>Plant-floor operations request identifier.</summary>
    public string WorkOrderNumber { get; } = Require(workOrderNumber, nameof(workOrderNumber));

    /// <summary>ISA-95 Material Definition ID of the product to produce.</summary>
    public string ProductCode { get; } = Require(productCode, nameof(productCode));

    public decimal PlannedQuantity { get; } = plannedQuantity > 0
        ? plannedQuantity
        : throw new ArgumentOutOfRangeException(nameof(plannedQuantity), "Planned quantity must be greater than zero.");

    public decimal ProducedQuantity { get; private set; }

    public DateTimeOffset DueAt { get; } = dueAt;

    public int Priority { get; private set; } = priority;

    public WorkOrderStatus Status { get; private set; } = WorkOrderStatus.Created;

    public Guid? AssignedEquipmentId { get; private set; }

    public DateTimeOffset? ReleasedAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public DomainResult Release()
    {
        if (Status is not WorkOrderStatus.Created)
        {
            return DomainResult.Failure($"Work order {WorkOrderNumber} cannot be released from {Status}.");
        }

        Status = WorkOrderStatus.Released;
        ReleasedAt = DateTimeOffset.UtcNow;
        return DomainResult.Success();
    }

    public DomainResult DispatchTo(Guid equipmentId)
    {
        if (Status is WorkOrderStatus.Completed or WorkOrderStatus.Cancelled)
        {
            return DomainResult.Failure($"Work order {WorkOrderNumber} is {Status} and cannot be dispatched.");
        }

        AssignedEquipmentId = equipmentId;
        return DomainResult.Success();
    }

    public DomainResult Start()
    {
        if (Status is not (WorkOrderStatus.Released or WorkOrderStatus.Held))
        {
            return DomainResult.Failure($"Work order {WorkOrderNumber} cannot start from {Status}.");
        }

        if (AssignedEquipmentId is null)
        {
            return DomainResult.Failure($"Work order {WorkOrderNumber} has no assigned equipment.");
        }

        Status = WorkOrderStatus.InProgress;
        StartedAt ??= DateTimeOffset.UtcNow;
        return DomainResult.Success();
    }

    public DomainResult ReportProduction(decimal quantity)
    {
        if (quantity <= 0)
        {
            return DomainResult.Failure("Reported quantity must be greater than zero.");
        }

        if (Status is not WorkOrderStatus.InProgress)
        {
            return DomainResult.Failure($"Work order {WorkOrderNumber} is not in progress.");
        }

        ProducedQuantity += quantity;
        return DomainResult.Success();
    }

    public DomainResult Hold()
    {
        if (Status is not WorkOrderStatus.InProgress)
        {
            return DomainResult.Failure($"Work order {WorkOrderNumber} cannot be held from {Status}.");
        }

        Status = WorkOrderStatus.Held;
        return DomainResult.Success();
    }

    public DomainResult Complete()
    {
        if (Status is not WorkOrderStatus.InProgress)
        {
            return DomainResult.Failure($"Work order {WorkOrderNumber} cannot be completed from {Status}.");
        }

        Status = WorkOrderStatus.Completed;
        CompletedAt = DateTimeOffset.UtcNow;
        return DomainResult.Success();
    }

    public DomainResult Cancel()
    {
        if (Status is WorkOrderStatus.Completed)
        {
            return DomainResult.Failure($"Completed work order {WorkOrderNumber} cannot be cancelled.");
        }

        Status = WorkOrderStatus.Cancelled;
        return DomainResult.Success();
    }

    private static string Require(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        return value.Trim();
    }
}
