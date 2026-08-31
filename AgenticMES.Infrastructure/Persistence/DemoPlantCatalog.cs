using AgenticMES.Domain.Common;
using AgenticMES.Domain.Entities;
using AgenticMES.Domain.Enums;
using AgenticMES.Domain.ValueObjects;

namespace AgenticMES.Infrastructure.Persistence;

/// <summary>
/// Stable ISA-95 demo plant: Krakow machining cell with three machines and a mixed work-order queue.
/// Shared by in-memory repositories (and telemetry) so shop-floor identities stay consistent.
/// </summary>
public sealed class DemoPlantCatalog
{
    public static readonly Guid EnterpriseId = new("a0000001-0000-4000-8000-000000000001");
    public static readonly Guid SiteKrakowId = new("a0000001-0000-4000-8000-000000000002");
    public static readonly Guid AreaMachiningId = new("a0000001-0000-4000-8000-000000000003");
    public static readonly Guid WorkCenterCncId = new("a0000001-0000-4000-8000-000000000004");

    public static readonly Guid Cnc01Id = new("a0000001-0000-4000-8000-000000000011");
    public static readonly Guid Cnc02Id = new("a0000001-0000-4000-8000-000000000012");
    public static readonly Guid Cnc03Id = new("a0000001-0000-4000-8000-000000000013");

    public static readonly Guid Wo1001Id = new("b0000001-0000-4000-8000-000000000001");
    public static readonly Guid Wo1002Id = new("b0000001-0000-4000-8000-000000000002");
    public static readonly Guid Wo1003Id = new("b0000001-0000-4000-8000-000000000003");
    public static readonly Guid Wo1004Id = new("b0000001-0000-4000-8000-000000000004");
    public static readonly Guid Wo1005Id = new("b0000001-0000-4000-8000-000000000005");

    /// <summary>Leaf machines used by the telemetry mock (CNC-01 / CNC-02 / CNC-03).</summary>
    public static IReadOnlyList<Guid> MachineIds { get; } = [Cnc01Id, Cnc02Id, Cnc03Id];

    /// <summary>Process-wide default plant. Repositories share these aggregate instances.</summary>
    public static DemoPlantCatalog Default { get; } = Create();

    public IReadOnlyList<Equipment> Equipment { get; }

    public IReadOnlyList<WorkOrder> WorkOrders { get; }

    private DemoPlantCatalog(IReadOnlyList<Equipment> equipment, IReadOnlyList<WorkOrder> workOrders)
    {
        Equipment = equipment;
        WorkOrders = workOrders;
    }

    public static DemoPlantCatalog Create()
    {
        var now = DateTimeOffset.UtcNow;
        var acme = new EquipmentHierarchy("ENT-ACME", null, null, null);
        var krakow = new EquipmentHierarchy("ENT-ACME", "SITE-KRK", null, null);
        var machining = new EquipmentHierarchy("ENT-ACME", "SITE-KRK", "AREA-MACH", null);
        var cncCell = new EquipmentHierarchy("ENT-ACME", "SITE-KRK", "AREA-MACH", "WC-CNC-01");

        var enterprise = new Equipment(EnterpriseId, "ENT-ACME", "ACME Manufacturing Group", EquipmentLevel.Enterprise, acme);
        var site = new Equipment(SiteKrakowId, "SITE-KRK", "Krakow Plant", EquipmentLevel.Site, krakow, EnterpriseId);
        var area = new Equipment(AreaMachiningId, "AREA-MACH", "Machining Area", EquipmentLevel.Area, machining, SiteKrakowId);
        var workCenter = new Equipment(WorkCenterCncId, "WC-CNC-01", "CNC Cell 1", EquipmentLevel.WorkCenter, cncCell, AreaMachiningId);

        var wo1001 = new WorkOrder(Wo1001Id, "WO-1001", "SHAFT-A", 600m, now.AddHours(8), priority: 5);
        Ensure(wo1001.Release());
        Ensure(wo1001.DispatchTo(Cnc01Id));
        Ensure(wo1001.Start());
        Ensure(wo1001.ReportProduction(80m));

        var wo1002 = new WorkOrder(Wo1002Id, "WO-1002", "HOUSING-B", 100m, now.AddDays(1), priority: 3);
        Ensure(wo1002.Release());

        var wo1003 = new WorkOrder(Wo1003Id, "WO-1003", "SHAFT-A", 120m, now.AddHours(4), priority: 10);
        Ensure(wo1003.Release());
        Ensure(wo1003.DispatchTo(Cnc03Id));
        Ensure(wo1003.Start());
        Ensure(wo1003.ReportProduction(18m));
        Ensure(wo1003.Hold());

        var wo1004 = new WorkOrder(Wo1004Id, "WO-1004", "FLANGE-C", 50m, now.AddDays(3));

        var wo1005 = new WorkOrder(Wo1005Id, "WO-1005", "HOUSING-B", 200m, now.AddHours(-2), priority: 1);
        Ensure(wo1005.Release());
        Ensure(wo1005.DispatchTo(Cnc02Id));
        Ensure(wo1005.Start());
        Ensure(wo1005.ReportProduction(200m));
        Ensure(wo1005.Complete());

        var cnc01 = new Equipment(
            Cnc01Id,
            "CNC-01",
            "Haas ST-20Y CNC Lathe",
            EquipmentLevel.Equipment,
            cncCell,
            WorkCenterCncId,
            EquipmentState.Running,
            wo1001.Id);

        var cnc02 = new Equipment(Cnc02Id, "CNC-02", "Haas VF-2SS Vertical Mill", EquipmentLevel.Equipment, cncCell, WorkCenterCncId);

        var cnc03 = new Equipment(
            Cnc03Id,
            "CNC-03",
            "Haas VF-3 Vertical Mill",
            EquipmentLevel.Equipment,
            cncCell,
            WorkCenterCncId,
            EquipmentState.Faulted,
            wo1003.Id,
            "Spindle over-temperature trip");

        return new(
            [enterprise, site, area, workCenter, cnc01, cnc02, cnc03],
            [wo1001, wo1002, wo1003, wo1004, wo1005]);
    }

    private static void Ensure(DomainResult result)
    {
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(result.Error);
        }
    }
}
