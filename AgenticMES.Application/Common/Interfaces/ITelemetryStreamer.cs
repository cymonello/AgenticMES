using System.Threading.Channels;
using AgenticMES.Domain.Events;

namespace AgenticMES.Application.Common.Interfaces;

/// <summary>
/// Mock OPC UA / MQTT source that publishes ISA-95 equipment performance tags.
/// Implementations push readouts into a <see cref="Channel{T}"/> for downstream consumers
/// (OEE, agents, operator UI).
/// </summary>
public interface ITelemetryStreamer
{
    /// <summary>Single-consumer channel of equipment performance / process-data readouts.</summary>
    ChannelReader<TelemetryEvent> Reader { get; }

    /// <summary>
    /// Produces machine readings until cancelled, then completes <see cref="Reader"/>.
    /// </summary>
    Task RunAsync(CancellationToken cancellationToken = default);
}
