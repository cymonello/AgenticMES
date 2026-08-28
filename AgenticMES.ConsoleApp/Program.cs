using AgenticMES.Application.Common.Interfaces;
using AgenticMES.Application.Services;
using AgenticMES.ConsoleApp;
using AgenticMES.Infrastructure.Persistence;
using AgenticMES.Infrastructure.Simulation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.Warning);

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
