namespace AgenticMES.Application.CQRS;

/// <summary>Shared input guardrails for CQRS commands (AI and operator payloads).</summary>
internal static class CommandGuards
{
    public static string? ValidateAudit(string reason, string triggeredBy, decimal? confidenceScore)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return "A reason is required for the audit trail.";
        }

        if (string.IsNullOrWhiteSpace(triggeredBy))
        {
            return "TriggeredBy is required for the audit trail.";
        }

        if (confidenceScore is < 0m or > 1m)
        {
            return "ConfidenceScore must be between 0 and 1.";
        }

        return null;
    }

    public static bool IsApproved(ApprovalStatus status) => status is ApprovalStatus.Approved;
}
