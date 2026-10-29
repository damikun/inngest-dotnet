using System.Text.Json;

namespace Inngest.Sample.Functions;

// Has no trigger of its own; SummarizeAccounts invokes it.
public sealed class ScoreAccount : InngestFunction<JsonElement>
{
    public override async Task<object?> ExecuteAsync(FunctionContext<JsonElement> context, CancellationToken cancellationToken)
    {
        var accountId = context.Event.Data.GetProperty("accountId").GetString()!;
        var score = await context.Step.RunAsync("compute-score", () => accountId.Length * 10);
        return new AccountScore(accountId, score);
    }
}
