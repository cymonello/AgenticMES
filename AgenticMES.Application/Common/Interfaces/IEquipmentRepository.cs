using AgenticMES.Domain.Entities;
using AgenticMES.Domain.Enums;

namespace AgenticMES.Application.Common.Interfaces;

/// <summary>
/// Persistence contract for ISA-95 Equipment (physical assets / machines).
/// Implementations live in Infrastructure; Application services depend only on this interface.
/// </summary>
public interface IEquipmentRepository
{
    /// <summary>Loads a single equipment node by aggregate identity.</summary>
    Task<Equipment?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves equipment by plant-floor ISA-95 equipment code (e.g. CNC-03).
    /// Used when operators or AI agents refer to a machine by its shop-floor identifier.
    /// </summary>
    Task<Equipment?> GetByCodeAsync(string equipmentCode, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Equipment>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns equipment currently in the given industrial state (Idle, Running, Faulted, etc.).</summary>
    Task<IReadOnlyList<Equipment>> ListByStateAsync(EquipmentState state, CancellationToken cancellationToken = default);

    /// <summary>Returns child equipment under a parent node (WorkCenter → Equipment hierarchy walk).</summary>
    Task<IReadOnlyList<Equipment>> ListByParentAsync(Guid parentEquipmentId, CancellationToken cancellationToken = default);

    Task AddAsync(Equipment equipment, CancellationToken cancellationToken = default);

    Task UpdateAsync(Equipment equipment, CancellationToken cancellationToken = default);
}
