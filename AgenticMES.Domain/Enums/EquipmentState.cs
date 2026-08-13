namespace AgenticMES.Domain.Enums;

/// <summary>
/// Production equipment states used by the industrial state machine.
/// </summary>
public enum EquipmentState
{
    Idle,
    Running,
    Faulted,
    Maintenance,
    Setup
}
