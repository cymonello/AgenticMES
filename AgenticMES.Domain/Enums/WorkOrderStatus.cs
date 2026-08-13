namespace AgenticMES.Domain.Enums;

/// <summary>
/// ISA-95 production / operations request lifecycle for a work order.
/// </summary>
public enum WorkOrderStatus
{
    Created,
    Released,
    InProgress,
    Held,
    Completed,
    Cancelled
}
