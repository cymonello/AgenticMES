using System.Collections.Concurrent;
using AgenticMES.Application.Common.Interfaces;
using AgenticMES.Domain.Entities;
using AgenticMES.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AgenticMES.Infrastructure.Persistence;

/// <summary>
/// Process-local ISA-95 equipment store seeded from <see cref="DemoPlantCatalog"/>.
/// </summary>
public sealed class InMemoryEquipmentRepository : IEquipmentRepository
{
    private readonly ConcurrentDictionary<Guid, Equipment> _items = new();
    private readonly ILogger<InMemoryEquipmentRepository> _logger;

    public InMemoryEquipmentRepository(
        ILogger<InMemoryEquipmentRepository> logger,
        DemoPlantCatalog? catalog = null)
    {
        _logger = logger;
        catalog ??= DemoPlantCatalog.Default;

        foreach (var equipment in catalog.Equipment)
        {
            _items[equipment.Id] = equipment;
        }

        _logger.LogInformation(
            "In-memory equipment store seeded with {Count} ISA-95 nodes.",
            _items.Count);
    }

    public Task<Equipment?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _items.TryGetValue(id, out var equipment);
        return Task.FromResult(equipment);
    }

    public Task<Equipment?> GetByCodeAsync(string equipmentCode, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(equipmentCode);

        var match = _items.Values.FirstOrDefault(e =>
            e.EquipmentCode.Equals(equipmentCode.Trim(), StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(match);
    }

    public Task<IReadOnlyList<Equipment>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<Equipment>>(Snapshot());
    }

    public Task<IReadOnlyList<Equipment>> ListByStateAsync(EquipmentState state, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<Equipment>>(
            [.. Snapshot().Where(e => e.State == state)]);
    }

    public Task<IReadOnlyList<Equipment>> ListByParentAsync(Guid parentEquipmentId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<Equipment>>(
            [.. Snapshot().Where(e => e.ParentEquipmentId == parentEquipmentId)]);
    }

    public Task AddAsync(Equipment equipment, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(equipment);

        if (!_items.TryAdd(equipment.Id, equipment))
        {
            throw new InvalidOperationException($"Equipment {equipment.Id} already exists.");
        }

        var duplicateCode = _items.Values.Any(e =>
            e.Id != equipment.Id
            && e.EquipmentCode.Equals(equipment.EquipmentCode, StringComparison.OrdinalIgnoreCase));
        if (duplicateCode)
        {
            _items.TryRemove(equipment.Id, out _);
            throw new InvalidOperationException($"Equipment code '{equipment.EquipmentCode}' already exists.");
        }

        _logger.LogDebug("Added equipment {EquipmentCode}.", equipment.EquipmentCode);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Equipment equipment, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(equipment);

        if (!_items.ContainsKey(equipment.Id))
        {
            throw new InvalidOperationException($"Equipment {equipment.Id} was not found.");
        }

        _items[equipment.Id] = equipment;
        return Task.CompletedTask;
    }

    private Equipment[] Snapshot() =>
        [.. _items.Values.OrderBy(e => e.Level).ThenBy(e => e.EquipmentCode, StringComparer.OrdinalIgnoreCase)];
}
