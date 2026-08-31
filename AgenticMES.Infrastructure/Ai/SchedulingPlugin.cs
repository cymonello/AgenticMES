using System.ComponentModel;
using AgenticMES.Application.Common.Interfaces;
using Microsoft.SemanticKernel;

namespace AgenticMES.Infrastructure.Ai;

/// <summary>
/// Semantic Kernel plugin that exposes ISA-95 production scheduling operations as AI tools.
/// Thin adapter layer between SK function calling and Application services.
/// </summary>
public sealed class SchedulingPlugin(ISchedulingService schedulingService)
{
    /// <summary>
    /// Reroutes an active work order from one equipment to another, enabling dynamic production optimization
    /// in response to equipment faults, maintenance needs, or capacity constraints.
    /// This is a high-risk operation that ALWAYS requires human approval before execution.
    /// </summary>
    [KernelFunction]
    [Description(
        "Reroutes an ISA-95 work order from one equipment to another to handle faults, maintenance, or optimize production flow. " +
        "This is a HIGH-RISK operation that ALWAYS requires human approval. " +
        "Stops the work order on the source equipment and dispatches remaining quantity to the target equipment. " +
        "Use when source equipment fails, enters maintenance, or when load balancing requires shifting production. " +
        "Always provide clear operational reasoning and an honest AI confidence score (0.0-1.0) for audit compliance.")]
    public Task<string> RerouteWorkOrderAsync(
        [Description("Work order number (e.g., WO-2024-001234)")] string workOrderNumber,
        [Description("Current equipment code where work order is assigned (e.g., CNC-03)")] string sourceEquipmentCode,
        [Description("Destination equipment code to receive the work order (e.g., CNC-05)")] string targetEquipmentCode,
        [Description("Clear operational reason for the reroute (e.g., 'Source machine faulted', 'Optimize utilization')")] string reason,
        [Description("AI agent's internal reasoning explaining why this reroute was chosen")] string decisionReasoning,
        [Description("AI confidence level (0.0 to 1.0). Values < 0.7 should trigger review.")] decimal confidenceScore,
        CancellationToken cancellationToken = default)
    {
        return schedulingService.RerouteWorkOrderAsync(
            workOrderNumber,
            sourceEquipmentCode,
            targetEquipmentCode,
            reason,
            decisionReasoning,
            confidenceScore,
            cancellationToken);
    }

    /// <summary>
    /// Identifies equipment that can serve as alternative production resources for work order rerouting.
    /// Filters for equipment that is operationally available (Idle or Running without conflicts),
    /// at the same hierarchical level, and physically located in the same area or work center.
    /// </summary>
    [KernelFunction]
    [Description(
        "Identifies alternative ISA-95 equipment suitable for work order rerouting. " +
        "Returns a list of equipment at the same hierarchical level and location that are operationally available (Idle or Running without conflicts). " +
        "Excludes Faulted or Maintenance equipment. " +
        "Use this to plan reroutes, analyze production capacity, or find backup machines when equipment becomes unavailable.")]
    public Task<string> GetAvailableAlternativeMachinesAsync(
        [Description("Reference equipment code to find alternatives for (e.g., CNC-03)")] string equipmentCode,
        CancellationToken cancellationToken = default)
    {
        return schedulingService.GetAvailableAlternativeMachinesAsync(equipmentCode, cancellationToken);
    }
}
