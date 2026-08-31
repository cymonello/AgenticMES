using System.Collections.Concurrent;
using AgenticMES.Application.Common.Interfaces;

namespace AgenticMES.Infrastructure.Services;

/// <summary>
/// In-memory HITL approval service for demo purposes.
/// Collects pending approval requests and allows the demo workflow to process them interactively.
/// </summary>
public sealed class DemoHitlApprovalService : IHitlApprovalService
{
    private readonly ConcurrentQueue<PendingApprovalRequest> _pending = new();
    private readonly ConcurrentDictionary<Guid, PendingApprovalRequest> _approved = new();

    public void RegisterPendingAction(PendingApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _pending.Enqueue(request);
    }

    public IReadOnlyList<PendingApprovalRequest> GetPendingRequests() =>
        _pending.ToArray();

    public void Approve(Guid requestId)
    {
        var request = _pending.FirstOrDefault(r => r.RequestId == requestId);
        if (request is not null)
        {
            _approved[requestId] = request;
        }
    }

    public void Clear()
    {
        while (_pending.TryDequeue(out _))
        {
        }

        _approved.Clear();
    }
}
