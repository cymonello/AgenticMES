namespace AgenticMES.Domain.Enums;

/// <summary>
/// Triggers for ISA-95 equipment state transitions.
/// These represent business actions that cause state changes.
/// </summary>
public enum EquipmentTrigger
{
    /// <summary>Start production (Idle/Setup → Running).</summary>
    Start,

    /// <summary>Stop production gracefully (Running → Idle).</summary>
    Stop,

    /// <summary>Enter fault state due to equipment failure.</summary>
    Fault,

    /// <summary>Enter setup/changeover mode.</summary>
    EnterSetup,

    /// <summary>Enter maintenance mode (planned or reactive).</summary>
    EnterMaintenance,

    /// <summary>Reset from fault or complete maintenance/setup back to Idle.</summary>
    Reset,

    /// <summary>Complete setup and transition to Running.</summary>
    CompleteSetup
}
