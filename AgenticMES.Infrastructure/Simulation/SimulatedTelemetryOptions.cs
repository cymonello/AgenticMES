namespace AgenticMES.Infrastructure.Simulation;

/// <summary>
/// Tuning knobs for the in-process OPC UA / MQTT telemetry mock.
/// </summary>
public sealed record SimulatedTelemetryOptions
{
    /// <summary>Interval between published reading cycles. Defaults to 1 second.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Bounded channel capacity. Producer waits when full so demo consumers do not drop tags.</summary>
    public int ChannelCapacity { get; init; } = 256;

    /// <summary>
    /// Equipment identities to simulate. When empty, the seeded CNC-01 / CNC-02 / CNC-03 machines are used.
    /// </summary>
    public IReadOnlyList<Guid> EquipmentIds { get; init; } = [];
}
