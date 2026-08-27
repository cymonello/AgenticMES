namespace AgenticMES.Application.CQRS;

/// <summary>
/// Human-in-the-loop gate for high-risk plant-floor actions
/// (machine shutdown, fault, maintenance, work-order reroute).
/// </summary>
public enum ApprovalStatus
{
    PendingApproval,
    Approved,
    Executed
}
