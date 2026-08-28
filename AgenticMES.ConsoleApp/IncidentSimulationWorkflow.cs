using System.Collections.Concurrent;
using AgenticMES.Application.Common.Interfaces;
using AgenticMES.Application.DTOs;
using AgenticMES.Domain.Entities;
using AgenticMES.Domain.Enums;
using AgenticMES.Domain.Events;
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
    ITelemetrySimulationController simulationController)
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);
    private const int LiveViewHeight = 13;

    private readonly DateTimeOffset _shiftStartedAt = DateTimeOffset.UtcNow.AddMinutes(-35);
    private readonly ConcurrentDictionary<Guid, MachineTelemetry> _telemetry = new();
    private readonly ConcurrentQueue<string> _eventLog = new();
    private readonly Dictionary<Guid, double> _lastProducedTelemetry = [];
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
        EnqueueEvent("[bold aqua]OPC UA mock online[/]  Temperature / ProducedItems / ScrapRate @ 400 ms");

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
        EnqueueEvent("[bold red]ANOMALY[/]  CNC-01 spindle temperature jumped to 128 °C — scrap climbing");

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
            "[grey]3.[/] Thermal runaway on CNC-01 with scrap-rate growth\n\n" +
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
                ScrapMarkup(row.ScrapRate, row.IsAnomalous));
        }

        var context = phase switch
        {
            DemoPhase.FloorBoard => "[grey]Talking point:[/] ISA-95 states + OEE = A × P × Q. CNC-01 running WO-1001 · CNC-02 idle · CNC-03 faulted.",
            DemoPhase.Streaming => "[grey]Talking point:[/] mock OPC UA tags at 400 ms feed produced qty into live OEE.",
            _ => "[grey]Talking point:[/] quality OEE collapsing — next beat is HITL maintenance + reroute WO-1001."
        };

        var events = _eventLog.ToArray();
        var latest = events.Length == 0
            ? "[grey]Awaiting telemetry…[/]"
            : events[^1];

        return new FixedHeightRenderable(
            new Rows(
                table,
                new Markup(context),
                new Markup(latest),
                new Markup($"[bold aqua]▸ {Markup.Escape(prompt)}[/]")),
            height: LiveViewHeight);
    }

    private async Task RenderEpilogueAsync(CancellationToken cancellationToken)
    {
        var rows = SnapshotMachines();
        var victim = rows.FirstOrDefault(r => r.Code == "CNC-01");

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold red]Incident closed — summary[/]").RuleStyle("red").Centered());
        AnsiConsole.WriteLine();

        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[bold]Asset[/]", "CNC-01  Haas ST-20Y CNC Lathe");
        grid.AddRow("[bold]Trigger[/]", "Sudden temperature jump + scrap-rate growth");
        grid.AddRow("[bold]Peak temperature[/]", victim?.Temperature is { } t ? $"[red]{t:0.0} °C[/]" : "[grey]n/a[/]");
        grid.AddRow("[bold]Scrap rate[/]", victim?.ScrapRate is { } s ? $"[red]{s:0.0} %[/]" : "[grey]n/a[/]");
        grid.AddRow("[bold]OEE after incident[/]", victim?.OeePercent is { } oee ? OeeMarkup(oee) : "[grey]n/a[/]");
        grid.AddRow("[bold]Quality factor[/]", victim?.QualityPercent is { } q ? FactorMarkup(q, invertLowOnAnomaly: true) : "[grey]n/a[/]");

        AnsiConsole.Write(new Panel(grid)
            .Header(" CNC-01 thermal runaway ")
            .BorderColor(Color.Red)
            .Padding(1, 0));

        AnsiConsole.MarkupLine("\n[grey]Talking point:[/] Agentic HITL would propose maintenance and reroute WO-1001 off CNC-01.\n");
        await WaitUntilEnterAsync("Press [bold]Enter[/] to finish the demo", cancellationToken);
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

        if (readout.TagName == TelemetryTags.Temperature)
        {
            snapshot.Temperature = readout.Value;
        }
        else if (readout.TagName == TelemetryTags.ScrapRate)
        {
            snapshot.ScrapRate = readout.Value;
        }
        else if (readout.TagName == TelemetryTags.ProducedItems)
        {
            snapshot.ProducedItems = readout.Value;
            await ApplyProductionDeltaAsync(readout.EquipmentId, readout.Value, cancellationToken);
        }

        var code = readout.EquipmentId == DemoPlantCatalog.Cnc01Id ? "CNC-01"
            : readout.EquipmentId == DemoPlantCatalog.Cnc02Id ? "CNC-02"
            : readout.EquipmentId == DemoPlantCatalog.Cnc03Id ? "CNC-03"
            : readout.EquipmentId.ToString("N")[..8];

        var anomaly = simulationController.HasActiveAnomaly
                      && readout.EquipmentId == simulationController.AnomalousEquipmentId;
        var tone = anomaly ? "red" : "grey70";
        EnqueueEvent(
            $"[{tone}]{readout.Timestamp:HH:mm:ss}[/] {code} {readout.TagName}={readout.Value}");
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

            var isAnomalous = simulationController.HasActiveAnomaly
                              && machine.Id == simulationController.AnomalousEquipmentId;

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

        var color = anomalous || celsius >= 110 ? "red"
            : celsius >= 95 ? "yellow"
            : "green";

        return $"[{color}]{celsius.Value:0.0} °C[/]";
    }

    private static string ScrapMarkup(double? percent, bool anomalous)
    {
        if (percent is null)
        {
            return "[grey]—[/]";
        }

        var color = anomalous || percent >= 10 ? "red" : percent >= 5 ? "yellow" : "green";
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
        public double Temperature { get; set; }
        public double ProducedItems { get; set; }
        public double ScrapRate { get; set; }
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
        bool IsAnomalous);
}
