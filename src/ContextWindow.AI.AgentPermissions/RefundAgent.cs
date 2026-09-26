using System.Globalization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace ContextWindow;

/// <summary>One Microsoft Agent Framework agent with two guarded tools.</summary>
public static class RefundAgent
{
    /// <summary>Host-provided caller is captured outside model-proposed tool arguments.</summary>
    public static AIAgent Create(IChatClient chatClient, RefundService service, Caller caller)
    {
        AIFunction readOrder = AIFunctionFactory.Create(
            (string orderId, CancellationToken cancellationToken) =>
                service.ReadOrderAsync(caller, orderId, cancellationToken),
            "read_order", "Read an order available to the signed-in user.");

        AIFunction refundOrder = AIFunctionFactory.Create(
            (string orderId, long minorUnits, string currency, CancellationToken cancellationToken) =>
                service.RefundAsync(
                    caller, new RefundRequest(orderId, minorUnits, currency), cancellationToken),
            "refund_order", "Attempt the exact approved refund.");

        AIAgent agent = new ChatClientAgent(
            chatClient,
            instructions: "Handle only the signed-in caller's orders.",
            tools: [readOrder, refundOrder]);

        return agent.AsBuilder().Use(async (inner, context, next, cancellationToken) =>
        {
            string orderId = Required(context, "orderId");

            if (context.Function.Name == "read_order")
            {
                await service.ReadOrderAsync(caller, orderId, cancellationToken);
            }
            else if (context.Function.Name == "refund_order")
            {
                string amount = Required(context, "minorUnits");

                if (!long.TryParse(amount, NumberStyles.Integer, CultureInfo.InvariantCulture, out long minorUnits))
                {
                    throw new UnauthorizedAccessException("Invalid refund amount.");
                }

                RefundRequest request = new(orderId, minorUnits, Required(context, "currency"));
                await service.AuthorizeRefundAsync(caller, request, cancellationToken);
            }
            else
            {
                throw new UnauthorizedAccessException("Unregistered tool.");
            }

            return await next(context, cancellationToken);
        }).Build();
    }

    private static string Required(FunctionInvocationContext context, string name)
    {
        if (!context.Arguments.TryGetValue(name, out object? value) || value is null)
        {
            throw new UnauthorizedAccessException("Missing required tool argument.");
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture) ??
            throw new UnauthorizedAccessException("Invalid tool argument.");
    }
}
