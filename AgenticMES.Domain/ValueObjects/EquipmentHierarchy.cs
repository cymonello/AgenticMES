namespace AgenticMES.Domain.ValueObjects;

/// <summary>
/// ISA-95 physical hierarchy identifiers for an equipment node.
/// </summary>
public readonly record struct EquipmentHierarchy(
    string? EnterpriseId,
    string? SiteId,
    string? AreaId,
    string? WorkCenterId);
