namespace AgenticMES.Domain.Enums;

/// <summary>
/// ISA-95 equipment hierarchy: Enterprise → Site → Area → WorkCenter → Equipment.
/// </summary>
public enum EquipmentLevel
{
    Enterprise = 0,
    Site = 1,
    Area = 2,
    WorkCenter = 3,
    Equipment = 4
}
