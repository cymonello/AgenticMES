using System.Collections.Concurrent;
using AgenticMES.Application.Common.Interfaces;
using AgenticMES.Domain.Entities;
using AgenticMES.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AgenticMES.Infrastructure.Persistence;

/// <summary>
/// Process-local operations-request store seeded from <see cref="DemoPlantCatalog"/>.
/// </summary>
public sealed class InMemoryWorkOrderRepository : IWorkOrderRepository
{
    private readonly ConcurrentDictionary<Guid, WorkOrder> _items = new();
    private readonly ILogger<InMemoryWorkOrderRepository> _logger;

    public InMemoryWorkOrderRepository(
        ILogger<InMemoryWorkOrderRepository> logger,
        DemoPlantCatalog? catalog = null)
    {
        _logger = logger;
        catalog ??= DemoPlantCatalog.Default;

        foreach (var workOrder in catalog.WorkOrders)
        {
            _items[workOrder.Id] = workOrder;
        }

        _logger.LogInformation(
            "In-memory work-order store seeded with {Count} operations requests.",
            _items.Count);
    }

    public Task<WorkOrder?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _items.TryGetValue(id, out var workOrder);
        return Task.FromResult(workOrder);
    }

    public Task<WorkOrder?> GetByNumberAsync(string workOrderNumber, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(workOrderNumber);

        var match = _items.Values.FirstOrDefault(w =>
            w.WorkOrderNumber.Equals(workOrderNumber.Trim(), StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(match);
    }

    public Task<IReadOnlyList<WorkOrder>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<WorkOrder>>(Snapshot());
    }

    public Task<IReadOnlyList<WorkOrder>> ListByStatusAsync(WorkOrderStatus status, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<WorkOrder>>(
            [.. Snapshot().Where(w => w.Status == status)]);
    }

    public Task<IReadOnlyList<WorkOrder>> ListByAssignedEquipmentAsync(Guid equipmentId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<WorkOrder>>(
            [.. Snapshot().Where(w => w.AssignedEquipmentId == equipmentId)]);
    }

    public Task AddAsync(WorkOrder workOrder, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(workOrder);

        if (!_items.TryAdd(workOrder.Id, workOrder))
        {
            throw new InvalidOperationException($"Work order {workOrder.Id} already exists.");
        }

        var duplicateNumber = _items.Values.Any(w =>
            w.Id != workOrder.Id
            && w.WorkOrderNumber.Equals(workOrder.WorkOrderNumber, StringComparison.OrdinalIgnoreCase));
        if (duplicateNumber)
        {
            _items.TryRemove(workOrder.Id, out _);
            throw new InvalidOperationException($"Work order number '{workOrder.WorkOrderNumber}' already exists.");
        }

        _logger.LogDebug("Added work order {WorkOrderNumber}.", workOrder.WorkOrderNumber);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(WorkOrder workOrder, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(workOrder);

        if (!_items.ContainsKey(workOrder.Id))
        {
            throw new InvalidOperationException($"Work order {workOrder.Id} was not found.");
        }

        _items[workOrder.Id] = workOrder;
        return Task.CompletedTask;
    }

    private WorkOrder[] Snapshot() =>
        [.. _items.Values.OrderByDescending(w => w.Priority).ThenBy(w => w.WorkOrderNumber, StringComparer.OrdinalIgnoreCase)];
}
