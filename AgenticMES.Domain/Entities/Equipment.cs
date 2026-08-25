using AgenticMES.Domain.Common;
using AgenticMES.Domain.Enums;
using AgenticMES.Domain.ValueObjects;

namespace AgenticMES.Domain.Entities;

/// <summary>
/// ISA-95 Equipment (physical asset / machine) with an industrial state machine.
/// </summary>
public sealed class Equipment(
    Guid id,
    string equipmentCode,
    string name,
    EquipmentLevel level,
    EquipmentHierarchy hierarchy,
    Guid? parentEquipmentId = null,
    EquipmentState state = EquipmentState.Idle,
    Guid? currentWorkOrderId = null)
{
    public Guid Id { get; } = id;

    /// <summary>ISA-95 Equipment ID (plant-floor code, e.g. CNC-03).</summary>
    public string EquipmentCode { get; } = Require(equipmentCode, nameof(equipmentCode));

    public string Name { get; private set; } = Require(name, nameof(name));

    public EquipmentLevel Level { get; } = level;

    public EquipmentHierarchy Hierarchy { get; private set; } = hierarchy;

    public Guid? ParentEquipmentId { get; } = parentEquipmentId;

    public EquipmentState State { get; private set; } = state;

    public Guid? CurrentWorkOrderId { get; private set; } = currentWorkOrderId;

    public string? LastFaultReason { get; private set; }

    public DateTimeOffset StateChangedAt { get; private set; } = DateTimeOffset.UtcNow;

    public bool CanTransitionTo(EquipmentState target) => (State, target) switch
    {
        (_, _) when State == target => false,
        (EquipmentState.Idle, EquipmentState.Running or EquipmentState.Setup or EquipmentState.Maintenance or EquipmentState.Faulted) => true,
        (EquipmentState.Running, EquipmentState.Idle or EquipmentState.Faulted or EquipmentState.Maintenance or EquipmentState.Setup) => true,
        (EquipmentState.Faulted, EquipmentState.Idle or EquipmentState.Maintenance) => true,
        (EquipmentState.Maintenance, EquipmentState.Idle) => true,
        (EquipmentState.Setup, EquipmentState.Idle or EquipmentState.Running or EquipmentState.Faulted) => true,
        _ => false
    };

    public DomainResult Start(Guid? workOrderId = null)
    {
        var result = TryTransitionTo(EquipmentState.Running);
        if (result.IsSuccess && workOrderId is not null)
        {
            CurrentWorkOrderId = workOrderId;
        }

        return result;
    }

    public DomainResult Stop()
    {
        var result = TryTransitionTo(EquipmentState.Idle);
        if (result.IsSuccess)
        {
            CurrentWorkOrderId = null;
            LastFaultReason = null;
        }

        return result;
    }

    public DomainResult Fault(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var result = TryTransitionTo(EquipmentState.Faulted);
        if (result.IsSuccess)
        {
            LastFaultReason = reason.Trim();
        }

        return result;
    }

    public DomainResult EnterSetup() => TryTransitionTo(EquipmentState.Setup);

    public DomainResult EnterMaintenance() => TryTransitionTo(EquipmentState.Maintenance);

    public DomainResult Reset()
    {
        var result = TryTransitionTo(EquipmentState.Idle);
        if (result.IsSuccess)
        {
            LastFaultReason = null;
            CurrentWorkOrderId = null;
        }

        return result;
    }

    public DomainResult AssignWorkOrder(Guid workOrderId)
    {
        if (State is EquipmentState.Faulted or EquipmentState.Maintenance)
        {
            return DomainResult.Failure($"Cannot dispatch a work order while {EquipmentCode} is {State}.");
        }

        CurrentWorkOrderId = workOrderId;
        return DomainResult.Success();
    }

    public DomainResult TryTransitionTo(EquipmentState target)
    {
        if (!CanTransitionTo(target))
        {
            return DomainResult.Failure($"Illegal ISA-95 equipment transition: {State} → {target} on {EquipmentCode}.");
        }

        State = target;
        StateChangedAt = DateTimeOffset.UtcNow;
        if (target is not EquipmentState.Faulted)
        {
            LastFaultReason = null;
        }

        return DomainResult.Success();
    }

    private static string Require(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        return value.Trim();
    }
}
