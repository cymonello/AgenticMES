using AgenticMES.Application.Common.Interfaces;
using AgenticMES.Application.CQRS;
using AgenticMES.Application.CQRS.Commands;
using AgenticMES.Application.Services;
using AgenticMES.ConsoleApp;
using AgenticMES.Infrastructure.Ai;
using AgenticMES.Infrastructure.Persistence;
using AgenticMES.Infrastructure.Services;
using AgenticMES.Infrastructure.Simulation;
using AgenticMES.Infrastructure.StateMachine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;

var builder = Host.CreateApplicationBuilder(args);

var logCapture = new DemoLogCapture();
builder.Services.AddSingleton(logCapture);

var fileLogger = new DailyFileLoggerProvider();
builder.Services.AddSingleton(fileLogger);

builder.Logging.ClearProviders();
builder.Logging.AddProvider(logCapture);
builder.Logging.AddProvider(fileLogger);

// Factory floor must be Trace so the file sink can see everything. Category rules below
// are scoped to DemoLogCapture only — they must not apply to DailyFileLoggerProvider.
builder.Logging.SetMinimumLevel(LogLevel.Trace);
builder.Logging.AddFilter<DailyFileLoggerProvider>((_, level) => level != LogLevel.None);
builder.Logging.AddFilter<DemoLogCapture>(ShouldCaptureInDemoUi);

builder.Services.AddSingleton(_ => DemoPlantCatalog.Create());

builder.Services.AddSingleton<IEquipmentRepository>(sp =>
    new InMemoryEquipmentRepository(
        sp.GetRequiredService<ILogger<InMemoryEquipmentRepository>>(),
        sp.GetRequiredService<DemoPlantCatalog>()));

builder.Services.AddSingleton<IWorkOrderRepository>(sp =>
    new InMemoryWorkOrderRepository(
        sp.GetRequiredService<ILogger<InMemoryWorkOrderRepository>>(),
        sp.GetRequiredService<DemoPlantCatalog>()));

builder.Services.AddSingleton<IOeeCalculatorService, OeeCalculatorService>();

builder.Services.AddSingleton(new SimulatedTelemetryOptions
{
    Interval = TimeSpan.FromMilliseconds(400),
    ChannelCapacity = 512,
    EquipmentIds = DemoPlantCatalog.MachineIds
});

builder.Services.AddSingleton<SimulatedTelemetryStreamer>();
builder.Services.AddSingleton<ITelemetryStreamer>(sp => sp.GetRequiredService<SimulatedTelemetryStreamer>());
builder.Services.AddSingleton<ITelemetrySimulationController>(sp => sp.GetRequiredService<SimulatedTelemetryStreamer>());

builder.Services.AddSingleton<IHitlApprovalService, DemoHitlApprovalService>();

// State machine factory (Stateless framework)
builder.Services.AddSingleton<IEquipmentStateMachineFactory, EquipmentStateMachineFactoryImpl>();

builder.Services.AddSingleton<ICommandHandler<ChangeMachineStateCommand, ChangeMachineStateResult>, ChangeMachineStateCommandHandler>();
builder.Services.AddSingleton<ICommandHandler<RerouteWorkOrderCommand, RerouteWorkOrderResult>, RerouteWorkOrderCommandHandler>();

// Application services (framework-agnostic)
builder.Services.AddSingleton<IMachineControlService, MachineControlService>();
builder.Services.AddSingleton<ISchedulingService, SchedulingService>();

// Infrastructure AI components (SK-specific)
builder.Services.AddSingleton<MachineControlPlugin>();
builder.Services.AddSingleton<SchedulingPlugin>();
builder.Services.AddSingleton<ManualsKnowledgeBase>();

builder.Services.AddSingleton<MesAgentOrchestrator>(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var apiKey = ResolveOpenAiApiKey(configuration);
    var modelId = configuration["OpenAI:ModelId"];
    if (string.IsNullOrWhiteSpace(modelId))
    {
        modelId = "gpt-4o-mini";
    }

    if (string.IsNullOrWhiteSpace(apiKey))
    {
        throw new InvalidOperationException(
            "OpenAI API key is not configured. Set the OPENAI_API_KEY environment variable or OpenAI:ApiKey.");
    }

    return new MesAgentOrchestrator(
        apiKey,
        modelId,
        sp.GetRequiredService<MachineControlPlugin>(),
        sp.GetRequiredService<SchedulingPlugin>(),
        sp.GetRequiredService<ManualsKnowledgeBase>(),
        sp.GetRequiredService<ILogger<MesAgentOrchestrator>>());
});

builder.Services.AddSingleton<Func<MesAgentOrchestrator>>(sp =>
    () => sp.GetRequiredService<MesAgentOrchestrator>());

builder.Services.AddSingleton<IncidentSimulationWorkflow>();

using var host = builder.Build();
using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    cts.Cancel();
};

try
{
    await host.Services
        .GetRequiredService<IncidentSimulationWorkflow>()
        .RunAsync(cts.Token);
}
catch (OperationCanceledException)
{
    AnsiConsole.MarkupLine("\n[grey]Demo interrupted.[/]");
}

static string? ResolveOpenAiApiKey(IConfiguration configuration) =>
    FirstNonEmpty(
        configuration["OpenAI:ApiKey"],
        configuration["OPENAI_API_KEY"],
        Environment.GetEnvironmentVariable("OPENAI_API_KEY"));

static string? FirstNonEmpty(params string?[] values)
{
    foreach (var value in values)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }
    }

    return null;
}

static bool ShouldCaptureInDemoUi(string? category, LogLevel level)
{
    category ??= string.Empty;

    if (category.StartsWith("Microsoft.SemanticKernel", StringComparison.Ordinal))
    {
        return level >= LogLevel.Debug;
    }

    if (category.StartsWith("AgenticMES.Infrastructure.Ai", StringComparison.Ordinal))
    {
        return level >= LogLevel.Information;
    }

    if (category.StartsWith("AgenticMES.Application.Services", StringComparison.Ordinal))
    {
        return level >= LogLevel.Information;
    }

    return level >= LogLevel.Warning;
}
