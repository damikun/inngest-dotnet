using System.Text.Json;

namespace Inngest.Sample.Functions;

// Scores several accounts in parallel by invoking another function per account, then reports the total.
public sealed class SummarizeAccounts : InngestFunction<JsonElement>
{
    private static readonly string[] AccountIds = ["acme", "globex", "initech"];

    public override async Task<object?> ExecuteAsync(FunctionContext<JsonElement> context, CancellationToken cancellationToken)
    {
        var scores = await Task.WhenAll(AccountIds.Select(accountId =>
            context.Step.InvokeAsync<AccountScore>($"score-{accountId}", new InvokeOptions("score-account")
            {
                Data = new { accountId },
                Timeout = TimeSpan.FromMinutes(5),
            })));

        var total = scores.Sum(score => score.Score);
        await context.Step.SendEventAsync("announce-summary", new InngestEvent("sample/summary.ready", new { total }));
        return new { total, accounts = scores.Length };
    }
}
