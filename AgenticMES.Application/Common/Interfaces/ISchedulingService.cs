namespace AgenticMES.Application.Common.Interfaces;

/// <summary>
/// Application service for ISA-95 production scheduling and work order dispatch optimization.
/// Provides framework-agnostic operations for dynamically rerouting production and identifying alternative manufacturing resources.
/// </summary>
public interface ISchedulingService
{
    /// <summary>
    /// Reroutes an active work order from one equipment to another, enabling dynamic production optimization
    /// in response to equipment faults, maintenance needs, or capacity constraints.
    /// This is a high-risk operation that ALWAYS requires human approval before execution.
    /// </summary>
    /// <param name="workOrderNumber">Plant-floor operations request identifier (e.g., WO-2024-001234).</param>
    /// <param name="sourceEquipmentCode">Current equipment code where the work order is assigned (e.g., CNC-03).</param>
    /// <param name="targetEquipmentCode">Destination equipment code to receive the work order (e.g., CNC-05).</param>
    /// <param name="reason">Clear operational justification for the reroute.</param>
    /// <param name="decisionReasoning">Internal reasoning chain explaining why this reroute was chosen.</param>
    /// <param name="confidenceScore">Confidence level for this decision (0.0 to 1.0).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Formatted result message indicating success/failure and approval status.</returns>
    Task<string> RerouteWorkOrderAsync(
        string workOrderNumber,
        string sourceEquipmentCode,
        string targetEquipmentCode,
        string reason,
        string decisionReasoning,
        decimal confidenceScore,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Identifies equipment that can serve as alternative production resources for work order rerouting.
    /// Filters for equipment that is operationally available (Idle or Running without conflicts),
    /// at the same hierarchical level, and physically located in the same area or work center.
    /// </summary>
    /// <param name="equipmentCode">Reference equipment code to find alternatives for (e.g., CNC-03).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Formatted list of alternative equipment codes with their current operational state.</returns>
    Task<string> GetAvailableAlternativeMachinesAsync(
        string equipmentCode,
        CancellationToken cancellationToken = default);
}
