using AgenticMES.Domain.Enums;

namespace AgenticMES.Application.Common.Interfaces;

/// <summary>
/// Human-in-the-loop approval service for high-risk AI agent actions.
/// Collects pending approval requests during AI execution and allows interactive approval.
/// </summary>
public interface IHitlApprovalService
{
    /// <summary>
    /// Registers a pending action that requires human approval.
    /// </summary>
    void RegisterPendingAction(PendingApprovalRequest request);

    /// <summary>
    /// Gets all pending approval requests.
    /// </summary>
    IReadOnlyList<PendingApprovalRequest> GetPendingRequests();

    /// <summary>
    /// Marks a pending action as approved by the human operator.
    /// </summary>
    void Approve(Guid requestId);

    /// <summary>
    /// Clears all pending and approved requests.
    /// </summary>
    void Clear();
}

/// <summary>
/// Represents a high-risk action awaiting human approval.
/// </summary>
public sealed record PendingApprovalRequest(
    Guid RequestId,
    ActionType ActionType,
    string Description,
    string Reason,
    string DecisionReasoning,
    decimal ConfidenceScore,
    DateTimeOffset RequestedAt,
    object CommandData);

public enum ActionType
{
    SetMachineState,
    RerouteWorkOrder
}
