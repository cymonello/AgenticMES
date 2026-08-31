namespace AgenticMES.Application.Common.Interfaces;

/// <summary>
/// Application service for ISA-95 machine state control and telemetry inspection.
/// Provides framework-agnostic operations for monitoring equipment health and coordinating production state transitions.
/// </summary>
public interface IMachineControlService
{
    /// <summary>
    /// Requests an ISA-95 equipment state transition (e.g., Idle → Running, Running → Faulted, Idle → Maintenance).
    /// High-risk actions (stopping active machines, triggering faults, entering maintenance) require human approval
    /// before execution and will return a PendingApproval status.
    /// </summary>
    /// <param name="equipmentCode">Plant-floor ISA-95 equipment identifier (e.g., CNC-03, LATHE-01, ROBOT-ARM-05).</param>
    /// <param name="targetState">Desired equipment state: Idle, Running, Faulted, Maintenance, or Setup.</param>
    /// <param name="reason">Clear operational justification for the state change.</param>
    /// <param name="decisionReasoning">Internal reasoning chain explaining why this action was chosen.</param>
    /// <param name="confidenceScore">Confidence level for this decision (0.0 to 1.0).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Formatted result message indicating success/failure and approval status.</returns>
    Task<string> SetMachineStateAsync(
        string equipmentCode,
        string targetState,
        string reason,
        string decisionReasoning,
        decimal confidenceScore,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the current operational state and latest telemetry snapshot for a specified equipment.
    /// Provides real-time visibility into machine health, production status, and work order assignment.
    /// </summary>
    /// <param name="equipmentCode">Plant-floor ISA-95 equipment identifier (e.g., CNC-03, LATHE-01, ROBOT-ARM-05).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Formatted status report including state, work order, and fault information.</returns>
    Task<string> GetMachineTelemetryStatusAsync(
        string equipmentCode,
        CancellationToken cancellationToken = default);
}
