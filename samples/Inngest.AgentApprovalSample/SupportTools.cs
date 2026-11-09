using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.AI;

namespace Inngest.AgentApprovalSample;

public sealed class SupportTools(Shop shop, IInngestRealtime realtime)
{
    public IList<AITool> For(RealtimeTopic<AgentUpdate> status) =>
    [
        AIFunctionFactory.Create(
            async ([Description("The order number, such as 1003")] int orderId, CancellationToken cancellationToken) =>
            {
                await realtime.PublishAsync(status, new AgentUpdate("tool", $"Looking up order {orderId}", "find_order"), cancellationToken);
                return (object?)shop.Find(orderId) ?? $"There is no order {orderId}.";
            },
            "find_order",
            "Gets an order: the customer, their email, the item, the total in EUR, the status and the amount refunded so far."),

        new ApprovalRequiredAIFunction(AIFunctionFactory.Create(
            async (int orderId, [Description("The amount in EUR")] decimal amount, [Description("Why the customer gets the money back")] string reason, CancellationToken cancellationToken) =>
            {
                await realtime.PublishAsync(status, new AgentUpdate("tool", string.Create(CultureInfo.InvariantCulture, $"Refunding {amount:0.00} EUR on order {orderId}"), "issue_refund"), cancellationToken);
                return shop.Refund(orderId, amount);
            },
            "issue_refund",
            "Refunds an amount in EUR for an order. A person approves every refund before it is made.")),

        new ApprovalRequiredAIFunction(AIFunctionFactory.Create(
            async (int orderId, string subject, [Description("Plain text, signed as The Brew Supply team")] string body, CancellationToken cancellationToken) =>
            {
                await realtime.PublishAsync(status, new AgentUpdate("tool", $"Emailing the customer of order {orderId}", "send_email"), cancellationToken);
                return shop.Send(orderId, subject, body);
            },
            "send_email",
            "Emails the customer of an order. A person reads every email before it is sent.")),
    ];
}
