using System.Collections.Concurrent;
using System.Text;
using AgenticMES.Application.Common.Interfaces;
using AgenticMES.Application.CQRS;
using AgenticMES.Application.CQRS.Commands;
using AgenticMES.Application.DTOs;
using AgenticMES.Domain.Entities;
using AgenticMES.Domain.Enums;
using AgenticMES.Domain.Events;
using AgenticMES.Infrastructure.Ai;
using AgenticMES.Infrastructure.Persistence;
using AgenticMES.Infrastructure.Simulation;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace AgenticMES.ConsoleApp;

/// <summary>
/// Operator-facing control-room demo for an interview vertical slice.
/// Each beat waits for Enter so the presenter can talk through ISA-95 state, OEE, telemetry, and the incident.
/// </summary>
public sealed class IncidentSimulationWorkflow(
    IEquipmentRepository equipmentRepository,
    IWorkOrderRepository workOrderRepository,
    IOeeCalculatorService oeeCalculator,
    ITelemetryStreamer telemetryStreamer,
    ITelemetrySimulationController simulationController,
    Func<MesAgentOrchestrator> orchestratorFactory,
    IHitlApprovalService hitlApprovalService,
    ICommandHandler<ChangeMachineStateCommand, ChangeMachineStateResult> machineStateHandler,
    ICommandHandler<RerouteWorkOrderCommand, RerouteWorkOrderResult> rerouteHandler,
    DemoLogCapture logCapture,
    DailyFileLoggerProvider fileLogger)
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);
    private const int LiveViewHeight = 13;
    private const int AiLogViewHeight = 14;

    private readonly DateTimeOffset _shiftStartedAt = DateTimeOffset.UtcNow.AddMinutes(-35);
    private readonly ConcurrentDictionary<Guid, MachineTelemetry> _telemetry = new();
    private readonly ConcurrentQueue<string> _eventLog = new();
    private readonly Dictionary<Guid, double> _lastProducedTelemetry = [];
    private readonly TelemetryIncidentCapture _incidentCapture = new();
    private readonly object _sync = new();

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        RenderSplash();
        await WaitUntilEnterAsync(
            "Press [bold]Enter[/] to open the live machine / OEE board",
            cancellationToken);

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var streamTask = Task.CompletedTask;
        var consumeTask = Task.CompletedTask;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            if (CanUseLiveDisplay())
            {
                TryClear();
                var previousWidth = AnsiConsole.Profile.Width;
                AnsiConsole.Cursor.Hide();
                SetTerminalAutowrap(false);
                try
                {
                    var windowWidth = ReadWindowWidth();
                    if (windowWidth > 4)
                    {
                        AnsiConsole.Profile.Width = windowWidth - 1;
                    }

                    await AnsiConsole.Live(BuildLiveView(
                            DemoPhase.FloorBoard,
                            clock.Elapsed,
                            SnapshotMachines(),
                            "Press Enter to start the OPC UA telemetry stream"))
                        .AutoClear(false)
                        .Overflow(VerticalOverflow.Ellipsis)
                        .StartAsync(async ctx =>
                        {
                            await RunPresenterStepsAsync(
                                runCts.Token,
                                clock,
                                startStreaming: () =>
                                {
                                    streamTask = telemetryStreamer.RunAsync(runCts.Token);
                                    consumeTask = ConsumeTelemetryAsync(runCts.Token);
                                },
                                publish: (phase, elapsed, prompt) =>
                                {
                                    ctx.UpdateTarget(BuildLiveView(phase, elapsed, SnapshotMachines(), prompt));
                                    return Task.CompletedTask;
                                });
                        });
                }
                finally
                {
                    SetTerminalAutowrap(true);
                    AnsiConsole.Profile.Width = previousWidth;
                    AnsiConsole.Cursor.Show();
                }
            }
            else
            {
                await RunPresenterStepsAsync(
                    runCts.Token,
                    clock,
                    startStreaming: () =>
                    {
                        streamTask = telemetryStreamer.RunAsync(runCts.Token);
                        consumeTask = ConsumeTelemetryAsync(runCts.Token);
                    },
                    publish: (phase, elapsed, prompt) =>
                    {
                        AnsiConsole.Write(BuildLiveView(phase, elapsed, SnapshotMachines(), prompt));
                        AnsiConsole.WriteLine();
                        return Task.CompletedTask;
                    },
                    refreshWhileWaiting: false);
            }
        }
        catch (OperationCanceledException)
        {
            await runCts.CancelAsync();
        }
        catch (IOException ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Console live display unavailable:[/] {Markup.Escape(ex.Message)}");
        }

        await runCts.CancelAsync();
        await Task.WhenAll(IgnoreCancel(streamTask), IgnoreCancel(consumeTask));

        if (!cancellationToken.IsCancellationRequested)
        {
            await RenderEpilogueAsync(cancellationToken);
        }
    }

    private async Task RunPresenterStepsAsync(
        CancellationToken cancellationToken,
        System.Diagnostics.Stopwatch clock,
        Action startStreaming,
        Func<DemoPhase, TimeSpan, string, Task> publish,
        bool refreshWhileWaiting = true)
    {
        await HoldStepAsync(
            DemoPhase.FloorBoard,
            "Press Enter to start the OPC UA telemetry stream",
            clock,
            publish,
            cancellationToken,
            refreshWhileWaiting);

        startStreaming();
        EnqueueEvent("[bold aqua]OPC UA mock online[/]  Temp / Scrap / Coolant / Vibration / SpindleLoad @ 400 ms");

        await HoldStepAsync(
            DemoPhase.Streaming,
            "Press Enter to inject the CNC-01 thermal anomaly",
            clock,
            publish,
            cancellationToken,
            refreshWhileWaiting);

        simulationController.InjectThermalAnomaly(
            DemoPlantCatalog.Cnc01Id,
            peakTemperatureCelsius: 128.0,
            peakScrapRatePercent: 24.0);
        EnqueueEvent("[bold red]INJECT[/]  thermal disturbance on CNC-01 — waiting for OPC UA tags to cross alarm");

        await HoldStepAsync(
            DemoPhase.Anomaly,
            "Press Enter to close the incident",
            clock,
            publish,
            cancellationToken,
            refreshWhileWaiting);
    }

    private async Task HoldStepAsync(
        DemoPhase phase,
        string prompt,
        System.Diagnostics.Stopwatch clock,
        Func<DemoPhase, TimeSpan, string, Task> publish,
        CancellationToken cancellationToken,
        bool refreshWhileWaiting)
    {
        DrainBufferedKeys();
        await publish(phase, clock.Elapsed, prompt);

        if (!CanReadKeys())
        {
            await Task.Delay(800, cancellationToken);
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            if (TryConsumeEnter())
            {
                return;
            }

            if (refreshWhileWaiting)
            {
                await publish(phase, clock.Elapsed, prompt);
            }

            await Task.Delay(RefreshInterval, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private void RenderSplash()
    {
        TryClear();
        AnsiConsole.Write(new FigletText("AGENTIC MES").Centered().Color(Color.Aqua));
        AnsiConsole.Write(new Rule("[bold gold1]Krakow Plant  ·  Machining Area  ·  CNC Cell 1[/]").RuleStyle("grey35").Centered());
        AnsiConsole.WriteLine();

        var tree = new Tree("[bold]ISA-95 equipment hierarchy[/]")
            .Style("grey70");
        var enterprise = tree.AddNode("[yellow]ENT-ACME[/]  ACME Manufacturing Group");
        var site = enterprise.AddNode("[aqua]SITE-KRK[/]  Krakow Plant");
        var area = site.AddNode("[blue]AREA-MACH[/]  Machining Area");
        var cell = area.AddNode("[gold1]WC-CNC-01[/]  CNC Cell 1");
        cell.AddNode("[green]CNC-01[/]  Haas ST-20Y CNC Lathe");
        cell.AddNode("[grey]CNC-02[/]  Haas VF-2SS Vertical Mill");
        cell.AddNode("[red]CNC-03[/]  Haas VF-3 Vertical Mill  [red](faulted)[/]");

        var scenario = new Markup(
            "[bold]Interview vertical slice[/]  — press [bold]Enter[/] after each beat\n" +
            "[grey]1.[/] Live machine state + ISO 22400 OEE board\n" +
            "[grey]2.[/] Normal telemetry stream (OPC UA / MQTT mock)\n" +
            "[grey]3.[/] Thermal runaway on CNC-01 with scrap-rate growth\n" +
            "[grey]4.[/] AI agent diagnoses the incident and writes an audit report\n\n" +
            "[grey]Ctrl+C aborts.[/]");

        AnsiConsole.Write(new Columns(
            new Panel(tree).Header(" Plant ").BorderColor(Color.Aqua).Padding(1, 0),
            new Panel(scenario).Header(" Scenario ").BorderColor(Color.Gold1).Padding(1, 0).Expand()));
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]In-memory Krakow plant seeded. Present the hierarchy, then continue.[/]");
        AnsiConsole.WriteLine();
    }

    private IRenderable BuildLiveView(
        DemoPhase phase,
        TimeSpan elapsed,
        IReadOnlyList<MachineRow> rows,
        string prompt)
    {
        var (title, accent) = phase switch
        {
            DemoPhase.FloorBoard => ("STEP 1/3  LIVE MACHINE STATE & OEE", "aqua"),
            DemoPhase.Streaming => ("STEP 2/3  TELEMETRY STREAM", "green"),
            DemoPhase.Anomaly => ("STEP 3/3  INCIDENT — CNC-01 THERMAL RUNAWAY", "red"),
            _ => ("AGENTIC MES", "grey")
        };

        var steps =
            $"{StepDot(phase >= DemoPhase.FloorBoard, "Board")}  " +
            $"{StepDot(phase >= DemoPhase.Streaming, "Stream")}  " +
            $"{StepDot(phase >= DemoPhase.Anomaly, "Anomaly")}";

        var table = new Table()
            .Title($"[bold {accent}]Agentic MES[/]  [grey]· shop-floor control room[/]")
            .Caption($"[bold {accent}]{title}[/]   {steps}   [grey]t+{elapsed:mm\\:ss}[/]")
            .Border(TableBorder.Rounded)
            .BorderColor(phase is DemoPhase.Anomaly ? Color.Red : Color.Grey)
            .AddColumn(new TableColumn("[bold]Machine[/]").NoWrap())
            .AddColumn(new TableColumn("[bold]State[/]").Centered().NoWrap())
            .AddColumn(new TableColumn("[bold]Order[/]").NoWrap())
            .AddColumn(new TableColumn("[bold]A[/]").RightAligned().NoWrap())
            .AddColumn(new TableColumn("[bold]P[/]").RightAligned().NoWrap())
            .AddColumn(new TableColumn("[bold]Q[/]").RightAligned().NoWrap())
            .AddColumn(new TableColumn("[bold]OEE[/]").RightAligned().NoWrap())
            .AddColumn(new TableColumn("[bold]Temp[/]").RightAligned().NoWrap())
            .AddColumn(new TableColumn("[bold]Coolant[/]").RightAligned().NoWrap())
            .AddColumn(new TableColumn("[bold]Scrap[/]").RightAligned().NoWrap());

        foreach (var row in rows)
        {
            var code = row.IsAnomalous ? $"[bold red]{row.Code} ⚠[/]" : $"[bold]{row.Code}[/]";
            table.AddRow(
                code,
                StateBadge(row.State),
                row.WorkOrder,
                FactorMarkup(row.AvailabilityPercent),
                FactorMarkup(row.PerformancePercent),
                FactorMarkup(row.QualityPercent, invertLowOnAnomaly: row.IsAnomalous),
                OeeMarkup(row.OeePercent),
                TemperatureMarkup(row.Temperature, row.IsAnomalous),
                CoolantMarkup(row.CoolantPressure, row.IsAnomalous),
                ScrapMarkup(row.ScrapRate, row.IsAnomalous));
        }

        var events = _eventLog.ToArray();
        var latest = events.Length == 0
            ? "[grey]Awaiting telemetry…[/]"
            : events[^1];

        return new FixedHeightRenderable(
            new Rows(
                table,
                new Markup(latest),
                new Markup($"[bold aqua]▸ {Markup.Escape(prompt)}[/]")),
            height: LiveViewHeight);
    }

    private async Task RenderEpilogueAsync(CancellationToken cancellationToken)
    {
        var rows = SnapshotMachines();
        _incidentCapture.TryDescribe(simulationController.AnomalousEquipmentId, out var captured);
        var victim = rows.FirstOrDefault(r => r.Code == captured?.EquipmentCode)
                     ?? rows.FirstOrDefault(r => r.IsAnomalous)
                     ?? rows.FirstOrDefault(r => r.Code == "CNC-01");

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold red]Incident closed — summary[/]").RuleStyle("red").Centered());
        AnsiConsole.WriteLine();

        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[bold]Asset[/]", victim is null ? "[grey]n/a[/]" : $"{Markup.Escape(victim.Code)}  {Markup.Escape(victim.Name)}");
        grid.AddRow("[bold]Trigger[/]", captured is null
            ? "[grey]no alarm crossed in the captured window[/]"
            : Markup.Escape($"{captured.PrimarySymptom} first seen {captured.DetectedAt:HH:mm:ss} UTC"));
        grid.AddRow("[bold]Peak temperature[/]", FormatPeak(captured?.PeakTemperature ?? victim?.Temperature, "°C", alert: true));
        grid.AddRow("[bold]Peak scrap rate[/]", FormatPeak(captured?.PeakScrapRate ?? victim?.ScrapRate, "%", alert: true));
        grid.AddRow("[bold]Coolant pressure[/]", FormatPeak(captured?.LatestCoolantPressure ?? victim?.CoolantPressure, "PSI", alert: (captured?.LatestCoolantPressure ?? victim?.CoolantPressure) < TelemetryIncidentCapture.CoolantLowPsi));
        grid.AddRow("[bold]Peak vibration[/]", FormatPeak(captured?.PeakVibration, "in/s", alert: captured?.PeakVibration >= TelemetryIncidentCapture.VibrationAlarmInPerSec));
        grid.AddRow("[bold]OEE after incident[/]", victim?.OeePercent is { } oee ? OeeMarkup(oee) : "[grey]n/a[/]");
        grid.AddRow("[bold]Quality factor[/]", victim?.QualityPercent is { } q ? FactorMarkup(q, invertLowOnAnomaly: true) : "[grey]n/a[/]");

        AnsiConsole.Write(new Panel(grid)
            .Header($" {captured?.EquipmentCode ?? victim?.Code ?? "Incident"} from telemetry window ")
            .BorderColor(Color.Red)
            .Padding(1, 0));

        AnsiConsole.WriteLine();
        await WaitUntilEnterAsync("Press [bold]Enter[/] to dispatch the AI agent", cancellationToken);
        await RunAiIncidentResponseAsync(cancellationToken);
    }

    private async Task RunAiIncidentResponseAsync(CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold gold1]STEP 4/4  AI INCIDENT RESPONSE[/]").RuleStyle("gold1").Centered());
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Building AnomalyContext from the captured OPC UA window, then invoking ProcessAnomalyAsync.[/]\n");

        MesAgentOrchestrator orchestrator;
        try
        {
            orchestrator = orchestratorFactory();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            var message = ex.InnerException?.Message ?? ex.Message;
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");
            AnsiConsole.MarkupLine("[grey]Shop-floor demo finished without the AI step.[/]\n");
            await WaitUntilEnterAsync("Press [bold]Enter[/] to finish the demo", cancellationToken);
            return;
        }

        var anomalyContext = await BuildAnomalyContextAsync(cancellationToken);
        if (anomalyContext is null)
        {
            AnsiConsole.MarkupLine("[red]No telemetry window was captured. Start the OPC UA stream before dispatching the AI agent.[/]\n");
            await WaitUntilEnterAsync("Press [bold]Enter[/] to finish the demo", cancellationToken);
            return;
        }

        RenderAnomalyContext(anomalyContext);

        AnsiConsole.MarkupLine("[grey]Agent logs (Semantic Kernel + MES tools) appear below while the model works.[/]\n");

        var report = await InvokeAgentWithLiveLogsAsync(orchestrator, anomalyContext, cancellationToken);
        
        await ProcessPendingApprovalsAsync(cancellationToken);
        
        var reportPath = await PersistReportAsync(report, cancellationToken);
        RenderIncidentReport(report, reportPath, fileLogger.CurrentFilePath);

        await RenderFinalShopFloorStateAsync(cancellationToken);
        
        await WaitUntilEnterAsync("Press [bold]Enter[/] to finish the demo", cancellationToken);
    }

    private async Task ProcessPendingApprovalsAsync(CancellationToken cancellationToken)
    {
        var pending = hitlApprovalService.GetPendingRequests();
        if (pending.Count == 0)
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold yellow]HUMAN-IN-THE-LOOP APPROVAL REQUIRED[/]").RuleStyle("yellow").Centered());
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[grey]The AI agent requested {pending.Count} high-risk action(s) that require operator approval.[/]\n");

        foreach (var request in pending)
        {
            RenderApprovalRequest(request);
            var approved = await PromptForApprovalAsync(cancellationToken);

            if (approved)
            {
                hitlApprovalService.Approve(request.RequestId);
                await ExecuteApprovedActionAsync(request, cancellationToken);
            }
            else
            {
                AnsiConsole.MarkupLine("[red]Action REJECTED by operator.[/]\n");
            }
        }

        hitlApprovalService.Clear();
    }

    private static void RenderApprovalRequest(PendingApprovalRequest request)
    {
        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[bold]Action type[/]", Markup.Escape(request.ActionType.ToString()));
        grid.AddRow("[bold]Description[/]", Markup.Escape(request.Description));
        grid.AddRow("[bold]Reason[/]", Markup.Escape(request.Reason));
        grid.AddRow("[bold]AI confidence[/]", $"{request.ConfidenceScore:P0}");
        grid.AddRow("[bold]AI reasoning[/]", Markup.Escape(request.DecisionReasoning));

        AnsiConsole.Write(new Panel(grid)
            .Header(" Pending High-Risk Action ")
            .BorderColor(Color.Yellow)
            .Padding(1, 0));
        AnsiConsole.WriteLine();
    }

    private static async Task<bool> PromptForApprovalAsync(CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[bold yellow]Approve this action? Type 'Y' to approve, any other key to reject:[/]");

        DrainBufferedKeys();

        if (!CanReadKeys())
        {
            await Task.Delay(400, cancellationToken);
            return false;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            if (TryReadKey(out var key))
            {
                var approved = key is ConsoleKey.Y;
                AnsiConsole.MarkupLine(approved
                    ? "[green]✓ APPROVED[/]\n"
                    : "[red]✗ REJECTED[/]\n");
                return approved;
            }

            await Task.Delay(50, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    private async Task ExecuteApprovedActionAsync(PendingApprovalRequest request, CancellationToken cancellationToken)
    {
        try
        {
            switch (request.ActionType)
            {
                case ActionType.SetMachineState:
                    var stateCommand = (ChangeMachineStateCommand)request.CommandData;
                    var approvedStateCommand = stateCommand with { ApprovalStatus = ApprovalStatus.Approved };
                    var stateResult = await machineStateHandler.HandleAsync(approvedStateCommand, cancellationToken);

                    if (stateResult.IsSuccess)
                    {
                        AnsiConsole.MarkupLine($"[green]✓ Executed: {Markup.Escape(request.Description)}[/]");
                        AnsiConsole.MarkupLine($"[grey]Result: {Markup.Escape(stateResult.NewState?.ToString() ?? "Unknown")} at {stateResult.ExecutionTimestamp:HH:mm:ss} UTC[/]\n");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[red]✗ Failed: {Markup.Escape(stateResult.Error ?? "Unknown error")}[/]\n");
                    }
                    break;

                case ActionType.RerouteWorkOrder:
                    var rerouteCommand = (RerouteWorkOrderCommand)request.CommandData;
                    var approvedRerouteCommand = rerouteCommand with { ApprovalStatus = ApprovalStatus.Approved };
                    var rerouteResult = await rerouteHandler.HandleAsync(approvedRerouteCommand, cancellationToken);

                    if (rerouteResult.IsSuccess)
                    {
                        AnsiConsole.MarkupLine($"[green]✓ Executed: {Markup.Escape(request.Description)}[/]");
                        AnsiConsole.MarkupLine($"[grey]Result: {rerouteResult.RemainingQuantity} units rerouted at {rerouteResult.ExecutionTimestamp:HH:mm:ss} UTC[/]\n");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[red]✗ Failed: {Markup.Escape(rerouteResult.Error ?? "Unknown error")}[/]\n");
                    }
                    break;

                default:
                    AnsiConsole.MarkupLine($"[red]Unknown action type: {request.ActionType}[/]\n");
                    break;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Exception during execution: {Markup.Escape(ex.Message)}[/]\n");
        }
    }

    private async Task RenderFinalShopFloorStateAsync(CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold aqua]FINAL SHOP FLOOR STATE[/]").RuleStyle("aqua").Centered());
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Machine states and work order assignments after AI agent actions:[/]\n");

        var rows = SnapshotMachines();
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Aqua)
            .AddColumn(new TableColumn("[bold]Machine[/]").NoWrap())
            .AddColumn(new TableColumn("[bold]State[/]").Centered().NoWrap())
            .AddColumn(new TableColumn("[bold]Work Order[/]").NoWrap())
            .AddColumn(new TableColumn("[bold]A[/]").RightAligned().NoWrap())
            .AddColumn(new TableColumn("[bold]P[/]").RightAligned().NoWrap())
            .AddColumn(new TableColumn("[bold]Q[/]").RightAligned().NoWrap())
            .AddColumn(new TableColumn("[bold]OEE[/]").RightAligned().NoWrap())
            .AddColumn(new TableColumn("[bold]Temp[/]").RightAligned().NoWrap())
            .AddColumn(new TableColumn("[bold]Coolant[/]").RightAligned().NoWrap())
            .AddColumn(new TableColumn("[bold]Scrap[/]").RightAligned().NoWrap());

        foreach (var row in rows)
        {
            var code = row.IsAnomalous ? $"[bold yellow]{row.Code}[/]" : $"[bold]{row.Code}[/]";
            table.AddRow(
                code,
                StateBadge(row.State),
                row.WorkOrder,
                FactorMarkup(row.AvailabilityPercent),
                FactorMarkup(row.PerformancePercent),
                FactorMarkup(row.QualityPercent, invertLowOnAnomaly: row.IsAnomalous),
                OeeMarkup(row.OeePercent),
                TemperatureMarkup(row.Temperature, row.IsAnomalous),
                CoolantMarkup(row.CoolantPressure, row.IsAnomalous),
                ScrapMarkup(row.ScrapRate, row.IsAnomalous));
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        var changes = new List<string>();
        foreach (var row in rows)
        {
            var equipment = await equipmentRepository.GetByCodeAsync(row.Code, cancellationToken);
            if (equipment is null)
            {
                continue;
            }

            var timeSinceChange = DateTimeOffset.UtcNow - equipment.StateChangedAt;
            if (timeSinceChange.TotalMinutes < 2)
            {
                changes.Add($"[aqua]•[/] {row.Code}: {Markup.Escape(equipment.State.ToString())} (changed {timeSinceChange.TotalSeconds:0}s ago)");
            }
        }

        if (changes.Count > 0)
        {
            AnsiConsole.MarkupLine("[grey]Recent state changes:[/]");
            foreach (var change in changes)
            {
                AnsiConsole.MarkupLine(change);
            }
            AnsiConsole.WriteLine();
        }
    }

    private async Task<AnomalyContext?> BuildAnomalyContextAsync(CancellationToken cancellationToken)
    {
        if (!_incidentCapture.TryDescribe(simulationController.AnomalousEquipmentId, out var captured) || captured is null)
        {
            return null;
        }

        var plantContext = await BuildPlantContextAsync(captured.EquipmentCode, cancellationToken);
        return new AnomalyContext(
            EquipmentCode: captured.EquipmentCode,
            PrimarySymptom: captured.PrimarySymptom,
            TelemetrySummary: captured.TelemetrySummary,
            DetectedAt: captured.DetectedAt,
            AdditionalContext: plantContext);
    }

    private async Task<string> BuildPlantContextAsync(string equipmentCode, CancellationToken cancellationToken)
    {
        var equipment = await equipmentRepository.GetByCodeAsync(equipmentCode, cancellationToken);
        if (equipment is null)
        {
            return $"Equipment {equipmentCode} is not in the live MES model.";
        }

        var assigned = await workOrderRepository.ListByAssignedEquipmentAsync(equipment.Id, cancellationToken);
        var workOrder = assigned.FirstOrDefault(w => w.Status is WorkOrderStatus.InProgress or WorkOrderStatus.Held);
        var workOrderLine = workOrder is null
            ? "none"
            : $"{workOrder.WorkOrderNumber} {workOrder.ProductCode} {workOrder.ProducedQuantity:0}/{workOrder.PlannedQuantity:0} ({workOrder.Status})";

        var siblings = equipment.ParentEquipmentId is { } parentId
            ? await equipmentRepository.ListByParentAsync(parentId, cancellationToken)
            : [];
        var alternatives = siblings
            .Where(s => s.Id != equipment.Id)
            .OrderBy(s => s.EquipmentCode, StringComparer.OrdinalIgnoreCase)
            .Select(s => $"{s.EquipmentCode} ({s.Name}): {s.State}" +
                         (s.CurrentWorkOrderId is null ? ", no work order" : ", has a work order"))
            .ToArray();

        var hierarchy =
            $"{equipment.Hierarchy.EnterpriseId ?? "n/a"} > {equipment.Hierarchy.SiteId ?? "n/a"} > " +
            $"{equipment.Hierarchy.AreaId ?? "n/a"} > {equipment.Hierarchy.WorkCenterId ?? "n/a"}";

        return $"""
            Equipment: {equipment.EquipmentCode} ({equipment.Name})
            Hierarchy: {hierarchy}
            MES state: {equipment.State} since {equipment.StateChangedAt:HH:mm:ss} UTC
            Active work order: {workOrderLine}
            Cell mates: {(alternatives.Length == 0 ? "none" : string.Join("; ", alternatives))}
            """;
    }

    private static void RenderAnomalyContext(AnomalyContext context)
    {
        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[bold]Equipment[/]", Markup.Escape(context.EquipmentCode));
        grid.AddRow("[bold]Symptom[/]", Markup.Escape(context.PrimarySymptom));
        grid.AddRow("[bold]Detected at[/]", $"{context.DetectedAt:yyyy-MM-dd HH:mm:ss} UTC");

        AnsiConsole.Write(new Panel(grid)
            .Header(" AnomalyContext (from telemetry window) ")
            .BorderColor(Color.Gold1)
            .Padding(1, 0));

        if (!string.IsNullOrWhiteSpace(context.TelemetrySummary))
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Panel(new Markup(Markup.Escape(context.TelemetrySummary)))
                .Header(" Captured tag window ")
                .BorderColor(Color.Grey)
                .Padding(1, 0)
                .Expand());
        }
        AnsiConsole.WriteLine();
    }

    private async Task<IncidentResponseReport> InvokeAgentWithLiveLogsAsync(
        MesAgentOrchestrator orchestrator,
        AnomalyContext anomalyContext,
        CancellationToken cancellationToken)
    {
        logCapture.BeginCapture();
        var logLines = new List<string>();

        try
        {
            var reportTask = orchestrator.ProcessAnomalyAsync(anomalyContext, cancellationToken);

            if (CanUseLiveDisplay())
            {
                await AnsiConsole.Live(BuildAiProgressView(logLines, isComplete: false))
                    .AutoClear(false)
                    .Overflow(VerticalOverflow.Ellipsis)
                    .StartAsync(async ctx =>
                    {
                        while (!reportTask.IsCompleted)
                        {
                            DrainCapturedLogs(logLines);
                            ctx.UpdateTarget(BuildAiProgressView(logLines, isComplete: false));
                            await Task.Delay(RefreshInterval, cancellationToken);
                        }

                        DrainCapturedLogs(logLines);
                        ctx.UpdateTarget(BuildAiProgressView(logLines, isComplete: true));
                    });
            }
            else
            {
                while (!reportTask.IsCompleted)
                {
                    while (logCapture.TryDequeue(out var line))
                    {
                        logLines.Add(line);
                        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(line)}[/]");
                    }

                    await Task.Delay(RefreshInterval, cancellationToken);
                }

                while (logCapture.TryDequeue(out var trailing))
                {
                    logLines.Add(trailing);
                    AnsiConsole.MarkupLine($"[grey]{Markup.Escape(trailing)}[/]");
                }
            }

            return await reportTask;
        }
        finally
        {
            logCapture.EndCapture();
        }
    }

    private void DrainCapturedLogs(List<string> logLines)
    {
        while (logCapture.TryDequeue(out var line))
        {
            logLines.Add(line);
        }
    }

    private static IRenderable BuildAiProgressView(IReadOnlyList<string> logLines, bool isComplete)
    {
        var header = isComplete
            ? "[bold green]AI agent finished[/]"
            : "[bold gold1]AI agent working — Semantic Kernel + MES tools[/]";

        var recent = logLines.Count == 0
            ? ["[grey]Waiting for agent logs…[/]"]
            : logLines.TakeLast(10).Select(line => $"[grey]{Markup.Escape(line)}[/]").ToArray();

        var body = new Markup(string.Join('\n', recent));
        var panel = new Panel(body)
            .Header($" {header} ")
            .BorderColor(isComplete ? Color.Green : Color.Gold1)
            .Padding(1, 0)
            .Expand();

        return new FixedHeightRenderable(panel, AiLogViewHeight);
    }

    private static async Task<string> PersistReportAsync(
        IncidentResponseReport report,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "reports");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"incident-{report.EquipmentCode}-{report.IncidentId:N}.md");
        await File.WriteAllTextAsync(path, FormatReportMarkdown(report), cancellationToken);
        return path;
    }

    private static string FormatReportMarkdown(IncidentResponseReport report)
    {
        var actions = report.ExecutedActions.Length == 0
            ? "- (none)"
            : string.Join('\n', report.ExecutedActions.Select(a => $"- {a}"));
        var followUp = report.RecommendedFollowUp.Length == 0
            ? "- (none)"
            : string.Join('\n', report.RecommendedFollowUp.Select(a => $"- {a}"));

        var builder = new StringBuilder();
        builder.AppendLine("# MES Incident Response Report");
        builder.AppendLine();
        builder.AppendLine($"- **Incident ID:** `{report.IncidentId}`");
        builder.AppendLine($"- **Equipment:** {report.EquipmentCode}");
        builder.AppendLine($"- **Symptom:** {report.PrimarySymptom}");
        builder.AppendLine($"- **Model:** {report.ModelUsed}");
        builder.AppendLine($"- **Success:** {report.Success}");
        builder.AppendLine($"- **Started:** {report.ProcessingStartedAt:O}");
        builder.AppendLine($"- **Completed:** {report.ProcessingCompletedAt:O}");
        builder.AppendLine($"- **Elapsed:** {report.ElapsedMilliseconds:0} ms");
        if (!string.IsNullOrWhiteSpace(report.ErrorMessage))
        {
            builder.AppendLine($"- **Error:** {report.ErrorMessage}");
        }

        builder.AppendLine();
        builder.AppendLine("## Telemetry summary");
        builder.AppendLine();
        builder.AppendLine("```");
        builder.AppendLine(report.TelemetrySummary.Trim());
        builder.AppendLine("```");
        builder.AppendLine();
        builder.AppendLine("## Diagnostic knowledge used");
        builder.AppendLine();
        builder.AppendLine("```");
        builder.AppendLine(report.DiagnosticKnowledgeUsed.Trim());
        builder.AppendLine("```");
        builder.AppendLine();
        builder.AppendLine("## AI reasoning");
        builder.AppendLine();
        builder.AppendLine(report.AiReasoning.Trim());
        builder.AppendLine();
        builder.AppendLine("## Executed actions");
        builder.AppendLine();
        builder.AppendLine(actions);
        builder.AppendLine();
        builder.AppendLine("## Recommended follow-up");
        builder.AppendLine();
        builder.AppendLine(followUp);
        builder.AppendLine();
        return builder.ToString();
    }

    private static void RenderIncidentReport(IncidentResponseReport report, string reportPath, string logFilePath)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold]Incident response report[/]").RuleStyle(report.Success ? "green" : "red").Centered());
        AnsiConsole.WriteLine();

        var status = report.Success ? "[green]Success[/]" : "[red]Failed[/]";
        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[bold]Incident ID[/]", Markup.Escape(report.IncidentId.ToString()));
        grid.AddRow("[bold]Equipment[/]", Markup.Escape(report.EquipmentCode));
        grid.AddRow("[bold]Symptom[/]", Markup.Escape(report.PrimarySymptom));
        grid.AddRow("[bold]Status[/]", status);
        grid.AddRow("[bold]Model[/]", Markup.Escape(report.ModelUsed));
        grid.AddRow("[bold]Elapsed[/]", $"{report.ElapsedMilliseconds:0} ms");
        if (!string.IsNullOrWhiteSpace(report.ErrorMessage))
        {
            grid.AddRow("[bold]Error[/]", $"[red]{Markup.Escape(report.ErrorMessage)}[/]");
        }

        AnsiConsole.Write(new Panel(grid)
            .Header(" Audit trail ")
            .BorderColor(report.Success ? Color.Green : Color.Red)
            .Padding(1, 0));

        if (!string.IsNullOrWhiteSpace(report.AiReasoning))
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Panel(new Markup(Markup.Escape(report.AiReasoning.Trim())))
                .Header(" AI reasoning ")
                .BorderColor(Color.Aqua)
                .Padding(1, 0)
                .Expand());
        }

        if (report.ExecutedActions.Length > 0)
        {
            var bullets = string.Join('\n', report.ExecutedActions.Select(action => $"[grey]•[/] {Markup.Escape(action)}"));
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Panel(new Markup(bullets))
                .Header(" Executed actions ")
                .BorderColor(Color.Grey)
                .Padding(1, 0));
        }

        if (report.RecommendedFollowUp.Length > 0)
        {
            var bullets = string.Join('\n', report.RecommendedFollowUp.Select(item => $"[grey]•[/] {Markup.Escape(item)}"));
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Panel(new Markup(bullets))
                .Header(" Recommended follow-up ")
                .BorderColor(Color.Grey)
                .Padding(1, 0));
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold gold1]Report file:[/] {Markup.Escape(reportPath)}");
        AnsiConsole.MarkupLine($"[bold gold1]Log file:[/] {Markup.Escape(logFilePath)}\n");
    }

    private async Task ConsumeTelemetryAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var readout in telemetryStreamer.Reader.ReadAllAsync(cancellationToken))
            {
                await ApplyReadoutAsync(readout, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Demo shutdown.
        }
    }

    private async Task ApplyReadoutAsync(TelemetryEvent readout, CancellationToken cancellationToken)
    {
        var snapshot = _telemetry.GetOrAdd(readout.EquipmentId, _ => new MachineTelemetry());
        snapshot.Apply(readout);

        if (readout.TagName == TelemetryTags.ProducedItems)
        {
            await ApplyProductionDeltaAsync(readout.EquipmentId, readout.Value, cancellationToken);
        }

        var justDetected = _incidentCapture.Observe(readout);
        var code = readout.EquipmentCode
                   ?? (readout.EquipmentId == DemoPlantCatalog.Cnc01Id ? "CNC-01"
                       : readout.EquipmentId == DemoPlantCatalog.Cnc02Id ? "CNC-02"
                       : readout.EquipmentId == DemoPlantCatalog.Cnc03Id ? "CNC-03"
                       : readout.EquipmentId.ToString("N")[..8]);

        var anomalous = _incidentCapture.AnomalousEquipmentId == readout.EquipmentId;
        var tone = anomalous ? "red" : "grey70";
        var quality = readout.Quality is TelemetryQuality.Good ? string.Empty : $" ({readout.Quality})";
        var unit = string.IsNullOrWhiteSpace(readout.EngineeringUnit) ? string.Empty : $" {Markup.Escape(readout.EngineeringUnit)}";
        EnqueueEvent($"[{tone}]{readout.Timestamp:HH:mm:ss}[/] {Markup.Escape(code)} {Markup.Escape(readout.TagName)}={readout.Value:0.00}{unit}{Markup.Escape(quality)}");

        if (justDetected)
        {
            EnqueueEvent(
                $"[bold red]DETECTED[/]  {Markup.Escape(code)} {Markup.Escape(readout.TagName)}={readout.Value:0.00}{unit} crossed alarm — capturing window");
        }
    }

    private async Task ApplyProductionDeltaAsync(
        Guid equipmentId,
        double producedItems,
        CancellationToken cancellationToken)
    {
        double delta;
        lock (_sync)
        {
            if (!_lastProducedTelemetry.TryGetValue(equipmentId, out var last))
            {
                _lastProducedTelemetry[equipmentId] = producedItems;
                return;
            }

            delta = producedItems - last;
            _lastProducedTelemetry[equipmentId] = producedItems;
        }

        if (delta <= 0)
        {
            return;
        }

        var assigned = await workOrderRepository
            .ListByAssignedEquipmentAsync(equipmentId, cancellationToken);
        var workOrder = assigned.FirstOrDefault(w => w.Status == WorkOrderStatus.InProgress);
        if (workOrder is null)
        {
            return;
        }

        lock (_sync)
        {
            var reported = workOrder.ReportProduction((decimal)delta);
            if (!reported.IsSuccess)
            {
                return;
            }
        }

        await workOrderRepository.UpdateAsync(workOrder, cancellationToken);
    }

    private IReadOnlyList<MachineRow> SnapshotMachines()
    {
        var machines = equipmentRepository.ListByParentAsync(DemoPlantCatalog.WorkCenterCncId)
            .GetAwaiter()
            .GetResult();

        var workOrders = workOrderRepository.ListAsync().GetAwaiter().GetResult();
        var asOf = DateTimeOffset.UtcNow;
        var rows = new List<MachineRow>(machines.Count);

        foreach (var machine in machines.OrderBy(m => m.EquipmentCode, StringComparer.OrdinalIgnoreCase))
        {
            _telemetry.TryGetValue(machine.Id, out var tel);
            var workOrder = machine.CurrentWorkOrderId is { } woId
                ? workOrders.FirstOrDefault(w => w.Id == woId)
                : null;

            OeeMetricDto? oee = null;
            if (workOrder is not null)
            {
                var scrapQuantity = ResolveScrapQuantity(workOrder, tel);
                var result = CalculateLiveOee(machine, workOrder, scrapQuantity, asOf);
                oee = result.Metric;
            }

            var isAnomalous = machine.Id == _incidentCapture.AnomalousEquipmentId;

            rows.Add(new MachineRow(
                machine.EquipmentCode,
                machine.Name,
                machine.State,
                FormatWorkOrder(workOrder),
                oee?.AvailabilityPercent,
                oee?.PerformancePercent,
                oee?.QualityPercent,
                oee?.OeePercent,
                tel?.Temperature,
                tel?.ProducedItems,
                tel?.ScrapRate,
                tel?.CoolantPressure,
                isAnomalous));
        }

        return rows;
    }

    private OeeCalculationResult CalculateLiveOee(
        Equipment machine,
        WorkOrder workOrder,
        decimal scrapQuantity,
        DateTimeOffset asOf)
    {
        var stateChangedAt = machine.State switch
        {
            EquipmentState.Running => _shiftStartedAt,
            EquipmentState.Faulted => _shiftStartedAt.AddMinutes(8),
            _ => machine.StateChangedAt
        };

        var request = new OeeCalculationRequest(
            machine.Id,
            machine.EquipmentCode,
            machine.State,
            stateChangedAt,
            workOrder.Id,
            workOrder.WorkOrderNumber,
            workOrder.Status,
            workOrder.AssignedEquipmentId,
            workOrder.PlannedQuantity,
            workOrder.ProducedQuantity,
            workOrder.DueAt,
            ReleasedAt: _shiftStartedAt.AddMinutes(-15),
            StartedAt: _shiftStartedAt,
            CompletedAt: workOrder.CompletedAt,
            Parameters: new OeeCalculationParameters(
                ScrapQuantity: scrapQuantity,
                IdealCycleTimeSeconds: 22m,
                AsOfUtc: asOf));

        return oeeCalculator.Calculate(request);
    }

    private static decimal ResolveScrapQuantity(WorkOrder workOrder, MachineTelemetry? telemetry)
    {
        if (telemetry is null || workOrder.ProducedQuantity <= 0m)
        {
            return 0m;
        }

        var scrap = workOrder.ProducedQuantity * (decimal)(telemetry.ScrapRate / 100d);
        return decimal.Round(Math.Clamp(scrap, 0m, workOrder.ProducedQuantity), 2, MidpointRounding.AwayFromZero);
    }

    private static string FormatWorkOrder(WorkOrder? workOrder)
    {
        if (workOrder is null)
        {
            return "[grey]—[/]";
        }

        return $"{workOrder.WorkOrderNumber} [grey]{workOrder.ProductCode}[/] {workOrder.ProducedQuantity:0}/{workOrder.PlannedQuantity:0}";
    }

    private void EnqueueEvent(string markup)
    {
        _eventLog.Enqueue(markup);
        while (_eventLog.Count > 8 && _eventLog.TryDequeue(out _))
        {
        }
    }

    private static async Task WaitUntilEnterAsync(string markupMessage, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine($"[bold aqua]▸[/] {markupMessage}");
        DrainBufferedKeys();

        if (!CanReadKeys())
        {
            await Task.Delay(400, cancellationToken);
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            if (TryConsumeEnter())
            {
                return;
            }

            await Task.Delay(50, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static bool CanReadKeys()
    {
        try
        {
            return !Console.IsInputRedirected;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool TryConsumeEnter()
    {
        try
        {
            if (!Console.KeyAvailable)
            {
                return false;
            }

            return Console.ReadKey(intercept: true).Key == ConsoleKey.Enter;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool TryReadKey(out ConsoleKey key)
    {
        key = default;
        try
        {
            if (!Console.KeyAvailable)
            {
                return false;
            }

            var keyInfo = Console.ReadKey(intercept: true);
            key = keyInfo.Key;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void DrainBufferedKeys()
    {
        try
        {
            while (Console.KeyAvailable)
            {
                Console.ReadKey(intercept: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static bool CanUseLiveDisplay()
    {
        try
        {
            return AnsiConsole.Profile.Out.IsTerminal && !Console.IsOutputRedirected;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static int ReadWindowWidth()
    {
        try
        {
            return Console.WindowWidth;
        }
        catch (IOException)
        {
            return AnsiConsole.Profile.Width;
        }
    }

    private static void SetTerminalAutowrap(bool enabled)
    {
        try
        {
            // DEC autowrap off (?7l) stops Windows from turning a full-width box line into an extra row.
            Console.Write(enabled ? "\u001b[?7h" : "\u001b[?7l");
        }
        catch (IOException)
        {
        }
    }

    private static void TryClear()
    {
        try
        {
            if (CanUseLiveDisplay())
            {
                AnsiConsole.Clear();
            }
        }
        catch (IOException)
        {
        }
    }

    private static async Task IgnoreCancel(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string StepDot(bool active, string label) =>
        active ? $"[bold green]● {label}[/]" : $"[grey]○ {label}[/]";

    private static string StateBadge(EquipmentState state) => state switch
    {
        EquipmentState.Running => "[black on green] RUNNING [/]",
        EquipmentState.Idle => "[black on grey]  IDLE   [/]",
        EquipmentState.Faulted => "[white on red] FAULTED [/]",
        EquipmentState.Maintenance => "[black on yellow]  MAINT  [/]",
        EquipmentState.Setup => "[black on aqua]  SETUP  [/]",
        _ => Markup.Escape(state.ToString())
    };

    private static string FactorMarkup(decimal? percent, bool invertLowOnAnomaly = false)
    {
        if (percent is null)
        {
            return "[grey]—[/]";
        }

        var color = percent.Value >= 90 ? "green"
            : percent.Value >= 75 ? "gold1"
            : invertLowOnAnomaly || percent.Value < 65 ? "red"
            : "yellow";

        return $"[{color}]{percent.Value:0.0}%[/]";
    }

    private static string OeeMarkup(decimal? percent)
    {
        if (percent is null)
        {
            return "[grey]—[/]";
        }

        var color = percent.Value >= 85 ? "green" : percent.Value >= 65 ? "gold1" : "red";
        return $"[bold {color}]{percent.Value:0.0}%[/]";
    }

    private static string TemperatureMarkup(double? celsius, bool anomalous)
    {
        if (celsius is null)
        {
            return "[grey]—[/]";
        }

        var color = anomalous || celsius >= TelemetryIncidentCapture.TemperatureAlarmCelsius ? "red"
            : celsius >= 95 ? "yellow"
            : "green";

        return $"[{color}]{celsius.Value:0.0} °C[/]";
    }

    private static string CoolantMarkup(double? psi, bool anomalous)
    {
        if (psi is null)
        {
            return "[grey]—[/]";
        }

        var color = anomalous || psi < TelemetryIncidentCapture.CoolantLowPsi ? "red"
            : psi < 15 ? "yellow"
            : "green";

        return $"[{color}]{psi.Value:0.0} PSI[/]";
    }

    private static string FormatPeak(double? value, string unit, bool alert)
    {
        if (value is null)
        {
            return "[grey]n/a[/]";
        }

        var color = alert ? "red" : "green";
        return $"[{color}]{value.Value:0.0} {unit}[/]";
    }

    private static string ScrapMarkup(double? percent, bool anomalous)
    {
        if (percent is null)
        {
            return "[grey]—[/]";
        }

        var color = anomalous || percent >= TelemetryIncidentCapture.ScrapAlarmPercent ? "red" : percent >= 5 ? "yellow" : "green";
        return $"[{color}]{percent.Value:0.0} %[/]";
    }

    private enum DemoPhase
    {
        FloorBoard,
        Streaming,
        Anomaly
    }

    /// <summary>
    /// Pins Live output to a constant line count so Spectre's cursor-up math cannot scroll the buffer.
    /// </summary>
    private sealed class FixedHeightRenderable(IRenderable inner, int height) : IRenderable
    {
        public Measurement Measure(RenderOptions options, int maxWidth)
        {
            var innerMeasure = inner.Measure(options, maxWidth);
            return new Measurement(innerMeasure.Min, innerMeasure.Max);
        }

        public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
        {
            var lines = Segment.SplitLines(inner.Render(options, maxWidth));
            if (lines.Count > height)
            {
                lines.RemoveRange(height, lines.Count - height);
            }

            while (lines.Count < height)
            {
                lines.Add(new SegmentLine());
            }

            for (var i = 0; i < lines.Count; i++)
            {
                foreach (var segment in lines[i])
                {
                    yield return segment;
                }

                if (i < lines.Count - 1)
                {
                    yield return Segment.LineBreak;
                }
            }
        }
    }

    private sealed class MachineTelemetry
    {
        public double Temperature { get; private set; }
        public double ProducedItems { get; private set; }
        public double ScrapRate { get; private set; }
        public double? CoolantPressure { get; private set; }
        public double? Vibration { get; private set; }
        public double? SpindleLoad { get; private set; }

        public void Apply(TelemetryEvent readout)
        {
            switch (readout.TagName)
            {
                case TelemetryTags.Temperature:
                    Temperature = readout.Value;
                    break;
                case TelemetryTags.ProducedItems:
                    ProducedItems = readout.Value;
                    break;
                case TelemetryTags.ScrapRate:
                    ScrapRate = readout.Value;
                    break;
                case TelemetryTags.CoolantPressure:
                    CoolantPressure = readout.Value;
                    break;
                case TelemetryTags.Vibration:
                    Vibration = readout.Value;
                    break;
                case TelemetryTags.SpindleLoad:
                    SpindleLoad = readout.Value;
                    break;
            }
        }
    }

    private sealed record MachineRow(
        string Code,
        string Name,
        EquipmentState State,
        string WorkOrder,
        decimal? AvailabilityPercent,
        decimal? PerformancePercent,
        decimal? QualityPercent,
        decimal? OeePercent,
        double? Temperature,
        double? ProducedItems,
        double? ScrapRate,
        double? CoolantPressure,
        bool IsAnomalous);
}
