using System.Text.Json.Serialization;
using Inngest.IntegrationTests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.Webhooks;

// The parts of a Stripe event and of a GitHub push that the functions read. The test id travels where a real payload has
// room for free text: the metadata of a Stripe object, and the name of a GitHub repository.
internal sealed record StripeEvent(string Id, string Type, StripeEventData Data);

internal sealed record StripeEventData(StripeObject Object);

internal sealed record StripeObject(string Id, StripeMetadata Metadata);

internal sealed record StripeMetadata(string TestId);

internal sealed record GitHubPush(string Ref, GitHubRepository Repository);

internal sealed record GitHubRepository([property: JsonPropertyName("full_name")] string FullName);

// What webhook events trigger. A run records the id of the delivery it was started for, under the test id, so that a test
// can count the runs a webhook started.
internal static class WebhookFunctions
{
    public const string Delivery = "delivery";

    public static void Add(IInngestBuilder inngest)
    {
        inngest.AddFunction<StripeEvent>(
            new FunctionOptions { Id = "stripe-invoice-paid", Triggers = [Trigger.Event("stripe/invoice.paid")] },
            static (context, _) =>
            {
                Record(context.Services, context.Event.Data.Data.Object.Metadata.TestId, context.Event.Data.Id);
                return Task.FromResult<object?>(null);
            });

        inngest.AddFunction<GitHubPush>(
            new FunctionOptions { Id = "github-push", Triggers = [Trigger.Event("github/push")] },
            static (context, _) =>
            {
                Record(context.Services, context.Event.Data.Repository.FullName, context.Event.Id ?? "(no id)");
                return Task.FromResult<object?>(null);
            });
    }

    private static void Record(IServiceProvider services, string testId, string deliveryId) =>
        services.GetRequiredService<Probe>().For(testId).Record(Delivery, deliveryId);
}
