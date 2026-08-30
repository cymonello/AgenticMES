using AgenticMES.Application.AiTools;
using AgenticMES.Application.Common.Interfaces;
using AgenticMES.Application.CQRS;
using AgenticMES.Application.CQRS.Commands;
using AgenticMES.Application.Services;
using AgenticMES.ConsoleApp;
using AgenticMES.Infrastructure.Ai;
using AgenticMES.Infrastructure.Persistence;
using AgenticMES.Infrastructure.Simulation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;

var builder = Host.CreateApplicationBuilder(args);

var logCapture = new DemoLogCapture();
builder.Services.AddSingleton(logCapture);

builder.Logging.ClearProviders();
builder.Logging.AddProvider(logCapture);
builder.Logging.SetMinimumLevel(LogLevel.Debug);
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("System", LogLevel.Warning);
builder.Logging.AddFilter("AgenticMES", LogLevel.Warning);
builder.Logging.AddFilter("AgenticMES.Infrastructure.Ai", LogLevel.Information);
builder.Logging.AddFilter("AgenticMES.Application.AiTools", LogLevel.Information);
builder.Logging.AddFilter("Microsoft.SemanticKernel", LogLevel.Debug);

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

builder.Services.AddSingleton<ICommandHandler<ChangeMachineStateCommand, ChangeMachineStateResult>, ChangeMachineStateCommandHandler>();
builder.Services.AddSingleton<ICommandHandler<RerouteWorkOrderCommand, RerouteWorkOrderResult>, RerouteWorkOrderCommandHandler>();
builder.Services.AddSingleton<MachineControlTools>();
builder.Services.AddSingleton<SchedulingTools>();
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
        sp.GetRequiredService<MachineControlTools>(),
        sp.GetRequiredService<SchedulingTools>(),
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
