namespace ContextWindow.Tests;
/// <summary>Direct service use with a trusted host-provided caller.</summary>
public static class RefundUsage
{
    /// <summary>Read the fictional Green order.</summary>
    public static Task<Order> ReadAsync(RefundService service, CancellationToken cancellationToken = default)
    {
        Caller caller = new("ana", "green");
        return service.ReadOrderAsync(caller, "G-217", cancellationToken);
    }
}
