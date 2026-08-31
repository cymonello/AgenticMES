using AgenticMES.Domain.Common;
using AgenticMES.Domain.Entities;
using AgenticMES.Domain.Enums;

namespace AgenticMES.Application.Common.Interfaces;

/// <summary>
/// Creates a Stateless-backed state machine bound to an equipment aggregate.
/// </summary>
public interface IEquipmentStateMachineFactory
{
    IEquipmentStateMachine Create(Equipment equipment);
}

/// <summary>
/// Trigger-based ISA-95 equipment transitions. Application code must not call
/// <see cref="Equipment.ApplyState"/> directly.
/// </summary>
public interface IEquipmentStateMachine
{
    Equipment Equipment { get; }

    bool CanFire(EquipmentTrigger trigger);

    DomainResult Fire(EquipmentTrigger trigger);

    /// <summary>Fires <see cref="EquipmentTrigger.Fault"/> with a recorded reason.</summary>
    DomainResult Fire(EquipmentTrigger trigger, string parameter);
}
