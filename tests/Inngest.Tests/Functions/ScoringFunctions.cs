using Inngest.Tests.Events;

namespace Inngest.Tests.Functions;

// Function classes with result types, for the tests of typed results and invoke by class.
internal sealed record ScoreRequest(string AccountId);

internal sealed record AccountScore(string AccountId, int Score);

// Registered by its attributes. It has no trigger: other functions invoke it.
[InngestFunction("score-account")]
internal sealed class ScoreAccount : InngestFunction<ScoreRequest, AccountScore>
{
    public override async Task<AccountScore> ExecuteAsync(FunctionContext<ScoreRequest> context, CancellationToken cancellationToken)
    {
        var accountId = context.Event.Data.AccountId;
        var score = await context.Step.RunAsync("compute-score", () => accountId.Length * 10);
        return new AccountScore(accountId, score);
    }
}

// Returns the same type as ScoreAccount, so that a mock for one compiles for the other. The tests register it in code.
internal sealed class RankAccount : InngestFunction<ScoreRequest, AccountScore>
{
    public override Task<AccountScore> ExecuteAsync(FunctionContext<ScoreRequest> context, CancellationToken cancellationToken) =>
        Task.FromResult(new AccountScore(context.Event.Data.AccountId, 1));
}

// Invokes ScoreAccount by its class for each account and adds the scores up.
[InngestFunction("summarize")]
[EventTrigger<OrderPaid>]
internal sealed class Summarize : InngestFunction<OrderPaid, int>
{
    public override async Task<int> ExecuteAsync(FunctionContext<OrderPaid> context, CancellationToken cancellationToken)
    {
        var scores = await Task.WhenAll(
            context.Step.InvokeAsync<ScoreAccount, AccountScore>("score-acme", new ScoreRequest("acme")),
            context.Step.InvokeAsync<ScoreAccount, AccountScore>(new StepOptions("score-globex") { Name = "Score Globex" }, new ScoreRequest("globex")));
        return scores.Sum(score => score.Score);
    }
}

// A typed function whose result can be null.
[InngestFunction("find-account")]
internal sealed class FindAccount : InngestFunction<ScoreRequest, string?>
{
    public override Task<string?> ExecuteAsync(FunctionContext<ScoreRequest> context, CancellationToken cancellationToken) =>
        Task.FromResult(context.Event.Data.AccountId == "missing" ? null : context.Event.Data.AccountId.ToUpperInvariant());
}

internal sealed class FailingScore : InngestFunction<ScoreRequest, AccountScore>
{
    public override Task<AccountScore> ExecuteAsync(FunctionContext<ScoreRequest> context, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The scoring service is down.");
}
