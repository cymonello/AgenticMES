using System.Threading.Channels;
using AgenticMES.Application.Common.Interfaces;
using AgenticMES.Domain.Enums;
using AgenticMES.Domain.Events;
using AgenticMES.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace AgenticMES.Infrastructure.Simulation;

/// <summary>
/// In-process OPC UA / MQTT mock that emits temperature, produced-item count, and scrap-rate
/// tags for each simulated machine once per second via a bounded <see cref="Channel{T}"/>.
/// </summary>
public sealed class SimulatedTelemetryStreamer : ITelemetryStreamer, ITelemetrySimulationController
{
    private readonly ILogger<SimulatedTelemetryStreamer> _logger;
    private readonly SimulatedTelemetryOptions _options;
    private readonly Channel<TelemetryEvent> _channel;
    private readonly MachineSimState[] _machines;
    private readonly object _gate = new();
    private readonly Random _random = Random.Shared;

    public SimulatedTelemetryStreamer(
        ILogger<SimulatedTelemetryStreamer> logger,
        SimulatedTelemetryOptions? options = null)
    {
        _logger = logger;
        _options = options ?? new SimulatedTelemetryOptions();

        ArgumentOutOfRangeException.ThrowIfLessThan(_options.ChannelCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.Interval, TimeSpan.Zero);

        _channel = Channel.CreateBounded<TelemetryEvent>(new BoundedChannelOptions(_options.ChannelCapacity)
        {
            SingleWriter = true,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        _machines = CreateMachines(_options.EquipmentIds);
    }

    public ChannelReader<TelemetryEvent> Reader => _channel.Reader;

    /// <inheritdoc />
    public bool HasActiveAnomaly { get; private set; }

    /// <inheritdoc />
    public Guid? AnomalousEquipmentId { get; private set; }

    /// <inheritdoc />
    public void InjectThermalAnomaly(
        Guid equipmentId,
        double peakTemperatureCelsius,
        double peakScrapRatePercent)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(peakTemperatureCelsius, 0d);
        ArgumentOutOfRangeException.ThrowIfLessThan(peakScrapRatePercent, 0d);

        lock (_gate)
        {
            var machine = Array.Find(_machines, candidate => candidate.EquipmentId == equipmentId);
            if (machine is null)
            {
                throw new InvalidOperationException($"No simulated machine is registered for {equipmentId}.");
            }

            machine.Anomaly = new ThermalAnomaly(peakTemperatureCelsius, peakScrapRatePercent);
            machine.Temperature = peakTemperatureCelsius;
            machine.ScrapRate = Math.Max(machine.ScrapRate, Math.Min(12.0, peakScrapRatePercent * 0.45));
            HasActiveAnomaly = true;
            AnomalousEquipmentId = equipmentId;
        }

        _logger.LogWarning(
            "Thermal anomaly injected on {EquipmentId}: peak {Temperature} °C, scrap climbing toward {ScrapRate}%.",
            equipmentId,
            peakTemperatureCelsius,
            peakScrapRatePercent);
    }

    /// <inheritdoc />
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Starting simulated telemetry for {MachineCount} machines every {IntervalMs} ms.",
            _machines.Length,
            _options.Interval.TotalMilliseconds);

        try
        {
            using var timer = new PeriodicTimer(_options.Interval);

            await PublishCycleAsync(cancellationToken).ConfigureAwait(false);

            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await PublishCycleAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected shutdown path for the mock streamer.
        }
        finally
        {
            _channel.Writer.TryComplete();
            _logger.LogInformation("Simulated telemetry streamer stopped.");
        }
    }

    private async Task PublishCycleAsync(CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.UtcNow;

        foreach (var machine in _machines)
        {
            double temperature;
            double producedItems;
            double scrapRate;
            lock (_gate)
            {
                Advance(machine);
                temperature = machine.Temperature;
                producedItems = machine.ProducedItems;
                scrapRate = machine.ScrapRate;
            }

            await WriteTagAsync(
                    machine,
                    timestamp,
                    TelemetryTags.Temperature,
                    temperature,
                    TelemetryTags.TemperatureUnit,
                    cancellationToken)
                .ConfigureAwait(false);

            await WriteTagAsync(
                    machine,
                    timestamp,
                    TelemetryTags.ProducedItems,
                    producedItems,
                    TelemetryTags.ProducedItemsUnit,
                    cancellationToken)
                .ConfigureAwait(false);

            await WriteTagAsync(
                    machine,
                    timestamp,
                    TelemetryTags.ScrapRate,
                    scrapRate,
                    TelemetryTags.ScrapRateUnit,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task WriteTagAsync(
        MachineSimState machine,
        DateTimeOffset timestamp,
        string tagName,
        double value,
        string engineeringUnit,
        CancellationToken cancellationToken)
    {
        var telemetry = new TelemetryEvent(
            EventId: Guid.NewGuid(),
            EquipmentId: machine.EquipmentId,
            Timestamp: timestamp,
            TagName: tagName,
            Value: Math.Round(value, 2, MidpointRounding.AwayFromZero),
            EngineeringUnit: engineeringUnit,
            Quality: TelemetryQuality.Good,
            ReportedState: machine.ReportedState);

        await _channel.Writer.WriteAsync(telemetry, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "{EquipmentId} {TagName}={Value} {Unit} ({State})",
            machine.EquipmentId,
            tagName,
            telemetry.Value,
            engineeringUnit,
            machine.ReportedState);
    }

    private void Advance(MachineSimState machine)
    {
        if (machine.Anomaly is { } anomaly)
        {
            AdvanceAnomaly(machine, anomaly);
            return;
        }

        // Random-walk process temperature around a typical CNC coolant/spindle band.
        machine.Temperature = Math.Clamp(
            machine.Temperature + NextDelta(1.6),
            min: 45.0,
            max: 105.0);

        // Cumulative production counter — 1–3 pieces per second while Running.
        machine.ProducedItems += _random.Next(1, 4);

        // Scrap rate (%) wanders in a realistic 0.2–8% quality band.
        machine.ScrapRate = Math.Clamp(
            machine.ScrapRate + NextDelta(0.35),
            min: 0.2,
            max: 8.0);
    }

    private void AdvanceAnomaly(MachineSimState machine, ThermalAnomaly anomaly)
    {
        machine.Temperature = Math.Clamp(
            anomaly.PeakTemperatureCelsius + NextDelta(3.5),
            min: anomaly.PeakTemperatureCelsius - 4.0,
            max: anomaly.PeakTemperatureCelsius + 8.0);

        // Quality collapse: scrap climbs each cycle toward the injected peak.
        machine.ScrapRate = Math.Clamp(
            machine.ScrapRate + 2.2 + NextDelta(0.45),
            min: machine.ScrapRate,
            max: anomaly.PeakScrapRatePercent);

        // Throughput drops while the spindle overheats.
        machine.ProducedItems += _random.Next(0, 2);
    }

    private double NextDelta(double amplitude) => (_random.NextDouble() - 0.5) * 2.0 * amplitude;

    private static MachineSimState[] CreateMachines(IReadOnlyList<Guid> equipmentIds)
    {
        var ids = equipmentIds.Count > 0
            ? equipmentIds
            : DemoPlantCatalog.MachineIds;

        return [.. ids.Select(id => new MachineSimState(id))];
    }

    private sealed class MachineSimState(Guid equipmentId)
    {
        public Guid EquipmentId { get; } = equipmentId;

        public EquipmentState ReportedState { get; } = EquipmentState.Running;

        public double Temperature { get; set; } = 72.0;

        public double ProducedItems { get; set; }

        public double ScrapRate { get; set; } = 1.8;

        public ThermalAnomaly? Anomaly { get; set; }
    }

    private sealed record ThermalAnomaly(double PeakTemperatureCelsius, double PeakScrapRatePercent);
}
