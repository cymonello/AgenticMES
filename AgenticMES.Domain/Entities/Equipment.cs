using AgenticMES.Domain.Common;
using AgenticMES.Domain.Enums;
using AgenticMES.Domain.ValueObjects;

namespace AgenticMES.Domain.Entities;

/// <summary>
/// ISA-95 Equipment (physical asset / machine). Industrial transitions are owned by the
/// Infrastructure state machine; this entity only stores state and assignment side effects.
/// </summary>
public sealed class Equipment(
    Guid id,
    string equipmentCode,
    string name,
    EquipmentLevel level,
    EquipmentHierarchy hierarchy,
    Guid? parentEquipmentId = null,
    EquipmentState state = EquipmentState.Idle,
    Guid? currentWorkOrderId = null,
    string? lastFaultReason = null)
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

    public string? LastFaultReason { get; private set; } =
        state is EquipmentState.Faulted ? lastFaultReason?.Trim() : null;

    public DateTimeOffset StateChangedAt { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Applies a state already accepted by the equipment state machine.
    /// Does not re-validate ISA-95 transitions.
    /// </summary>
    public void ApplyState(EquipmentState target, string? faultReason = null)
    {
        State = target;
        StateChangedAt = DateTimeOffset.UtcNow;

        if (target is EquipmentState.Faulted)
        {
            if (!string.IsNullOrWhiteSpace(faultReason))
            {
                LastFaultReason = faultReason.Trim();
            }
        }
        else
        {
            LastFaultReason = null;
        }

        if (target is EquipmentState.Idle)
        {
            CurrentWorkOrderId = null;
        }
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

    /// <summary>
    /// Drops the current operations request without changing industrial state.
    /// Used when rerouting a work order off a Faulted or Idle machine.
    /// </summary>
    public DomainResult ClearWorkOrderAssignment()
    {
        CurrentWorkOrderId = null;
        return DomainResult.Success();
    }

    private static string Require(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        return value.Trim();
    }
}
