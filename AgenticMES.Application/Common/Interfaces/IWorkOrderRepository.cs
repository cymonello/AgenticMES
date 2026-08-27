using AgenticMES.Domain.Entities;
using AgenticMES.Domain.Enums;

namespace AgenticMES.Application.Common.Interfaces;

/// <summary>
/// Persistence contract for ISA-95 production work orders (operations requests).
/// Implementations live in Infrastructure; Application services depend only on this interface.
/// </summary>
public interface IWorkOrderRepository
{
    /// <summary>Loads a single work order by aggregate identity.</summary>
    Task<WorkOrder?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a work order by plant-floor operations request number.
    /// Used when dispatch, tracking, or AI tools look up an order by its shop-floor identifier.
    /// </summary>
    Task<WorkOrder?> GetByNumberAsync(string workOrderNumber, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkOrder>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns work orders in the given lifecycle status (Released, InProgress, Held, etc.).</summary>
    Task<IReadOnlyList<WorkOrder>> ListByStatusAsync(WorkOrderStatus status, CancellationToken cancellationToken = default);

    /// <summary>Returns work orders currently dispatched to the specified equipment.</summary>
    Task<IReadOnlyList<WorkOrder>> ListByAssignedEquipmentAsync(Guid equipmentId, CancellationToken cancellationToken = default);

    Task AddAsync(WorkOrder workOrder, CancellationToken cancellationToken = default);

    Task UpdateAsync(WorkOrder workOrder, CancellationToken cancellationToken = default);
}
