using AgenticMES.Application.Common.Interfaces;
using AgenticMES.Application.DTOs;
using AgenticMES.Application.Services;
using AgenticMES.Application.Tests.Support;
using AgenticMES.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgenticMES.Application.Tests.Services;

public sealed class OeeCalculatorServiceTests
{
    private readonly OeeCalculatorService _sut = new(NullLogger<OeeCalculatorService>.Instance);

    [Fact]
    public void Calculate_ThrowsWhenRequestIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => _sut.Calculate((OeeCalculationRequest)null!));
    }

    [Fact]
    public void Calculate_RejectsWorkOrderDispatchedToDifferentEquipment()
    {
        var request = TestFixtures.CreateOeeRequest(
            equipmentId: Guid.NewGuid(),
            assignedEquipmentId: Guid.NewGuid(),
            workOrderNumber: "WO-mismatch");

        var result = _sut.Calculate(request);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Metric);
        Assert.Equal("Work order WO-mismatch is dispatched to a different equipment asset.", result.Error);
    }

    [Fact]
    public void Calculate_RejectsNegativeScrapQuantity()
    {
        var request = TestFixtures.CreateOeeRequest(
            parameters: new OeeCalculationParameters(ScrapQuantity: -1m, AsOfUtc: TestFixtures.AsOf));

        var result = _sut.Calculate(request);

        Assert.False(result.IsSuccess);
        Assert.Equal("Scrap quantity cannot be negative.", result.Error);
    }

    [Fact]
    public void Calculate_RejectsScrapExceedingProducedQuantity()
    {
        var request = TestFixtures.CreateOeeRequest(
            producedQuantity: 10m,
            parameters: new OeeCalculationParameters(ScrapQuantity: 11m, AsOfUtc: TestFixtures.AsOf));

        var result = _sut.Calculate(request);

        Assert.False(result.IsSuccess);
        Assert.Equal("Scrap quantity cannot exceed produced quantity.", result.Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.5)]
    public void Calculate_RejectsNonPositiveIdealCycleTime(decimal cycleTime)
    {
        var request = TestFixtures.CreateOeeRequest(
            parameters: new OeeCalculationParameters(IdealCycleTimeSeconds: cycleTime, AsOfUtc: TestFixtures.AsOf));

        var result = _sut.Calculate(request);

        Assert.False(result.IsSuccess);
        Assert.Equal("Ideal cycle time must be greater than zero when provided.", result.Error);
    }

    [Fact]
    public void Calculate_RejectsNegativeUnplannedDowntime()
    {
        var request = TestFixtures.CreateOeeRequest(
            parameters: new OeeCalculationParameters(
                UnplannedDowntime: TimeSpan.FromMinutes(-1),
                AsOfUtc: TestFixtures.AsOf));

        var result = _sut.Calculate(request);

        Assert.False(result.IsSuccess);
        Assert.Equal("Unplanned downtime cannot be negative.", result.Error);
    }

    [Fact]
    public void Calculate_RejectsNegativePlannedDowntime()
    {
        var request = TestFixtures.CreateOeeRequest(
            parameters: new OeeCalculationParameters(
                PlannedDowntime: TimeSpan.FromMinutes(-1),
                AsOfUtc: TestFixtures.AsOf));

        var result = _sut.Calculate(request);

        Assert.False(result.IsSuccess);
        Assert.Equal("Planned downtime cannot be negative.", result.Error);
    }

    [Fact]
    public void Calculate_ReturnsPerfectOeeWhenRunningAtPlanRateWithNoScrap()
    {
        var request = TestFixtures.CreateOeeRequest();

        var result = _sut.Calculate(request);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Metric);
        Assert.Equal(1m, result.Metric.Availability);
        Assert.Equal(1m, result.Metric.Performance);
        Assert.Equal(1m, result.Metric.Quality);
        Assert.Equal(1m, result.Metric.Oee);
        Assert.Equal(TimeSpan.FromHours(1), result.Metric.PlannedProductionTime);
        Assert.Equal(TimeSpan.FromHours(1), result.Metric.ActualRunTime);
        Assert.Equal(100m, result.Metric.GoodQuantity);
        Assert.Equal(TestFixtures.AsOf, result.Metric.CalculatedAt);
        Assert.Equal("CNC-01", result.Metric.EquipmentCode);
        Assert.Equal("WO-2024-001", result.Metric.WorkOrderNumber);
    }

    [Fact]
    public void Calculate_ReducesQualityByScrapRatio()
    {
        var request = TestFixtures.CreateOeeRequest(
            parameters: new OeeCalculationParameters(ScrapQuantity: 10m, AsOfUtc: TestFixtures.AsOf));

        var result = _sut.Calculate(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(0.9m, result.Metric!.Quality);
        Assert.Equal(90m, result.Metric.GoodQuantity);
        Assert.Equal(0.9m, result.Metric.Oee);
    }

    [Fact]
    public void Calculate_TreatsQualityAsOneWhenNothingHasBeenProduced()
    {
        var request = TestFixtures.CreateOeeRequest(producedQuantity: 0m);

        var result = _sut.Calculate(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(1m, result.Metric!.Quality);
        Assert.Equal(0m, result.Metric.Performance);
        Assert.Equal(0m, result.Metric.Oee);
        Assert.Equal(0m, result.Metric.GoodQuantity);
    }

    [Fact]
    public void Calculate_SubtractsExplicitDowntimeFromAvailability()
    {
        var request = TestFixtures.CreateOeeRequest(
            parameters: new OeeCalculationParameters(
                UnplannedDowntime: TimeSpan.FromMinutes(10),
                PlannedDowntime: TimeSpan.FromMinutes(5),
                AsOfUtc: TestFixtures.AsOf));

        var result = _sut.Calculate(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(0.75m, result.Metric!.Availability);
        Assert.Equal(TimeSpan.FromMinutes(45), result.Metric.ActualRunTime);
        Assert.Equal(1m, result.Metric.Performance);
        Assert.Equal(0.75m, result.Metric.Oee);
    }

    [Fact]
    public void Calculate_CapsStopTimeAtPlannedProductionWindow()
    {
        var request = TestFixtures.CreateOeeRequest(
            parameters: new OeeCalculationParameters(
                UnplannedDowntime: TimeSpan.FromHours(2),
                AsOfUtc: TestFixtures.AsOf));

        var result = _sut.Calculate(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(0m, result.Metric!.Availability);
        Assert.Equal(TimeSpan.Zero, result.Metric.ActualRunTime);
        Assert.Equal(0m, result.Metric.Performance);
    }

    [Fact]
    public void Calculate_UsesIdealCycleTimeForPerformanceWhenProvided()
    {
        var request = TestFixtures.CreateOeeRequest(
            producedQuantity: 80m,
            parameters: new OeeCalculationParameters(
                IdealCycleTimeSeconds: 36m,
                AsOfUtc: TestFixtures.AsOf));

        var result = _sut.Calculate(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(1m, result.Metric!.Availability);
        Assert.Equal(0.8m, result.Metric.Performance);
        Assert.Equal(0.8m, result.Metric.Oee);
    }

    [Fact]
    public void Calculate_ClampsPerformanceWhenFasterThanIdealRate()
    {
        var request = TestFixtures.CreateOeeRequest(
            producedQuantity: 200m,
            parameters: new OeeCalculationParameters(
                IdealCycleTimeSeconds: 36m,
                AsOfUtc: TestFixtures.AsOf));

        var result = _sut.Calculate(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(1m, result.Metric!.Performance);
    }

    [Fact]
    public void Calculate_UsesFaultedStateOverlapAsStopTimeWhenDowntimeIsNotProvided()
    {
        var request = TestFixtures.CreateOeeRequest(
            equipmentState: EquipmentState.Faulted,
            stateChangedAt: TestFixtures.AsOf.AddMinutes(-20));

        var result = _sut.Calculate(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(0.6667m, result.Metric!.Availability);
        Assert.Equal(TimeSpan.FromMinutes(40), result.Metric.ActualRunTime);
    }

    [Theory]
    [InlineData(EquipmentState.Idle)]
    [InlineData(EquipmentState.Setup)]
    [InlineData(EquipmentState.Maintenance)]
    public void Calculate_TreatsNonRunningStatesAsStopTime(EquipmentState state)
    {
        var request = TestFixtures.CreateOeeRequest(
            equipmentState: state,
            stateChangedAt: TestFixtures.AsOf.AddMinutes(-30));

        var result = _sut.Calculate(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(0.5m, result.Metric!.Availability);
        Assert.Equal(TimeSpan.FromMinutes(30), result.Metric.ActualRunTime);
    }

    [Fact]
    public void Calculate_ClampsDowntimeStartToPeriodStartWhenStateChangedEarlier()
    {
        var request = TestFixtures.CreateOeeRequest(
            equipmentState: EquipmentState.Faulted,
            stateChangedAt: TestFixtures.AsOf.AddHours(-3));

        var result = _sut.Calculate(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(0m, result.Metric!.Availability);
        Assert.Equal(TimeSpan.Zero, result.Metric.ActualRunTime);
    }

    [Fact]
    public void Calculate_IgnoresStopOverlapWhenStateChangedAfterPeriodEnd()
    {
        var request = TestFixtures.CreateOeeRequest(
            equipmentState: EquipmentState.Faulted,
            stateChangedAt: TestFixtures.AsOf.AddMinutes(5),
            completedAt: TestFixtures.AsOf);

        var result = _sut.Calculate(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(1m, result.Metric!.Availability);
        Assert.Equal(TimeSpan.FromHours(1), result.Metric.ActualRunTime);
    }

    [Fact]
    public void Calculate_UsesCompletedAtAsPeriodEndInsteadOfAsOf()
    {
        var request = TestFixtures.CreateOeeRequest(
            startedAt: TestFixtures.AsOf.AddHours(-2),
            completedAt: TestFixtures.AsOf.AddHours(-1));

        var result = _sut.Calculate(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(TimeSpan.FromHours(1), result.Metric!.PlannedProductionTime);
        Assert.Equal(TestFixtures.AsOf, result.Metric.CalculatedAt);
    }

    [Fact]
    public void Calculate_FallsBackToReleasedAtWhenStartedAtIsMissing()
    {
        var releasedAt = TestFixtures.AsOf.AddMinutes(-30);
        var equipmentId = Guid.NewGuid();
        var request = new OeeCalculationRequest(
            equipmentId,
            "CNC-01",
            EquipmentState.Running,
            releasedAt,
            Guid.NewGuid(),
            "WO-2024-001",
            WorkOrderStatus.Released,
            equipmentId,
            100m,
            50m,
            TestFixtures.AsOf.AddHours(2),
            releasedAt,
            StartedAt: null,
            CompletedAt: null,
            new OeeCalculationParameters(AsOfUtc: TestFixtures.AsOf));

        var result = _sut.Calculate(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(TimeSpan.FromMinutes(30), result.Metric!.PlannedProductionTime);
    }

    [Fact]
    public void Calculate_ReturnsZeroTimesWhenPeriodHasNotStarted()
    {
        var equipmentId = Guid.NewGuid();
        var request = new OeeCalculationRequest(
            equipmentId,
            "CNC-01",
            EquipmentState.Idle,
            TestFixtures.AsOf,
            Guid.NewGuid(),
            "WO-2024-001",
            WorkOrderStatus.Created,
            equipmentId,
            100m,
            0m,
            TestFixtures.AsOf.AddHours(2),
            ReleasedAt: null,
            StartedAt: null,
            CompletedAt: null,
            new OeeCalculationParameters(AsOfUtc: TestFixtures.AsOf));

        var result = _sut.Calculate(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(TimeSpan.Zero, result.Metric!.PlannedProductionTime);
        Assert.Equal(TimeSpan.Zero, result.Metric.ActualRunTime);
        Assert.Equal(0m, result.Metric.Availability);
        Assert.Equal(0m, result.Metric.Performance);
    }

    [Fact]
    public void Calculate_ClampsInvertedPeriodToZeroPlannedTime()
    {
        var request = TestFixtures.CreateOeeRequest(
            startedAt: TestFixtures.AsOf,
            completedAt: TestFixtures.AsOf.AddHours(-1));

        var result = _sut.Calculate(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(TimeSpan.Zero, result.Metric!.PlannedProductionTime);
        Assert.Equal(0m, result.Metric.Availability);
    }

    [Fact]
    public void Calculate_ReturnsZeroPerformanceWhenPlannedQuantityIsZeroAndNoCycleTime()
    {
        var equipmentId = Guid.NewGuid();
        var request = new OeeCalculationRequest(
            equipmentId,
            "CNC-01",
            EquipmentState.Running,
            TestFixtures.AsOf.AddHours(-1),
            Guid.NewGuid(),
            "WO-2024-001",
            WorkOrderStatus.InProgress,
            equipmentId,
            PlannedQuantity: 0m,
            ProducedQuantity: 10m,
            TestFixtures.AsOf.AddHours(2),
            TestFixtures.AsOf.AddHours(-1),
            TestFixtures.AsOf.AddHours(-1),
            CompletedAt: null,
            new OeeCalculationParameters(AsOfUtc: TestFixtures.AsOf));

        var result = _sut.Calculate(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(0m, result.Metric!.Performance);
    }

    [Fact]
    public void Calculate_UsesDefaultParametersWhenParametersAreNull()
    {
        var request = TestFixtures.CreateOeeRequest() with { Parameters = null };

        var result = _sut.Calculate(request);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Metric);
        Assert.Equal(100m, result.Metric.GoodQuantity);
        Assert.Equal(1m, result.Metric.Quality);
    }

    [Fact]
    public void Calculate_RoundsOeeFactorsToFourDecimalPlaces()
    {
        var request = TestFixtures.CreateOeeRequest(
            producedQuantity: 1m,
            parameters: new OeeCalculationParameters(
                IdealCycleTimeSeconds: 1m,
                AsOfUtc: TestFixtures.AsOf));

        var result = _sut.Calculate(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(0.0003m, result.Metric!.Performance);
    }

    [Fact]
    public void Calculate_FromEquipmentAndWorkOrderReturnsSuccess()
    {
        var equipment = TestFixtures.CreateEquipment(state: EquipmentState.Running);
        var workOrder = TestFixtures.CreateWorkOrder();
        workOrder.Release();
        workOrder.DispatchTo(equipment.Id);
        workOrder.Start();
        workOrder.ReportProduction(10m);

        var result = ((IOeeCalculatorService)_sut).Calculate(
            equipment,
            workOrder,
            new OeeCalculationParameters(AsOfUtc: TestFixtures.AsOf));

        Assert.True(result.IsSuccess);
        Assert.Equal(equipment.Id, result.Metric!.EquipmentId);
        Assert.Equal(workOrder.Id, result.Metric.WorkOrderId);
        Assert.Equal(10m, result.Metric.ProducedQuantity);
        Assert.Equal(10m, result.Metric.GoodQuantity);
    }
}
