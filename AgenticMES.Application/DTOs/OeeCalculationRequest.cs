using AgenticMES.Domain.Entities;
using AgenticMES.Domain.Enums;

namespace AgenticMES.Application.DTOs;

/// <summary>
/// Optional production facts that are not stored on <see cref="Equipment"/> / <see cref="WorkOrder"/>
/// but are required for a complete ISO-22400-style OEE calculation.
/// </summary>
public sealed record OeeCalculationParameters(
    decimal ScrapQuantity = 0m,
    TimeSpan? UnplannedDowntime = null,
    TimeSpan? PlannedDowntime = null,
    decimal? IdealCycleTimeSeconds = null,
    DateTimeOffset? AsOfUtc = null);

/// <summary>
/// Immutable snapshot of equipment and work-order parameters used to compute OEE.
/// </summary>
public sealed record OeeCalculationRequest(
    Guid EquipmentId,
    string EquipmentCode,
    EquipmentState EquipmentState,
    DateTimeOffset StateChangedAt,
    Guid WorkOrderId,
    string WorkOrderNumber,
    WorkOrderStatus WorkOrderStatus,
    Guid? AssignedEquipmentId,
    decimal PlannedQuantity,
    decimal ProducedQuantity,
    DateTimeOffset DueAt,
    DateTimeOffset? ReleasedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    OeeCalculationParameters? Parameters = null)
{
    public static OeeCalculationRequest From(
        Equipment equipment,
        WorkOrder workOrder,
        OeeCalculationParameters? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(equipment);
        ArgumentNullException.ThrowIfNull(workOrder);

        return new(
            equipment.Id,
            equipment.EquipmentCode,
            equipment.State,
            equipment.StateChangedAt,
            workOrder.Id,
            workOrder.WorkOrderNumber,
            workOrder.Status,
            workOrder.AssignedEquipmentId,
            workOrder.PlannedQuantity,
            workOrder.ProducedQuantity,
            workOrder.DueAt,
            workOrder.ReleasedAt,
            workOrder.StartedAt,
            workOrder.CompletedAt,
            parameters);
    }
}
