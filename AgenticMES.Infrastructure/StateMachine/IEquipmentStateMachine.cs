using AgenticMES.Domain.Common;
using AgenticMES.Domain.Entities;
using AgenticMES.Domain.Enums;

namespace AgenticMES.Infrastructure.StateMachine;

/// <summary>
/// Stateless-based state machine implementation for Equipment entity.
/// Provides a cleaner, trigger-based API with automatic validation and transition management.
/// </summary>
public interface IEquipmentStateMachine
{
    /// <summary>Gets the equipment this state machine manages.</summary>
    Equipment Equipment { get; }

    /// <summary>Checks if a trigger can be fired in the current state.</summary>
    bool CanFire(EquipmentTrigger trigger);

    /// <summary>Fires a trigger, causing a state transition if valid.</summary>
    DomainResult Fire(EquipmentTrigger trigger);

    /// <summary>Fires a parameterized trigger (e.g., Fault with reason).</summary>
    DomainResult Fire<TParam>(EquipmentTrigger trigger, TParam parameter);

    /// <summary>Generates a DOT graph representation for visualization.</summary>
    string ToDotGraph();
}
