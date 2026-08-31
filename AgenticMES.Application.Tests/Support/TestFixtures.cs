using AgenticMES.Application.DTOs;
using AgenticMES.Domain.Entities;
using AgenticMES.Domain.Enums;
using AgenticMES.Domain.ValueObjects;

namespace AgenticMES.Application.Tests.Support;

internal static class TestFixtures
{
    public static readonly DateTimeOffset AsOf =
        new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    public static EquipmentHierarchy DefaultHierarchy { get; } =
        new("ENT-1", "SITE-1", "AREA-1", "WC-1");

    public static Equipment CreateEquipment(
        string code = "CNC-01",
        string name = "CNC Mill 01",
        EquipmentLevel level = EquipmentLevel.Equipment,
        Guid? id = null,
        Guid? parentId = null,
        EquipmentState state = EquipmentState.Idle,
        Guid? currentWorkOrderId = null,
        string? lastFaultReason = null,
        EquipmentHierarchy? hierarchy = null) =>
        new(
            id ?? Guid.NewGuid(),
            code,
            name,
            level,
            hierarchy ?? DefaultHierarchy,
            parentId,
            state,
            currentWorkOrderId,
            lastFaultReason);

    public static WorkOrder CreateWorkOrder(
        string number = "WO-2024-001",
        string productCode = "WIDGET-A",
        decimal plannedQuantity = 100m,
        Guid? id = null) =>
        new(
            id ?? Guid.NewGuid(),
            number,
            productCode,
            plannedQuantity,
            AsOf.AddDays(1));

    public static OeeCalculationRequest CreateOeeRequest(
        Guid? equipmentId = null,
        string equipmentCode = "CNC-01",
        EquipmentState equipmentState = EquipmentState.Running,
        DateTimeOffset? stateChangedAt = null,
        Guid? workOrderId = null,
        string workOrderNumber = "WO-2024-001",
        Guid? assignedEquipmentId = null,
        decimal plannedQuantity = 100m,
        decimal producedQuantity = 100m,
        DateTimeOffset? releasedAt = null,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? completedAt = null,
        OeeCalculationParameters? parameters = null)
    {
        var eqId = equipmentId ?? Guid.NewGuid();
        var start = startedAt ?? AsOf.AddHours(-1);

        return new OeeCalculationRequest(
            eqId,
            equipmentCode,
            equipmentState,
            stateChangedAt ?? start,
            workOrderId ?? Guid.NewGuid(),
            workOrderNumber,
            WorkOrderStatus.InProgress,
            assignedEquipmentId ?? eqId,
            plannedQuantity,
            producedQuantity,
            AsOf.AddHours(2),
            releasedAt ?? start.AddMinutes(-10),
            start,
            completedAt,
            parameters ?? new OeeCalculationParameters(AsOfUtc: AsOf));
    }
}
