using AgenticMES.Domain.Enums;

namespace AgenticMES.Domain.Events;

/// <summary>
/// Immutable ISA-95 equipment performance / process-data readout (OPC UA / MQTT tag update).
/// </summary>
public sealed record TelemetryEvent(
    Guid EventId,
    Guid EquipmentId,
    DateTimeOffset Timestamp,
    string TagName,
    double Value,
    string? EngineeringUnit = null,
    TelemetryQuality Quality = TelemetryQuality.Good,
    EquipmentState? ReportedState = null);
