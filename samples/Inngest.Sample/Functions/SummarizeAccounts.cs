using System.Text.Json;

namespace Inngest.Sample.Functions;

// Scores several accounts in parallel by invoking another function per account, then reports the total. Runs every
// morning, and whenever someone asks for a summary.
[InngestFunction("summarize-accounts")]
[EventTrigger<SummaryRequested>]
[CronTrigger("0 6 * * *", Timezone = "Europe/Prague")]
public sealed class SummarizeAccounts : InngestFunction<JsonElement, SummaryReady>
{
    private static readonly string[] AccountIds = ["acme", "globex", "initech"];

    public override async Task<SummaryReady> ExecuteAsync(FunctionContext<JsonElement> context, CancellationToken cancellationToken)
    {
        var scores = await Task.WhenAll(AccountIds.Select(accountId =>
            context.Step.InvokeAsync<ScoreAccount, AccountScore>($"score-{accountId}", new ScoreRequest(accountId),
                timeout: TimeSpan.FromMinutes(5))));

        var summary = new SummaryReady(scores.Sum(score => score.Score), scores.Length);
        await context.Step.SendEventAsync("announce-summary", summary);
        return summary;
    }
}
