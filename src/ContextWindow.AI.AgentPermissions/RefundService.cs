namespace ContextWindow;

/// <summary>Host-verified actor and tenant; never supplied by a model.</summary>
public sealed record Caller(string UserId, string TenantId);

/// <summary>Untrusted proposed refund arguments.</summary>
public sealed record RefundRequest(string OrderId, long MinorUnits, string Currency);

/// <summary>Tenant-owned order with current policy.</summary>
public sealed record Order(string Id, string TenantId, string ReaderId, bool Refundable);

/// <summary>Revocable authorization for one exact action.</summary>
public sealed record Approval(
    string TenantId, string ActorId, string Operation, string OrderId,
    long MinorUnits, string Currency, DateTimeOffset ExpiresAt, bool Revoked);

/// <summary>Application-owned current business records.</summary>
public interface IRefundRecords
{
    /// <summary>Find an order in the trusted caller's tenant.</summary>
    Task<Order?> FindOrderAsync(string tenantId, string orderId, CancellationToken cancellationToken);

    /// <summary>Find a current approval for the tenant, actor and order.</summary>
    Task<Approval?> FindApprovalAsync(
        string tenantId, string actorId, string orderId, CancellationToken cancellationToken);
}

/// <summary>Downstream attempt, not proof of payment settlement.</summary>
public interface IRefundWriter
{
    /// <summary>Submit a permitted attempt.</summary>
    Task AttemptAsync(Caller caller, RefundRequest request, CancellationToken cancellationToken);
}

/// <summary>Authoritative policy boundary for agent tools and direct callers.</summary>
public sealed class RefundService(IRefundRecords records, IRefundWriter writer, TimeProvider clock)
{
    /// <summary>Do not disclose an order outside the caller's tenant and reader scope.</summary>
    public async Task<Order> ReadOrderAsync(
        Caller caller, string orderId, CancellationToken cancellationToken = default)
    {
        Order? order = await records.FindOrderAsync(caller.TenantId, orderId, cancellationToken);

        if (order is null || order.TenantId != caller.TenantId || order.ReaderId != caller.UserId)
        {
            throw new UnauthorizedAccessException("Order not available to caller.");
        }

        return order;
    }

    /// <summary>Check policy and approval without a downstream attempt.</summary>
    public async Task AuthorizeRefundAsync(
        Caller caller, RefundRequest request, CancellationToken cancellationToken = default)
    {
        Order order = await ReadOrderAsync(caller, request.OrderId, cancellationToken);
        Approval? approval = await records.FindApprovalAsync(
            caller.TenantId, caller.UserId, request.OrderId, cancellationToken);

        if (!order.Refundable || request.MinorUnits <= 0 ||
            approval is null || approval.TenantId != caller.TenantId ||
            approval.ActorId != caller.UserId || approval.Operation != "refund" ||
            approval.OrderId != request.OrderId || approval.MinorUnits != request.MinorUnits ||
            approval.Currency != request.Currency || approval.Revoked ||
            approval.ExpiresAt <= clock.GetUtcNow())
        {
            throw new UnauthorizedAccessException("Refund not permitted.");
        }
    }

    /// <summary>Repeat the rule at the service boundary before a write attempt.</summary>
    public async Task RefundAsync(
        Caller caller, RefundRequest request, CancellationToken cancellationToken = default)
    {
        await AuthorizeRefundAsync(caller, request, cancellationToken);
        await writer.AttemptAsync(caller, request, cancellationToken);
    }
}
