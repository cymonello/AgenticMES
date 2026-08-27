namespace AgenticMES.Domain.Events;

/// <summary>
/// Canonical OPC UA / MQTT tag names and engineering units for equipment performance readouts.
/// </summary>
public static class TelemetryTags
{
    public const string Temperature = "Temperature";
    public const string ProducedItems = "ProducedItems";
    public const string ScrapRate = "ScrapRate";

    public const string TemperatureUnit = "°C";
    public const string ProducedItemsUnit = "pcs";
    public const string ScrapRateUnit = "%";
}
