namespace AgenticMES.Application.CQRS.Commands;

/// <summary>
/// Stops the work order on source equipment and dispatches remaining quantity to target equipment.
/// Always requires <see cref="ApprovalStatus.Approved"/> before execution (HITL).
/// </summary>
public sealed record RerouteWorkOrderCommand(
    Guid WorkOrderId,
    Guid SourceEquipmentId,
    Guid TargetEquipmentId,
    string Reason,
    string TriggeredBy,
    ApprovalStatus ApprovalStatus = ApprovalStatus.PendingApproval,
    string? DecisionReasoning = null,
    decimal? ConfidenceScore = null) : ICommand<RerouteWorkOrderResult>;

public sealed record RerouteWorkOrderResult(
    bool IsSuccess,
    ApprovalStatus ApprovalStatus,
    string? Error,
    string? WorkOrderNumber,
    string? SourceEquipmentCode,
    string? TargetEquipmentCode,
    decimal RemainingQuantity,
    decimal ProducedQuantity,
    string Reason,
    string? TriggeredBy,
    DateTimeOffset ExecutionTimestamp)
{
    public static RerouteWorkOrderResult Success(
        string workOrderNumber,
        string sourceCode,
        string targetCode,
        decimal remaining,
        decimal produced,
        string reason,
        string triggeredBy) =>
        new(true, ApprovalStatus.Executed, null, workOrderNumber, sourceCode, targetCode, remaining, produced, reason, triggeredBy, DateTimeOffset.UtcNow);

    public static RerouteWorkOrderResult Pending(
        string workOrderNumber,
        string sourceCode,
        string targetCode,
        decimal remaining,
        decimal produced,
        string reason,
        string triggeredBy) =>
        new(true, ApprovalStatus.PendingApproval, null, workOrderNumber, sourceCode, targetCode, remaining, produced, reason, triggeredBy, DateTimeOffset.UtcNow);

    public static RerouteWorkOrderResult Failure(string error, string reason, string? triggeredBy = null) =>
        new(false, ApprovalStatus.PendingApproval, error, null, null, null, 0m, 0m, reason, triggeredBy, DateTimeOffset.UtcNow);
}
