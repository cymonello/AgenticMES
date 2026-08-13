namespace AgenticMES.Domain.Common;

/// <summary>
/// Lightweight result for domain state transitions (no exceptions for business-rule failures).
/// </summary>
public readonly record struct DomainResult(bool IsSuccess, string? Error)
{
    public static DomainResult Success() => new(true, null);

    public static DomainResult Failure(string error) => new(false, error);
}
