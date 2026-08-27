using AgenticMES.Application.Common.Interfaces;
using AgenticMES.Application.DTOs;
using AgenticMES.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AgenticMES.Application.Services;

/// <summary>
/// Deterministic OEE calculator. Availability uses planned vs running time,
/// Performance uses produced vs ideal rate, Quality uses good vs total count.
/// </summary>
public sealed class OeeCalculatorService(ILogger<OeeCalculatorService> logger) : IOeeCalculatorService
{
    public OeeCalculationResult Calculate(OeeCalculationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parameters = request.Parameters ?? new OeeCalculationParameters();
        var validationError = Validate(request, parameters);
        if (validationError is not null)
        {
            logger.LogWarning(
                "OEE calculation rejected for {EquipmentCode} / {WorkOrderNumber}: {Reason}",
                request.EquipmentCode,
                request.WorkOrderNumber,
                validationError);
            return OeeCalculationResult.Failure(validationError);
        }

        var asOf = parameters.AsOfUtc ?? DateTimeOffset.UtcNow;
        var (plannedProductionTime, actualRunTime) = ResolveTimes(request, parameters, asOf);

        var availability = Ratio(actualRunTime, plannedProductionTime);
        var performance = CalculatePerformance(
            request.PlannedQuantity,
            request.ProducedQuantity,
            plannedProductionTime,
            actualRunTime,
            parameters.IdealCycleTimeSeconds);
        var goodQuantity = request.ProducedQuantity - parameters.ScrapQuantity;
        var quality = request.ProducedQuantity > 0m
            ? Clamp01(goodQuantity / request.ProducedQuantity)
            : 1m;

        var metric = new OeeMetricDto(
            request.EquipmentId,
            request.EquipmentCode,
            request.WorkOrderId,
            request.WorkOrderNumber,
            availability,
            performance,
            quality,
            Clamp01(availability * performance * quality),
            plannedProductionTime,
            actualRunTime,
            request.PlannedQuantity,
            request.ProducedQuantity,
            goodQuantity,
            asOf);

        logger.LogInformation(
            "OEE for {EquipmentCode} / {WorkOrderNumber}: A={Availability:P1} P={Performance:P1} Q={Quality:P1} OEE={Oee:P1}",
            metric.EquipmentCode,
            metric.WorkOrderNumber,
            metric.Availability,
            metric.Performance,
            metric.Quality,
            metric.Oee);

        return OeeCalculationResult.Success(metric);
    }

    private static string? Validate(OeeCalculationRequest request, OeeCalculationParameters parameters)
    {
        if (request.AssignedEquipmentId is { } assigned && assigned != request.EquipmentId)
        {
            return $"Work order {request.WorkOrderNumber} is dispatched to a different equipment asset.";
        }

        if (parameters.ScrapQuantity < 0m)
        {
            return "Scrap quantity cannot be negative.";
        }

        if (parameters.ScrapQuantity > request.ProducedQuantity)
        {
            return "Scrap quantity cannot exceed produced quantity.";
        }

        if (parameters.IdealCycleTimeSeconds is <= 0m)
        {
            return "Ideal cycle time must be greater than zero when provided.";
        }

        if (parameters.UnplannedDowntime is { } unplanned && unplanned < TimeSpan.Zero)
        {
            return "Unplanned downtime cannot be negative.";
        }

        if (parameters.PlannedDowntime is { } planned && planned < TimeSpan.Zero)
        {
            return "Planned downtime cannot be negative.";
        }

        return null;
    }

    /// <summary>
    /// Planned production time is the work-order window (start → complete/now).
    /// Actual run time subtracts stop time: explicit downtime overrides, otherwise
    /// the current equipment state's overlap with that window (Faulted/Idle/Setup/Maintenance).
    /// </summary>
    private static (TimeSpan Planned, TimeSpan Run) ResolveTimes(
        OeeCalculationRequest request,
        OeeCalculationParameters parameters,
        DateTimeOffset asOf)
    {
        var periodStart = request.StartedAt ?? request.ReleasedAt ?? asOf;
        var periodEnd = request.CompletedAt ?? asOf;
        if (periodEnd < periodStart)
        {
            periodEnd = periodStart;
        }

        var planned = periodEnd - periodStart;
        if (planned <= TimeSpan.Zero)
        {
            return (TimeSpan.Zero, TimeSpan.Zero);
        }

        var stopTime = ResolveStopTime(request, parameters, periodStart, periodEnd);
        if (stopTime > planned)
        {
            stopTime = planned;
        }

        return (planned, planned - stopTime);
    }

    private static TimeSpan ResolveStopTime(
        OeeCalculationRequest request,
        OeeCalculationParameters parameters,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd)
    {
        if (parameters.UnplannedDowntime is not null || parameters.PlannedDowntime is not null)
        {
            return (parameters.UnplannedDowntime ?? TimeSpan.Zero)
                   + (parameters.PlannedDowntime ?? TimeSpan.Zero);
        }

        if (request.EquipmentState is EquipmentState.Running)
        {
            return TimeSpan.Zero;
        }

        var downtimeStart = request.StateChangedAt < periodStart ? periodStart : request.StateChangedAt;
        var overlap = periodEnd - downtimeStart;
        return overlap > TimeSpan.Zero ? overlap : TimeSpan.Zero;
    }

    /// <summary>
    /// Performance = (ideal cycle time × total count) / run time.
    /// When cycle time is omitted, the work-order plan rate (planned qty / planned time) is used.
    /// </summary>
    private static decimal CalculatePerformance(
        decimal plannedQuantity,
        decimal producedQuantity,
        TimeSpan plannedProductionTime,
        TimeSpan actualRunTime,
        decimal? idealCycleTimeSeconds)
    {
        if (producedQuantity <= 0m || actualRunTime <= TimeSpan.Zero)
        {
            return 0m;
        }

        var runSeconds = ToSeconds(actualRunTime);
        if (runSeconds <= 0m)
        {
            return 0m;
        }

        if (idealCycleTimeSeconds is { } cycleTime)
        {
            return Clamp01(cycleTime * producedQuantity / runSeconds);
        }

        var plannedSeconds = ToSeconds(plannedProductionTime);
        if (plannedQuantity <= 0m || plannedSeconds <= 0m)
        {
            return 0m;
        }

        var impliedCycleTime = plannedSeconds / plannedQuantity;
        return Clamp01(impliedCycleTime * producedQuantity / runSeconds);
    }

    private static decimal Ratio(TimeSpan numerator, TimeSpan denominator)
    {
        if (denominator <= TimeSpan.Zero)
        {
            return 0m;
        }

        return Clamp01(ToSeconds(numerator) / ToSeconds(denominator));
    }

    private static decimal ToSeconds(TimeSpan value) => (decimal)value.TotalSeconds;

    private static decimal Clamp01(decimal value)
    {
        if (value < 0m)
        {
            return 0m;
        }

        if (value > 1m)
        {
            return 1m;
        }

        return decimal.Round(value, 4, MidpointRounding.AwayFromZero);
    }
}
