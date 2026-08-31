using AgenticMES.Application.Common.Interfaces;
using AgenticMES.Application.CQRS;
using AgenticMES.Application.CQRS.Commands;
using AgenticMES.Application.Services;
using AgenticMES.Application.Tests.Support;
using AgenticMES.Domain.Entities;
using AgenticMES.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgenticMES.Application.Tests.Services;

public sealed class SchedulingServiceTests
{
    private readonly ICommandHandler<RerouteWorkOrderCommand, RerouteWorkOrderResult> _handler =
        Substitute.For<ICommandHandler<RerouteWorkOrderCommand, RerouteWorkOrderResult>>();

    private readonly IEquipmentRepository _equipmentRepository = Substitute.For<IEquipmentRepository>();
    private readonly IWorkOrderRepository _workOrderRepository = Substitute.For<IWorkOrderRepository>();
    private readonly SchedulingService _sut;

    public SchedulingServiceTests()
    {
        _sut = new SchedulingService(
            _handler,
            _equipmentRepository,
            _workOrderRepository,
            NullLogger<SchedulingService>.Instance);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RerouteWorkOrder_RejectsMissingWorkOrderNumber(string workOrderNumber)
    {
        var result = await RerouteAsync(workOrderNumber: workOrderNumber);

        Assert.Equal("Error: workOrderNumber is required and cannot be empty.", result);
        await _handler.DidNotReceive().HandleAsync(Arg.Any<RerouteWorkOrderCommand>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RerouteWorkOrder_RejectsMissingSourceEquipmentCode(string source)
    {
        var result = await RerouteAsync(sourceEquipmentCode: source);

        Assert.Equal("Error: sourceEquipmentCode is required and cannot be empty.", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RerouteWorkOrder_RejectsMissingTargetEquipmentCode(string target)
    {
        var result = await RerouteAsync(targetEquipmentCode: target);

        Assert.Equal("Error: targetEquipmentCode is required and cannot be empty.", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RerouteWorkOrder_RejectsMissingReason(string reason)
    {
        var result = await RerouteAsync(reason: reason);

        Assert.Equal(
            "Error: reason is required for audit compliance. Provide a clear operational justification.",
            result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RerouteWorkOrder_RejectsMissingDecisionReasoning(string reasoning)
    {
        var result = await RerouteAsync(decisionReasoning: reasoning);

        Assert.Equal("Error: decisionReasoning is required. Explain the AI agent's reasoning chain.", result);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public async Task RerouteWorkOrder_RejectsConfidenceOutsideZeroToOne(decimal confidence)
    {
        var result = await RerouteAsync(confidenceScore: confidence);

        Assert.Equal("Error: confidenceScore must be between 0.0 and 1.0.", result);
    }

    [Theory]
    [InlineData("CNC-01", "CNC-01")]
    [InlineData("CNC-01", "cnc-01")]
    [InlineData("  CNC-01  ", "CNC-01")]
    public async Task RerouteWorkOrder_RejectsIdenticalSourceAndTarget(
        string source,
        string target)
    {
        var result = await RerouteAsync(sourceEquipmentCode: source, targetEquipmentCode: target);

        Assert.Equal("Error: sourceEquipmentCode and targetEquipmentCode must be different.", result);
        await _workOrderRepository.DidNotReceive()
            .GetByNumberAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RerouteWorkOrder_ReturnsErrorWhenWorkOrderIsMissing()
    {
        _workOrderRepository.GetByNumberAsync("WO-missing", Arg.Any<CancellationToken>())
            .Returns((WorkOrder?)null);

        var result = await RerouteAsync(workOrderNumber: "WO-missing");

        Assert.Equal("Error: Work order 'WO-missing' was not found in the system.", result);
        await _handler.DidNotReceive().HandleAsync(Arg.Any<RerouteWorkOrderCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RerouteWorkOrder_ReturnsErrorWhenSourceEquipmentIsMissing()
    {
        var workOrder = TestFixtures.CreateWorkOrder();
        _workOrderRepository.GetByNumberAsync(workOrder.WorkOrderNumber, Arg.Any<CancellationToken>())
            .Returns(workOrder);
        _equipmentRepository.GetByCodeAsync("CNC-01", Arg.Any<CancellationToken>())
            .Returns((Equipment?)null);

        var result = await RerouteAsync(workOrderNumber: workOrder.WorkOrderNumber);

        Assert.Equal("Error: Source equipment with code 'CNC-01' was not found in the system.", result);
    }

    [Fact]
    public async Task RerouteWorkOrder_ReturnsErrorWhenTargetEquipmentIsMissing()
    {
        var workOrder = TestFixtures.CreateWorkOrder();
        var source = TestFixtures.CreateEquipment(code: "CNC-01");
        _workOrderRepository.GetByNumberAsync(workOrder.WorkOrderNumber, Arg.Any<CancellationToken>())
            .Returns(workOrder);
        _equipmentRepository.GetByCodeAsync("CNC-01", Arg.Any<CancellationToken>())
            .Returns(source);
        _equipmentRepository.GetByCodeAsync("CNC-02", Arg.Any<CancellationToken>())
            .Returns((Equipment?)null);

        var result = await RerouteAsync(workOrderNumber: workOrder.WorkOrderNumber);

        Assert.Equal("Error: Target equipment with code 'CNC-02' was not found in the system.", result);
        await _handler.DidNotReceive().HandleAsync(Arg.Any<RerouteWorkOrderCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RerouteWorkOrder_TrimsLookupAndCommandFieldsAndSendsHitlPendingCommand()
    {
        var workOrder = TestFixtures.CreateWorkOrder(number: "WO-2024-001");
        var source = TestFixtures.CreateEquipment(code: "CNC-01");
        var target = TestFixtures.CreateEquipment(code: "CNC-02");
        ArrangeRerouteLookups(workOrder, source, target);
        _handler.HandleAsync(Arg.Any<RerouteWorkOrderCommand>(), Arg.Any<CancellationToken>())
            .Returns(RerouteWorkOrderResult.Pending(
                workOrder.WorkOrderNumber, source.EquipmentCode, target.EquipmentCode, 80m, 20m, "fault", "AI-Agent"));

        await _sut.RerouteWorkOrderAsync(
            "  WO-2024-001  ",
            "  CNC-01  ",
            "  CNC-02  ",
            "  source faulted  ",
            "  CNC-02 is idle  ",
            1m);

        await _workOrderRepository.Received(1).GetByNumberAsync("WO-2024-001", Arg.Any<CancellationToken>());
        await _equipmentRepository.Received(1).GetByCodeAsync("CNC-01", Arg.Any<CancellationToken>());
        await _equipmentRepository.Received(1).GetByCodeAsync("CNC-02", Arg.Any<CancellationToken>());
        await _handler.Received(1).HandleAsync(
            Arg.Is<RerouteWorkOrderCommand>(c =>
                c.WorkOrderId == workOrder.Id &&
                c.SourceEquipmentId == source.Id &&
                c.TargetEquipmentId == target.Id &&
                c.Reason == "source faulted" &&
                c.TriggeredBy == "AI-Agent" &&
                c.ApprovalStatus == ApprovalStatus.PendingApproval &&
                c.DecisionReasoning == "CNC-02 is idle" &&
                c.ConfidenceScore == 1m),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RerouteWorkOrder_ReturnsFailedMessageWhenHandlerFails()
    {
        var workOrder = TestFixtures.CreateWorkOrder();
        var source = TestFixtures.CreateEquipment(code: "CNC-01");
        var target = TestFixtures.CreateEquipment(code: "CNC-02");
        ArrangeRerouteLookups(workOrder, source, target);
        _handler.HandleAsync(Arg.Any<RerouteWorkOrderCommand>(), Arg.Any<CancellationToken>())
            .Returns(RerouteWorkOrderResult.Failure("Target is faulted", "reason", "AI-Agent"));

        var result = await RerouteAsync(workOrderNumber: workOrder.WorkOrderNumber);

        Assert.Equal("Failed: Target is faulted", result);
    }

    [Fact]
    public async Task RerouteWorkOrder_FormatsPendingApprovalResult()
    {
        var workOrder = TestFixtures.CreateWorkOrder();
        var source = TestFixtures.CreateEquipment(code: "CNC-01");
        var target = TestFixtures.CreateEquipment(code: "CNC-02");
        var pending = RerouteWorkOrderResult.Pending(
            workOrder.WorkOrderNumber, source.EquipmentCode, target.EquipmentCode, 70m, 30m, "capacity", "AI-Agent");
        ArrangeRerouteLookups(workOrder, source, target);
        _handler.HandleAsync(Arg.Any<RerouteWorkOrderCommand>(), Arg.Any<CancellationToken>())
            .Returns(pending);

        var result = await RerouteAsync(workOrderNumber: workOrder.WorkOrderNumber);

        Assert.StartsWith(
            $"Success: Work order '{workOrder.WorkOrderNumber}' reroute from CNC-01 to CNC-02 is PENDING HUMAN APPROVAL (high-risk operation).",
            result);
        Assert.Contains("Remaining quantity: 70, Already produced: 30.", result);
        Assert.Contains("Reason: capacity.", result);
        Assert.Contains(pending.ExecutionTimestamp.ToString("O"), result);
    }

    [Fact]
    public async Task RerouteWorkOrder_FormatsExecutedResult()
    {
        var workOrder = TestFixtures.CreateWorkOrder();
        var source = TestFixtures.CreateEquipment(code: "CNC-01");
        var target = TestFixtures.CreateEquipment(code: "CNC-02");
        var executed = RerouteWorkOrderResult.Success(
            workOrder.WorkOrderNumber, source.EquipmentCode, target.EquipmentCode, 40m, 60m, "capacity", "AI-Agent");
        ArrangeRerouteLookups(workOrder, source, target);
        _handler.HandleAsync(Arg.Any<RerouteWorkOrderCommand>(), Arg.Any<CancellationToken>())
            .Returns(executed);

        var result = await RerouteAsync(workOrderNumber: workOrder.WorkOrderNumber);

        Assert.StartsWith(
            $"Success: Work order '{workOrder.WorkOrderNumber}' rerouted from CNC-01 to CNC-02.",
            result);
        Assert.Contains("Remaining quantity: 40, Already produced: 60.", result);
        Assert.Contains(executed.ExecutionTimestamp.ToString("O"), result);
    }

    [Fact]
    public async Task RerouteWorkOrder_FormatsApprovedStatusFallback()
    {
        var workOrder = TestFixtures.CreateWorkOrder();
        var source = TestFixtures.CreateEquipment(code: "CNC-01");
        var target = TestFixtures.CreateEquipment(code: "CNC-02");
        var approved = new RerouteWorkOrderResult(
            true,
            ApprovalStatus.Approved,
            null,
            workOrder.WorkOrderNumber,
            source.EquipmentCode,
            target.EquipmentCode,
            10m,
            90m,
            "capacity",
            "AI-Agent",
            DateTimeOffset.UtcNow);
        ArrangeRerouteLookups(workOrder, source, target);
        _handler.HandleAsync(Arg.Any<RerouteWorkOrderCommand>(), Arg.Any<CancellationToken>())
            .Returns(approved);

        var result = await RerouteAsync(workOrderNumber: workOrder.WorkOrderNumber);

        Assert.Equal(
            $"Success: Reroute recorded with status Approved. Work order: {workOrder.WorkOrderNumber}.",
            result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetAvailableAlternativeMachines_RejectsMissingEquipmentCode(string equipmentCode)
    {
        var result = await _sut.GetAvailableAlternativeMachinesAsync(equipmentCode);

        Assert.Equal("Error: equipmentCode is required and cannot be empty.", result);
        await _equipmentRepository.DidNotReceive().GetByCodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAvailableAlternativeMachines_ReturnsErrorWhenReferenceIsMissing()
    {
        _equipmentRepository.GetByCodeAsync("CNC-99", Arg.Any<CancellationToken>())
            .Returns((Equipment?)null);

        var result = await _sut.GetAvailableAlternativeMachinesAsync("CNC-99");

        Assert.Equal("Error: Equipment with code 'CNC-99' was not found in the system.", result);
        await _equipmentRepository.DidNotReceive().ListAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAvailableAlternativeMachines_ReportsWhenNoPeersQualify()
    {
        var parentId = Guid.NewGuid();
        var reference = TestFixtures.CreateEquipment(code: "CNC-01", parentId: parentId);
        _equipmentRepository.GetByCodeAsync("CNC-01", Arg.Any<CancellationToken>())
            .Returns(reference);
        _equipmentRepository.ListAsync(Arg.Any<CancellationToken>())
            .Returns([
                reference,
                TestFixtures.CreateEquipment(code: "CNC-03", parentId: parentId, state: EquipmentState.Faulted),
                TestFixtures.CreateEquipment(code: "CNC-04", parentId: parentId, state: EquipmentState.Maintenance),
                TestFixtures.CreateEquipment(code: "CNC-05", parentId: Guid.NewGuid()),
                TestFixtures.CreateEquipment(
                    code: "WC-ALT",
                    parentId: parentId,
                    level: EquipmentLevel.WorkCenter)
            ]);

        var result = await _sut.GetAvailableAlternativeMachinesAsync("CNC-01");

        Assert.Equal(
            $"No alternative equipment found for 'CNC-01' (Level: Equipment, Parent: {parentId}).",
            result);
    }

    [Fact]
    public async Task GetAvailableAlternativeMachines_ReportsNoneParentWhenReferenceHasNoParent()
    {
        var reference = TestFixtures.CreateEquipment(code: "SITE-1", level: EquipmentLevel.Site);
        _equipmentRepository.GetByCodeAsync("SITE-1", Arg.Any<CancellationToken>())
            .Returns(reference);
        _equipmentRepository.ListAsync(Arg.Any<CancellationToken>())
            .Returns([reference]);

        var result = await _sut.GetAvailableAlternativeMachinesAsync("SITE-1");

        Assert.Equal(
            "No alternative equipment found for 'SITE-1' (Level: Site, Parent: None).",
            result);
    }

    [Fact]
    public async Task GetAvailableAlternativeMachines_ListsIdleFirstThenBusyRunningPeers()
    {
        var parentId = Guid.NewGuid();
        var workOrderId = Guid.NewGuid();
        var reference = TestFixtures.CreateEquipment(code: "CNC-01", name: "CNC Mill 01", parentId: parentId);
        var idleLathe = TestFixtures.CreateEquipment(code: "LATHE-01", name: "Lathe 01", parentId: parentId);
        var idleCnc = TestFixtures.CreateEquipment(
            code: "CNC-05",
            name: "CNC Mill 05",
            parentId: parentId,
            state: EquipmentState.Idle);
        var busy = TestFixtures.CreateEquipment(
            code: "CNC-02",
            name: "CNC Mill 02",
            parentId: parentId,
            state: EquipmentState.Running,
            currentWorkOrderId: workOrderId);
        var setup = TestFixtures.CreateEquipment(
            code: "CNC-06",
            name: "CNC Mill 06",
            parentId: parentId,
            state: EquipmentState.Setup);

        _equipmentRepository.GetByCodeAsync("CNC-01", Arg.Any<CancellationToken>())
            .Returns(reference);
        _equipmentRepository.ListAsync(Arg.Any<CancellationToken>())
            .Returns([reference, busy, setup, idleLathe, idleCnc]);

        var result = await _sut.GetAvailableAlternativeMachinesAsync("CNC-01");

        Assert.Contains(
            "Alternative equipment for 'CNC-01' (Level: Equipment, Hierarchy: AREA-1 > WC-1):",
            result);
        Assert.Contains("- CNC-05 (CNC Mill 05): State=Idle, AVAILABLE (Idle, no work order)", result);
        Assert.Contains("- LATHE-01 (Lathe 01): State=Idle, AVAILABLE (Idle, no work order)", result);
        Assert.Contains($"- CNC-02 (CNC Mill 02): State=Running, BUSY (Running with work order {workOrderId})", result);
        Assert.Contains("- CNC-06 (CNC Mill 06): State=Setup, Available but Setup", result);
        Assert.Contains("Total alternatives found: 4", result);

        var idleCncIndex = result.IndexOf("CNC-05", StringComparison.Ordinal);
        var idleLatheIndex = result.IndexOf("LATHE-01", StringComparison.Ordinal);
        var busyIndex = result.IndexOf("CNC-02", StringComparison.Ordinal);
        Assert.True(idleCncIndex < idleLatheIndex);
        Assert.True(idleLatheIndex < busyIndex);
    }

    [Fact]
    public async Task RerouteWorkOrder_ForwardsCancellationToken()
    {
        var workOrder = TestFixtures.CreateWorkOrder();
        var source = TestFixtures.CreateEquipment(code: "CNC-01");
        var target = TestFixtures.CreateEquipment(code: "CNC-02");
        using var cts = new CancellationTokenSource();
        _workOrderRepository.GetByNumberAsync(workOrder.WorkOrderNumber, cts.Token)
            .Returns(workOrder);
        _equipmentRepository.GetByCodeAsync("CNC-01", cts.Token).Returns(source);
        _equipmentRepository.GetByCodeAsync("CNC-02", cts.Token).Returns(target);
        _handler.HandleAsync(Arg.Any<RerouteWorkOrderCommand>(), cts.Token)
            .Returns(RerouteWorkOrderResult.Pending(
                workOrder.WorkOrderNumber, "CNC-01", "CNC-02", 1m, 0m, "reason", "AI-Agent"));

        await _sut.RerouteWorkOrderAsync(
            workOrder.WorkOrderNumber,
            "CNC-01",
            "CNC-02",
            "reason",
            "reasoning",
            0.5m,
            cts.Token);

        await _workOrderRepository.Received(1).GetByNumberAsync(workOrder.WorkOrderNumber, cts.Token);
        await _handler.Received(1).HandleAsync(Arg.Any<RerouteWorkOrderCommand>(), cts.Token);
    }

    private Task<string> RerouteAsync(
        string workOrderNumber = "WO-2024-001",
        string sourceEquipmentCode = "CNC-01",
        string targetEquipmentCode = "CNC-02",
        string reason = "capacity",
        string decisionReasoning = "idle peer available",
        decimal confidenceScore = 0.9m) =>
        _sut.RerouteWorkOrderAsync(
            workOrderNumber,
            sourceEquipmentCode,
            targetEquipmentCode,
            reason,
            decisionReasoning,
            confidenceScore);

    private void ArrangeRerouteLookups(WorkOrder workOrder, Equipment source, Equipment target)
    {
        _workOrderRepository.GetByNumberAsync(workOrder.WorkOrderNumber, Arg.Any<CancellationToken>())
            .Returns(workOrder);
        _equipmentRepository.GetByCodeAsync(source.EquipmentCode, Arg.Any<CancellationToken>())
            .Returns(source);
        _equipmentRepository.GetByCodeAsync(target.EquipmentCode, Arg.Any<CancellationToken>())
            .Returns(target);
    }
}
