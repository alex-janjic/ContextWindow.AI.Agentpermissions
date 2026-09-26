using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace ContextWindow.Tests;

public sealed class RefundTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly Caller Ana = new("ana", "green");
    private static readonly RefundRequest Exact = new("G-217", 4200, "USD");

    [Fact]
    public async Task AllowedControlReadsOrderAndAttemptsOnceAsync()
    {
        Fixture fixture = new();
        Order order = await RefundUsage.ReadAsync(fixture.Service);
        await fixture.Service.RefundAsync(Ana, Exact);

        Assert.Equal("G-217", order.Id);
        Assert.Equal([Exact], fixture.Writer.Attempts);
        Assert.Equal(4200, fixture.Writer.ConfirmedLocalMinorUnits);
    }

    [Fact]
    public async Task MissingApprovalPreventsWriteAsync()
    {
        Fixture fixture = new();
        fixture.Records.Approval = null;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.RefundAsync(Ana, Exact));
        Assert.Empty(fixture.Writer.Attempts);
    }

    [Fact]
    public async Task CrossTenantCannotReadOrWriteAsync()
    {
        Fixture fixture = new();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ReadOrderAsync(Ana, "B-104"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.RefundAsync(Ana, new RefundRequest("B-104", 4200, "USD")));
        Assert.Equal(0, fixture.Records.BlueRefundMinorUnits);
        Assert.Empty(fixture.Writer.Attempts);
    }

    [Theory]
    [InlineData(4500, "USD")]
    [InlineData(4200, "EUR")]
    public async Task ChangedAmountOrCurrencyCannotReuseApprovalAsync(long minorUnits, string currency)
    {
        Fixture fixture = new();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.RefundAsync(Ana, new RefundRequest("G-217", minorUnits, currency)));
        Assert.Empty(fixture.Writer.Attempts);
    }

    [Fact]
    public async Task ExpiryAtBoundaryAndRevocationDenyAsync()
    {
        Fixture fixture = new();
        fixture.Clock.Now = Now.AddMinutes(5);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.RefundAsync(Ana, Exact));
        fixture.Clock.Now = Now;
        fixture.Records.Approval = fixture.Records.Approval! with
        {
            Revoked = true
        };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.RefundAsync(Ana, Exact));
        Assert.Empty(fixture.Writer.Attempts);
    }

    [Fact]
    public async Task DirectServiceInvocationChecksPolicyAndActorAsync()
    {
        Fixture fixture = new();
        fixture.Records.Green = fixture.Records.Green with
        {
            Refundable = false
        };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.RefundAsync(Ana, Exact));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ReadOrderAsync(new Caller("eve", "green"), "G-217"));
        Assert.Empty(fixture.Writer.Attempts);
    }

    [Theory]
    [InlineData("read_order", "B-104", 4200, "USD", false)]
    [InlineData("refund_order", "G-217", 4500, "USD", false)]
    [InlineData("refund_order", "G-217", 4200, "USD", true)]
    public async Task ScriptedAgentCallsToolsUnderHostIdentityAsync(string toolName, string orderId, long minorUnits, string currency, bool allowed)
    {
        Fixture fixture = new();
        using ScriptedChatClient client = new(toolName, orderId, minorUnits, currency);
        AIAgent agent = RefundAgent.Create(client, fixture.Service, Ana);

        await agent.RunAsync("A fictional scripted request");

        Assert.Equal(allowed ? 1 : 0, fixture.Writer.Attempts.Count);
        Assert.Equal(allowed ? 4200 : 0, fixture.Writer.ConfirmedLocalMinorUnits);
        Assert.Equal(allowed ? 2 : 1, fixture.Records.ReadCount);
    }

    private sealed class ScriptedChatClient(string toolName, string orderId, long minorUnits, string currency) : IChatClient
    {
        private int responses;

        public ChatClientMetadata Metadata { get; } = new("scripted-fixture");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            responses++;

            if (responses == 1)
            {
                Dictionary<string, object?> arguments = new()
                {
                    ["orderId"] = orderId,
                    ["minorUnits"] = minorUnits,
                    ["currency"] = currency
                };
                ChatMessage call = new(ChatRole.Assistant, [new FunctionCallContent("call-1", toolName, arguments)]);
                return Task.FromResult(new ChatResponse([call]));
            }

            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "done")]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class Fixture
    {
        public Fixture()
        {
            Service = new RefundService(Records, Writer, Clock);
        }

        public MemoryRecords Records { get; } = new();
        public MemoryWriter Writer { get; } = new();
        public FixedClock Clock { get; } = new();
        public RefundService Service
        {
            get;
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = RefundTests.Now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MemoryRecords : IRefundRecords
    {
        public Order Green { get; set; } = new("G-217", "green", "ana", true);
        public Order Blue { get; } = new("B-104", "blue", "bob", true);
        public Approval? Approval { get; set; } = new("green", "ana", "refund", "G-217", 4200, "USD", Now.AddMinutes(5), false);
        public int ReadCount
        {
            get; private set;
        }
        public long BlueRefundMinorUnits
        {
            get; private set;
        }

        public Task<Order?> FindOrderAsync(string tenantId, string orderId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            Order? order = tenantId == "green" && orderId == Green.Id ? Green : tenantId == "blue" && orderId == Blue.Id ? Blue : null;
            return Task.FromResult(order);
        }

        public Task<Approval?> FindApprovalAsync(string tenantId, string actorId, string orderId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Approval? approval = Approval;
            return Task.FromResult(approval is not null && approval.TenantId == tenantId && approval.ActorId == actorId && approval.OrderId == orderId ? approval : null);
        }
    }

    private sealed class MemoryWriter : IRefundWriter
    {
        public List<RefundRequest> Attempts { get; } = [];
        public long ConfirmedLocalMinorUnits
        {
            get; private set;
        }

        public Task AttemptAsync(Caller caller, RefundRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts.Add(request);
            ConfirmedLocalMinorUnits += request.MinorUnits;
            return Task.CompletedTask;
        }
    }
}
