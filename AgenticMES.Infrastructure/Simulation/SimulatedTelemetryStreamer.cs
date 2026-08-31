using System.Threading.Channels;
using AgenticMES.Application.Common.Interfaces;
using AgenticMES.Domain.Enums;
using AgenticMES.Domain.Events;
using AgenticMES.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace AgenticMES.Infrastructure.Simulation;

/// <summary>
/// In-process OPC UA / MQTT mock that emits process and quality tags for each simulated
/// machine via a bounded <see cref="Channel{T}"/>.
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
            machine.CoolantPressure = Math.Min(machine.CoolantPressure, 9.5);
            machine.Vibration = Math.Max(machine.Vibration, 0.95);
            machine.SpindleLoad = Math.Max(machine.SpindleLoad, 78.0);
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
            MachineReading reading;
            lock (_gate)
            {
                Advance(machine);
                reading = machine.Capture();
            }

            await WriteTagAsync(reading, timestamp, TelemetryTags.Temperature, reading.Temperature, TelemetryTags.TemperatureUnit, cancellationToken)
                .ConfigureAwait(false);
            await WriteTagAsync(reading, timestamp, TelemetryTags.ProducedItems, reading.ProducedItems, TelemetryTags.ProducedItemsUnit, cancellationToken)
                .ConfigureAwait(false);
            await WriteTagAsync(reading, timestamp, TelemetryTags.ScrapRate, reading.ScrapRate, TelemetryTags.ScrapRateUnit, cancellationToken)
                .ConfigureAwait(false);
            await WriteTagAsync(reading, timestamp, TelemetryTags.CoolantPressure, reading.CoolantPressure, TelemetryTags.CoolantPressureUnit, cancellationToken)
                .ConfigureAwait(false);
            await WriteTagAsync(reading, timestamp, TelemetryTags.Vibration, reading.Vibration, TelemetryTags.VibrationUnit, cancellationToken)
                .ConfigureAwait(false);
            await WriteTagAsync(reading, timestamp, TelemetryTags.SpindleLoad, reading.SpindleLoad, TelemetryTags.SpindleLoadUnit, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task WriteTagAsync(
        MachineReading reading,
        DateTimeOffset timestamp,
        string tagName,
        double value,
        string engineeringUnit,
        CancellationToken cancellationToken)
    {
        var rounded = Math.Round(value, 2, MidpointRounding.AwayFromZero);
        var telemetry = new TelemetryEvent(
            EventId: Guid.NewGuid(),
            EquipmentId: reading.EquipmentId,
            Timestamp: timestamp,
            TagName: tagName,
            Value: rounded,
            EngineeringUnit: engineeringUnit,
            Quality: QualityFor(tagName, rounded, reading.Anomalous),
            ReportedState: reading.ReportedState,
            EquipmentCode: reading.EquipmentCode);

        await _channel.Writer.WriteAsync(telemetry, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "{EquipmentCode} {TagName}={Value} {Unit} ({State}, {Quality})",
            reading.EquipmentCode,
            tagName,
            telemetry.Value,
            engineeringUnit,
            reading.ReportedState,
            telemetry.Quality);
    }

    private static TelemetryQuality QualityFor(string tagName, double value, bool anomalous)
    {
        if (!anomalous)
        {
            return TelemetryQuality.Good;
        }

        return tagName switch
        {
            TelemetryTags.Temperature when value >= 120 => TelemetryQuality.Bad,
            TelemetryTags.Temperature => TelemetryQuality.Uncertain,
            TelemetryTags.CoolantPressure when value < 10 => TelemetryQuality.Bad,
            TelemetryTags.CoolantPressure => TelemetryQuality.Uncertain,
            TelemetryTags.Vibration when value >= 1.0 => TelemetryQuality.Bad,
            TelemetryTags.ScrapRate when value >= 15 => TelemetryQuality.Uncertain,
            TelemetryTags.SpindleLoad when value >= 80 => TelemetryQuality.Uncertain,
            _ => TelemetryQuality.Good
        };
    }

    private void Advance(MachineSimState machine)
    {
        if (machine.Anomaly is { } anomaly)
        {
            AdvanceAnomaly(machine, anomaly);
            return;
        }

        machine.Temperature = Math.Clamp(machine.Temperature + NextDelta(1.6), min: 45.0, max: 105.0);
        machine.ProducedItems += _random.Next(1, 4);
        machine.ScrapRate = Math.Clamp(machine.ScrapRate + NextDelta(0.35), min: 0.2, max: 8.0);
        machine.CoolantPressure = Math.Clamp(machine.CoolantPressure + NextDelta(0.35), min: 15.0, max: 20.0);
        machine.Vibration = Math.Clamp(machine.Vibration + NextDelta(0.04), min: 0.12, max: 0.45);
        machine.SpindleLoad = Math.Clamp(machine.SpindleLoad + NextDelta(2.4), min: 30.0, max: 60.0);
    }

    private void AdvanceAnomaly(MachineSimState machine, ThermalAnomaly anomaly)
    {
        machine.Temperature = Math.Clamp(
            anomaly.PeakTemperatureCelsius + NextDelta(3.5),
            min: anomaly.PeakTemperatureCelsius - 4.0,
            max: anomaly.PeakTemperatureCelsius + 8.0);

        machine.ScrapRate = Math.Clamp(
            machine.ScrapRate + 2.2 + NextDelta(0.45),
            min: machine.ScrapRate,
            max: anomaly.PeakScrapRatePercent);

        machine.CoolantPressure = Math.Clamp(
            machine.CoolantPressure - 1.6 + NextDelta(0.35),
            min: 5.0,
            max: machine.CoolantPressure);

        machine.Vibration = Math.Clamp(
            machine.Vibration + 0.12 + NextDelta(0.05),
            min: machine.Vibration,
            max: 1.8);

        machine.SpindleLoad = Math.Clamp(
            machine.SpindleLoad + 3.5 + NextDelta(1.2),
            min: machine.SpindleLoad,
            max: 92.0);

        machine.ProducedItems += _random.Next(0, 2);
    }

    private double NextDelta(double amplitude) => (_random.NextDouble() - 0.5) * 2.0 * amplitude;

    private static MachineSimState[] CreateMachines(IReadOnlyList<Guid> equipmentIds)
    {
        var ids = equipmentIds.Count > 0
            ? equipmentIds
            : DemoPlantCatalog.MachineIds;

        return [.. ids.Select(id => new MachineSimState(id, CodeFor(id)))];
    }

    private static string CodeFor(Guid equipmentId)
    {
        if (equipmentId == DemoPlantCatalog.Cnc01Id)
        {
            return "CNC-01";
        }

        if (equipmentId == DemoPlantCatalog.Cnc02Id)
        {
            return "CNC-02";
        }

        if (equipmentId == DemoPlantCatalog.Cnc03Id)
        {
            return "CNC-03";
        }

        return equipmentId.ToString("N")[..8];
    }

    private sealed class MachineSimState(Guid equipmentId, string equipmentCode)
    {
        public Guid EquipmentId { get; } = equipmentId;

        public string EquipmentCode { get; } = equipmentCode;

        public EquipmentState ReportedState { get; } = EquipmentState.Running;

        public double Temperature { get; set; } = 72.0;

        public double ProducedItems { get; set; }

        public double ScrapRate { get; set; } = 1.8;

        public double CoolantPressure { get; set; } = 18.0;

        public double Vibration { get; set; } = 0.28;

        public double SpindleLoad { get; set; } = 44.0;

        public ThermalAnomaly? Anomaly { get; set; }

        public MachineReading Capture() => new(
            EquipmentId,
            EquipmentCode,
            ReportedState,
            Temperature,
            ProducedItems,
            ScrapRate,
            CoolantPressure,
            Vibration,
            SpindleLoad,
            Anomaly is not null);
    }

    private readonly record struct MachineReading(
        Guid EquipmentId,
        string EquipmentCode,
        EquipmentState ReportedState,
        double Temperature,
        double ProducedItems,
        double ScrapRate,
        double CoolantPressure,
        double Vibration,
        double SpindleLoad,
        bool Anomalous);

    private sealed record ThermalAnomaly(double PeakTemperatureCelsius, double PeakScrapRatePercent);
}
