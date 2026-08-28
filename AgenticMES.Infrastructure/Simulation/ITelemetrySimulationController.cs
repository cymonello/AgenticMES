namespace AgenticMES.Infrastructure.Simulation;

/// <summary>
/// Demo hooks that inject plant-floor incidents into the in-process OPC UA / MQTT mock.
/// </summary>
public interface ITelemetrySimulationController
{
    /// <summary>True after <see cref="InjectThermalAnomaly"/> has been applied to a machine.</summary>
    bool HasActiveAnomaly { get; }

    /// <summary>Equipment identity currently running the thermal / quality incident, if any.</summary>
    Guid? AnomalousEquipmentId { get; }

    /// <summary>
    /// Spikes process temperature immediately and drives scrap rate toward
    /// <paramref name="peakScrapRatePercent"/> on subsequent publish cycles.
    /// </summary>
    void InjectThermalAnomaly(
        Guid equipmentId,
        double peakTemperatureCelsius,
        double peakScrapRatePercent);
}
