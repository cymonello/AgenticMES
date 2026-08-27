namespace AgenticMES.Application.DTOs;

/// <summary>
/// ISA-95 / ISO 22400 OEE readout. Factor values are ratios in [0, 1].
/// </summary>
public sealed record OeeMetricDto(
    Guid EquipmentId,
    string EquipmentCode,
    Guid WorkOrderId,
    string WorkOrderNumber,
    decimal Availability,
    decimal Performance,
    decimal Quality,
    decimal Oee,
    TimeSpan PlannedProductionTime,
    TimeSpan ActualRunTime,
    decimal PlannedQuantity,
    decimal ProducedQuantity,
    decimal GoodQuantity,
    DateTimeOffset CalculatedAt)
{
    public decimal AvailabilityPercent => ToPercent(Availability);

    public decimal PerformancePercent => ToPercent(Performance);

    public decimal QualityPercent => ToPercent(Quality);

    public decimal OeePercent => ToPercent(Oee);

    private static decimal ToPercent(decimal ratio) =>
        decimal.Round(ratio * 100m, 2, MidpointRounding.AwayFromZero);
}

/// <summary>
/// Result wrapper so OEE validation failures are not thrown as exceptions.
/// </summary>
public readonly record struct OeeCalculationResult(bool IsSuccess, OeeMetricDto? Metric, string? Error)
{
    public static OeeCalculationResult Success(OeeMetricDto metric) => new(true, metric, null);

    public static OeeCalculationResult Failure(string error) => new(false, null, error);
}
