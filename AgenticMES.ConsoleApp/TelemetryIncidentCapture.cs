using System.Collections.Concurrent;
using System.Text;
using AgenticMES.Domain.Enums;
using AgenticMES.Domain.Events;

namespace AgenticMES.ConsoleApp;

/// <summary>
/// In-memory rolling window of OPC UA / MQTT readouts. Detects the incident from tag
/// thresholds and trends — no persistence layer.
/// </summary>
internal sealed class TelemetryIncidentCapture
{
    public const double TemperatureAlarmCelsius = 110.0;
    public const double ScrapAlarmPercent = 10.0;
    public const double CoolantLowPsi = 12.0;
    public const double VibrationAlarmInPerSec = 0.8;

    private const int MaxEvents = 720;

    private readonly ConcurrentQueue<TelemetryEvent> _window = new();

    public DateTimeOffset? DetectedAt { get; private set; }

    public Guid? AnomalousEquipmentId { get; private set; }

    public string? AnomalousEquipmentCode { get; private set; }

    /// <summary>
    /// Records a readout. Returns true when this sample is the first to cross an alarm threshold.
    /// </summary>
    public bool Observe(TelemetryEvent readout)
    {
        _window.Enqueue(readout);
        while (_window.Count > MaxEvents && _window.TryDequeue(out _))
        {
        }

        if (DetectedAt is not null || !IsAlarm(readout))
        {
            return false;
        }

        DetectedAt = readout.Timestamp;
        AnomalousEquipmentId = readout.EquipmentId;
        AnomalousEquipmentCode = readout.EquipmentCode;
        return true;
    }

    public bool TryDescribe(Guid? fallbackEquipmentId, out CapturedIncident? incident)
    {
        var events = _window.ToArray();
        var equipmentId = AnomalousEquipmentId ?? fallbackEquipmentId;
        if (equipmentId is null || events.Length == 0)
        {
            incident = null;
            return false;
        }

        var forMachine = events.Where(e => e.EquipmentId == equipmentId.Value).ToArray();
        if (forMachine.Length == 0)
        {
            incident = null;
            return false;
        }

        var code = AnomalousEquipmentCode
                   ?? forMachine.LastOrDefault(e => !string.IsNullOrWhiteSpace(e.EquipmentCode))?.EquipmentCode
                   ?? equipmentId.Value.ToString("N")[..8];

        incident = new CapturedIncident(
            EquipmentId: equipmentId.Value,
            EquipmentCode: code,
            PrimarySymptom: InferSymptom(forMachine),
            DetectedAt: DetectedAt ?? forMachine[0].Timestamp,
            TelemetrySummary: BuildSummary(code, forMachine, DetectedAt),
            PeakTemperature: Max(forMachine, TelemetryTags.Temperature),
            PeakScrapRate: Max(forMachine, TelemetryTags.ScrapRate),
            LatestCoolantPressure: Last(forMachine, TelemetryTags.CoolantPressure),
            PeakVibration: Max(forMachine, TelemetryTags.Vibration),
            LatestSpindleLoad: Last(forMachine, TelemetryTags.SpindleLoad));
        return true;
    }

    private static bool IsAlarm(TelemetryEvent readout) => readout.TagName switch
    {
        TelemetryTags.Temperature => readout.Value >= TemperatureAlarmCelsius,
        TelemetryTags.ScrapRate => readout.Value >= ScrapAlarmPercent,
        TelemetryTags.CoolantPressure => readout.Value < CoolantLowPsi,
        TelemetryTags.Vibration => readout.Value >= VibrationAlarmInPerSec,
        _ => readout.Quality is TelemetryQuality.Bad
    };

    private static string InferSymptom(IReadOnlyList<TelemetryEvent> events)
    {
        var peakTemp = Max(events, TelemetryTags.Temperature);
        var minCoolant = Min(events, TelemetryTags.CoolantPressure);
        var peakVibration = Max(events, TelemetryTags.Vibration);
        var peakScrap = Max(events, TelemetryTags.ScrapRate);

        if (peakTemp >= TemperatureAlarmCelsius)
        {
            return "THERMAL_RUNAWAY";
        }

        if (peakVibration >= VibrationAlarmInPerSec)
        {
            return "VIBRATION_ALARM";
        }

        if (minCoolant is { } coolant && coolant < CoolantLowPsi)
        {
            return "THERMAL_RUNAWAY";
        }

        if (peakScrap >= ScrapAlarmPercent)
        {
            return "THERMAL_RUNAWAY";
        }

        return "PROCESS_DEVIATION";
    }

    private static string BuildSummary(
        string equipmentCode,
        IReadOnlyList<TelemetryEvent> events,
        DateTimeOffset? detectedAt)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Equipment: {equipmentCode}");
        builder.AppendLine($"Window: {events.Count} tag updates from {events[0].Timestamp:HH:mm:ss} to {events[^1].Timestamp:HH:mm:ss} UTC");
        if (detectedAt is not null)
        {
            builder.AppendLine($"Alarm first observed: {detectedAt:HH:mm:ss} UTC");
        }

        var state = events.LastOrDefault(e => e.ReportedState is not null)?.ReportedState;
        if (state is not null)
        {
            builder.AppendLine($"Reported state: {state}");
        }

        foreach (var group in events
                     .GroupBy(e => e.TagName, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group.OrderBy(e => e.Timestamp).ToArray();
            var first = ordered[0];
            var last = ordered[^1];
            var min = ordered.Min(e => e.Value);
            var max = ordered.Max(e => e.Value);
            var unit = last.EngineeringUnit ?? string.Empty;
            var quality = last.Quality == TelemetryQuality.Good ? string.Empty : $", quality {last.Quality}";
            builder.AppendLine(
                $"{group.Key}: {first.Value:0.00} → {last.Value:0.00} {unit} (min {min:0.00}, max {max:0.00}{quality})".TrimEnd());
        }

        return builder.ToString().TrimEnd();
    }

    private static double? Max(IEnumerable<TelemetryEvent> events, string tag)
    {
        var values = events.Where(e => e.TagName == tag).Select(e => e.Value).ToArray();
        return values.Length == 0 ? null : values.Max();
    }

    private static double? Min(IEnumerable<TelemetryEvent> events, string tag)
    {
        var values = events.Where(e => e.TagName == tag).Select(e => e.Value).ToArray();
        return values.Length == 0 ? null : values.Min();
    }

    private static double? Last(IEnumerable<TelemetryEvent> events, string tag) =>
        events.LastOrDefault(e => e.TagName == tag)?.Value;
}

internal sealed record CapturedIncident(
    Guid EquipmentId,
    string EquipmentCode,
    string PrimarySymptom,
    DateTimeOffset DetectedAt,
    string TelemetrySummary,
    double? PeakTemperature,
    double? PeakScrapRate,
    double? LatestCoolantPressure,
    double? PeakVibration,
    double? LatestSpindleLoad);
