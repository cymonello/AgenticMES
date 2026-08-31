using AgenticMES.Domain.Common;
using AgenticMES.Domain.Entities;
using AgenticMES.Domain.Enums;

namespace AgenticMES.Application.Common.Interfaces;

/// <summary>
/// Factory for creating Equipment state machines.
/// Implementation in Infrastructure layer uses Stateless framework.
/// </summary>
public interface IEquipmentStateMachineFactory
{
    /// <summary>
    /// Creates a state machine for the given equipment entity.
    /// </summary>
    IEquipmentStateMachineWrapper Create(Equipment equipment);
}

/// <summary>
/// Wrapper interface for equipment state machine operations.
/// Keeps Application layer independent of Stateless framework.
/// </summary>
public interface IEquipmentStateMachineWrapper
{
    /// <summary>Gets the equipment this state machine manages.</summary>
    Equipment Equipment { get; }

    /// <summary>Checks if a trigger can be fired in the current state.</summary>
    bool CanFire(EquipmentTrigger trigger);

    /// <summary>Fires a trigger, causing a state transition if valid.</summary>
    DomainResult Fire(EquipmentTrigger trigger);

    /// <summary>Fires a parameterized trigger (e.g., Fault with reason).</summary>
    DomainResult Fire(EquipmentTrigger trigger, string parameter);
}
