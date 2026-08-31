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

public sealed class MachineControlServiceTests
{
    private readonly ICommandHandler<ChangeMachineStateCommand, ChangeMachineStateResult> _handler =
        Substitute.For<ICommandHandler<ChangeMachineStateCommand, ChangeMachineStateResult>>();

    private readonly IEquipmentRepository _equipmentRepository = Substitute.For<IEquipmentRepository>();

    private readonly MachineControlService _sut;

    public MachineControlServiceTests()
    {
        _sut = new MachineControlService(
            _handler,
            _equipmentRepository,
            NullLogger<MachineControlService>.Instance);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SetMachineState_RejectsMissingEquipmentCode(string equipmentCode)
    {
        var result = await _sut.SetMachineStateAsync(
            equipmentCode, "Idle", "reason", "reasoning", 0.9m);

        Assert.Equal("Error: equipmentCode is required and cannot be empty.", result);
        await _handler.DidNotReceive().HandleAsync(Arg.Any<ChangeMachineStateCommand>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SetMachineState_RejectsMissingTargetState(string targetState)
    {
        var result = await _sut.SetMachineStateAsync(
            "CNC-01", targetState, "reason", "reasoning", 0.9m);

        Assert.Equal("Error: targetState is required and cannot be empty.", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SetMachineState_RejectsMissingReason(string reason)
    {
        var result = await _sut.SetMachineStateAsync(
            "CNC-01", "Idle", reason, "reasoning", 0.9m);

        Assert.Equal(
            "Error: reason is required for audit compliance. Provide a clear operational justification.",
            result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SetMachineState_RejectsMissingDecisionReasoning(string reasoning)
    {
        var result = await _sut.SetMachineStateAsync(
            "CNC-01", "Idle", "reason", reasoning, 0.9m);

        Assert.Equal("Error: decisionReasoning is required. Explain the AI agent's reasoning chain.", result);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public async Task SetMachineState_RejectsConfidenceOutsideZeroToOne(decimal confidence)
    {
        var result = await _sut.SetMachineStateAsync(
            "CNC-01", "Idle", "reason", "reasoning", confidence);

        Assert.Equal("Error: confidenceScore must be between 0.0 and 1.0.", result);
    }

    [Fact]
    public async Task SetMachineState_RejectsUnknownTargetState()
    {
        var result = await _sut.SetMachineStateAsync(
            "CNC-01", "Offline", "reason", "reasoning", 0.9m);

        Assert.Equal(
            "Error: Invalid targetState 'Offline'. Valid values: Idle, Running, Faulted, Maintenance, Setup.",
            result);
        await _equipmentRepository.DidNotReceive()
            .GetByCodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetMachineState_ReturnsErrorWhenEquipmentIsMissing()
    {
        _equipmentRepository.GetByCodeAsync("CNC-99", Arg.Any<CancellationToken>())
            .Returns((Equipment?)null);

        var result = await _sut.SetMachineStateAsync(
            "CNC-99", "Idle", "reason", "reasoning", 0.9m);

        Assert.Equal("Error: Equipment with code 'CNC-99' was not found in the system.", result);
        await _handler.DidNotReceive().HandleAsync(Arg.Any<ChangeMachineStateCommand>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("idle", EquipmentState.Idle)]
    [InlineData("RUNNING", EquipmentState.Running)]
    [InlineData("Faulted", EquipmentState.Faulted)]
    [InlineData("maintenance", EquipmentState.Maintenance)]
    [InlineData("Setup", EquipmentState.Setup)]
    public async Task SetMachineState_ParsesTargetStateIgnoringCase(
        string targetState,
        EquipmentState expected)
    {
        var equipment = TestFixtures.CreateEquipment();
        _equipmentRepository.GetByCodeAsync(equipment.EquipmentCode, Arg.Any<CancellationToken>())
            .Returns(equipment);
        _handler.HandleAsync(Arg.Any<ChangeMachineStateCommand>(), Arg.Any<CancellationToken>())
            .Returns(ChangeMachineStateResult.Pending(
                equipment.EquipmentCode, EquipmentState.Idle, expected, "reason", "AI-Agent"));

        await _sut.SetMachineStateAsync(
            equipment.EquipmentCode, targetState, "reason", "reasoning", 0.8m);

        await _handler.Received(1).HandleAsync(
            Arg.Is<ChangeMachineStateCommand>(c => c.TargetState == expected),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetMachineState_TrimsLookupAndCommandFieldsAndSendsHitlPendingCommand()
    {
        var equipment = TestFixtures.CreateEquipment(code: "CNC-01");
        _equipmentRepository.GetByCodeAsync("CNC-01", Arg.Any<CancellationToken>())
            .Returns(equipment);
        _handler.HandleAsync(Arg.Any<ChangeMachineStateCommand>(), Arg.Any<CancellationToken>())
            .Returns(ChangeMachineStateResult.Pending(
                equipment.EquipmentCode, EquipmentState.Idle, EquipmentState.Maintenance, "spindle wear", "AI-Agent"));

        await _sut.SetMachineStateAsync(
            "  CNC-01  ",
            "Maintenance",
            "  spindle wear  ",
            "  vibration trend  ",
            0m);

        await _handler.Received(1).HandleAsync(
            Arg.Is<ChangeMachineStateCommand>(c =>
                c.EquipmentId == equipment.Id &&
                c.TargetState == EquipmentState.Maintenance &&
                c.Reason == "spindle wear" &&
                c.TriggeredBy == "AI-Agent" &&
                c.ApprovalStatus == ApprovalStatus.PendingApproval &&
                c.DecisionReasoning == "vibration trend" &&
                c.ConfidenceScore == 0m),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetMachineState_ReturnsFailedMessageWhenHandlerFails()
    {
        var equipment = TestFixtures.CreateEquipment();
        _equipmentRepository.GetByCodeAsync(equipment.EquipmentCode, Arg.Any<CancellationToken>())
            .Returns(equipment);
        _handler.HandleAsync(Arg.Any<ChangeMachineStateCommand>(), Arg.Any<CancellationToken>())
            .Returns(ChangeMachineStateResult.Failure("Illegal transition", "reason", "AI-Agent"));

        var result = await _sut.SetMachineStateAsync(
            equipment.EquipmentCode, "Running", "reason", "reasoning", 1m);

        Assert.Equal("Failed: Illegal transition", result);
    }

    [Fact]
    public async Task SetMachineState_FormatsPendingApprovalResult()
    {
        var equipment = TestFixtures.CreateEquipment();
        var pending = ChangeMachineStateResult.Pending(
            equipment.EquipmentCode, EquipmentState.Running, EquipmentState.Faulted, "overtemp", "AI-Agent");
        _equipmentRepository.GetByCodeAsync(equipment.EquipmentCode, Arg.Any<CancellationToken>())
            .Returns(equipment);
        _handler.HandleAsync(Arg.Any<ChangeMachineStateCommand>(), Arg.Any<CancellationToken>())
            .Returns(pending);

        var result = await _sut.SetMachineStateAsync(
            equipment.EquipmentCode, "Faulted", "overtemp", "thermal trip", 0.95m);

        Assert.StartsWith(
            $"Success: State transition '{equipment.EquipmentCode}' from Running to Faulted is PENDING HUMAN APPROVAL.",
            result);
        Assert.Contains("This is a high-risk action.", result);
        Assert.Contains("Reason: overtemp.", result);
        Assert.Contains(pending.ExecutionTimestamp.ToString("O"), result);
    }

    [Fact]
    public async Task SetMachineState_FormatsExecutedResult()
    {
        var equipment = TestFixtures.CreateEquipment();
        var executed = ChangeMachineStateResult.Success(
            equipment.EquipmentCode, EquipmentState.Idle, EquipmentState.Running, "dispatch", "AI-Agent");
        _equipmentRepository.GetByCodeAsync(equipment.EquipmentCode, Arg.Any<CancellationToken>())
            .Returns(equipment);
        _handler.HandleAsync(Arg.Any<ChangeMachineStateCommand>(), Arg.Any<CancellationToken>())
            .Returns(executed);

        var result = await _sut.SetMachineStateAsync(
            equipment.EquipmentCode, "Running", "dispatch", "queue ready", 0.7m);

        Assert.StartsWith(
            $"Success: Equipment '{equipment.EquipmentCode}' transitioned from Idle to Running.",
            result);
        Assert.Contains("Reason: dispatch.", result);
        Assert.Contains(executed.ExecutionTimestamp.ToString("O"), result);
    }

    [Fact]
    public async Task SetMachineState_FormatsApprovedStatusFallback()
    {
        var equipment = TestFixtures.CreateEquipment();
        var approved = new ChangeMachineStateResult(
            true,
            ApprovalStatus.Approved,
            null,
            equipment.EquipmentCode,
            EquipmentState.Idle,
            EquipmentState.Setup,
            "changeover",
            "AI-Agent",
            DateTimeOffset.UtcNow);
        _equipmentRepository.GetByCodeAsync(equipment.EquipmentCode, Arg.Any<CancellationToken>())
            .Returns(equipment);
        _handler.HandleAsync(Arg.Any<ChangeMachineStateCommand>(), Arg.Any<CancellationToken>())
            .Returns(approved);

        var result = await _sut.SetMachineStateAsync(
            equipment.EquipmentCode, "Setup", "changeover", "sku switch", 0.6m);

        Assert.Equal(
            $"Success: State change recorded with status Approved. Equipment: {equipment.EquipmentCode}.",
            result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetMachineTelemetryStatus_RejectsMissingEquipmentCode(string equipmentCode)
    {
        var result = await _sut.GetMachineTelemetryStatusAsync(equipmentCode);

        Assert.Equal("Error: equipmentCode is required and cannot be empty.", result);
        await _equipmentRepository.DidNotReceive()
            .GetByCodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetMachineTelemetryStatus_ReturnsErrorWhenEquipmentIsMissing()
    {
        _equipmentRepository.GetByCodeAsync("CNC-99", Arg.Any<CancellationToken>())
            .Returns((Equipment?)null);

        var result = await _sut.GetMachineTelemetryStatusAsync("CNC-99");

        Assert.Equal("Error: Equipment with code 'CNC-99' was not found in the system.", result);
    }

    [Fact]
    public async Task GetMachineTelemetryStatus_ReportsIdleEquipmentWithoutWorkOrder()
    {
        var parentId = Guid.NewGuid();
        var equipment = TestFixtures.CreateEquipment(
            code: "CNC-01",
            name: "CNC Mill 01",
            parentId: parentId,
            state: EquipmentState.Idle);

        _equipmentRepository.GetByCodeAsync("CNC-01", Arg.Any<CancellationToken>())
            .Returns(equipment);

        var result = await _sut.GetMachineTelemetryStatusAsync("  CNC-01  ");

        Assert.Contains("Equipment: CNC-01 (CNC Mill 01)", result);
        Assert.Contains("Current State: Idle", result);
        Assert.Contains($"Hierarchy: Equipment under Parent ID: {parentId}", result);
        Assert.Contains("Assigned Work Order: None (equipment is available)", result);
        Assert.DoesNotContain("Last Fault Reason:", result);
        Assert.Contains("Hierarchy Path: ENT-1 > SITE-1 > AREA-1 > WC-1", result);
        Assert.Contains("ago (at", result);
    }

    [Fact]
    public async Task GetMachineTelemetryStatus_ReportsAssignedWorkOrderAndFaultReason()
    {
        var workOrderId = Guid.NewGuid();
        var equipment = TestFixtures.CreateEquipment(
            code: "CNC-03",
            name: "CNC Mill 03",
            state: EquipmentState.Faulted,
            currentWorkOrderId: workOrderId,
            lastFaultReason: "overtemperature");

        _equipmentRepository.GetByCodeAsync("CNC-03", Arg.Any<CancellationToken>())
            .Returns(equipment);

        var result = await _sut.GetMachineTelemetryStatusAsync("CNC-03");

        Assert.Contains($"Assigned Work Order ID: {workOrderId}", result);
        Assert.Contains("Last Fault Reason: overtemperature", result);
        Assert.Contains("Hierarchy: Equipment under Parent ID: None", result);
    }

    [Fact]
    public async Task SetMachineState_ForwardsCancellationToken()
    {
        var equipment = TestFixtures.CreateEquipment();
        using var cts = new CancellationTokenSource();
        _equipmentRepository.GetByCodeAsync(equipment.EquipmentCode, cts.Token)
            .Returns(equipment);
        _handler.HandleAsync(Arg.Any<ChangeMachineStateCommand>(), cts.Token)
            .Returns(ChangeMachineStateResult.Pending(
                equipment.EquipmentCode, EquipmentState.Idle, EquipmentState.Idle, "reason", "AI-Agent"));

        await _sut.SetMachineStateAsync(
            equipment.EquipmentCode, "Idle", "reason", "reasoning", 0.5m, cts.Token);

        await _equipmentRepository.Received(1).GetByCodeAsync(equipment.EquipmentCode, cts.Token);
        await _handler.Received(1).HandleAsync(Arg.Any<ChangeMachineStateCommand>(), cts.Token);
    }
}
