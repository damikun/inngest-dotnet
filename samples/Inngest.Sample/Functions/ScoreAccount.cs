namespace Inngest.Sample.Functions;

// Has no trigger of its own; SummarizeAccounts invokes it by its class and gets an AccountScore back.
[InngestFunction("score-account")]
public sealed class ScoreAccount : InngestFunction<ScoreRequest, AccountScore>
{
    public override async Task<AccountScore> ExecuteAsync(FunctionContext<ScoreRequest> context, CancellationToken cancellationToken)
    {
        var accountId = context.Event.Data.AccountId;
        var score = await context.Step.RunAsync("compute-score", () => accountId.Length * 10);
        return new AccountScore(accountId, score);
    }
}
