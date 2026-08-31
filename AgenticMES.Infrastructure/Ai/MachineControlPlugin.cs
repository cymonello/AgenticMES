using System.ComponentModel;
using AgenticMES.Application.Common.Interfaces;
using Microsoft.SemanticKernel;

namespace AgenticMES.Infrastructure.Ai;

/// <summary>
/// Semantic Kernel plugin that exposes ISA-95 machine control operations as AI tools.
/// Thin adapter layer between SK function calling and Application services.
/// </summary>
public sealed class MachineControlPlugin(IMachineControlService machineControlService)
{
    /// <summary>
    /// Requests an ISA-95 equipment state transition (e.g., Idle → Running, Running → Faulted, Idle → Maintenance).
    /// High-risk actions (stopping active machines, triggering faults, entering maintenance) require human approval
    /// before execution and will return a PendingApproval status.
    /// </summary>
    [KernelFunction]
    [Description(
        "Transitions an ISA-95 industrial equipment to a new operational state (Idle, Running, Faulted, Maintenance, Setup). " +
        "High-risk actions (stopping active production, entering maintenance, triggering faults) require human approval. " +
        "Use when equipment must start/stop production, enter maintenance mode, or handle fault conditions. " +
        "Always provide clear operational reasoning and an honest AI confidence score (0.0-1.0) for audit compliance.")]
    public Task<string> SetMachineStateAsync(
        [Description("ISA-95 equipment code (e.g., CNC-03, LATHE-01)")] string equipmentCode,
        [Description("Target state: Idle, Running, Faulted, Maintenance, or Setup")] string targetState,
        [Description("Clear operational reason for the state change (e.g., 'Scheduled preventive maintenance', 'Detected anomalous vibration')")] string reason,
        [Description("AI agent's internal reasoning explaining why this action was chosen")] string decisionReasoning,
        [Description("AI confidence level (0.0 to 1.0). Values < 0.7 should trigger review.")] decimal confidenceScore,
        CancellationToken cancellationToken = default)
    {
        return machineControlService.SetMachineStateAsync(
            equipmentCode,
            targetState,
            reason,
            decisionReasoning,
            confidenceScore,
            cancellationToken);
    }

    /// <summary>
    /// Retrieves the current operational state and latest telemetry snapshot for a specified equipment.
    /// Provides real-time visibility into machine health, production status, and work order assignment.
    /// </summary>
    [KernelFunction]
    [Description(
        "Retrieves the current operational state and status of an ISA-95 industrial equipment. " +
        "Returns the equipment's state (Idle, Running, Faulted, Maintenance, Setup), assigned work order, " +
        "time since last state change, and fault reason if applicable. " +
        "Use this to verify equipment availability, diagnose production issues, or check machine health before making control decisions.")]
    public Task<string> GetMachineTelemetryStatusAsync(
        [Description("ISA-95 equipment code (e.g., CNC-03, LATHE-01)")] string equipmentCode,
        CancellationToken cancellationToken = default)
    {
        return machineControlService.GetMachineTelemetryStatusAsync(equipmentCode, cancellationToken);
    }
}
