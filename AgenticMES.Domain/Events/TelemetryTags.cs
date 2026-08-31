namespace AgenticMES.Domain.Events;

/// <summary>
/// Canonical OPC UA / MQTT tag names and engineering units for equipment performance readouts.
/// </summary>
public static class TelemetryTags
{
    public const string Temperature = "Temperature";
    public const string ProducedItems = "ProducedItems";
    public const string ScrapRate = "ScrapRate";
    public const string CoolantPressure = "CoolantPressure";
    public const string Vibration = "Vibration";
    public const string SpindleLoad = "SpindleLoad";

    public const string TemperatureUnit = "°C";
    public const string ProducedItemsUnit = "pcs";
    public const string ScrapRateUnit = "%";
    public const string CoolantPressureUnit = "PSI";
    public const string VibrationUnit = "in/s";
    public const string SpindleLoadUnit = "%";
}
