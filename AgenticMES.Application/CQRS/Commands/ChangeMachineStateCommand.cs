using AgenticMES.Domain.Enums;

namespace AgenticMES.Application.CQRS.Commands;

/// <summary>
/// Requests an ISA-95 equipment state transition and records why it happened.
/// Shutdown, fault, and maintenance require <see cref="ApprovalStatus.Approved"/> before execution.
/// </summary>
public sealed record ChangeMachineStateCommand(
    Guid EquipmentId,
    EquipmentState TargetState,
    string Reason,
    string TriggeredBy,
    ApprovalStatus ApprovalStatus = ApprovalStatus.PendingApproval,
    string? DecisionReasoning = null,
    decimal? ConfidenceScore = null) : ICommand<ChangeMachineStateResult>;

public sealed record ChangeMachineStateResult(
    bool IsSuccess,
    ApprovalStatus ApprovalStatus,
    string? Error,
    string? EquipmentCode,
    EquipmentState? PreviousState,
    EquipmentState? NewState,
    string Reason,
    string? TriggeredBy,
    DateTimeOffset ExecutionTimestamp)
{
    public static ChangeMachineStateResult Success(
        string equipmentCode,
        EquipmentState previousState,
        EquipmentState newState,
        string reason,
        string triggeredBy) =>
        new(true, ApprovalStatus.Executed, null, equipmentCode, previousState, newState, reason, triggeredBy, DateTimeOffset.UtcNow);

    public static ChangeMachineStateResult Pending(
        string equipmentCode,
        EquipmentState previousState,
        EquipmentState targetState,
        string reason,
        string triggeredBy) =>
        new(true, ApprovalStatus.PendingApproval, null, equipmentCode, previousState, targetState, reason, triggeredBy, DateTimeOffset.UtcNow);

    public static ChangeMachineStateResult Failure(string error, string reason, string? triggeredBy = null) =>
        new(false, ApprovalStatus.PendingApproval, error, null, null, null, reason, triggeredBy, DateTimeOffset.UtcNow);
}
