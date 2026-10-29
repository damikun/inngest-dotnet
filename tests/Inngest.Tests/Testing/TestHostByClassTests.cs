using Inngest.Testing;
using Inngest.Tests.Events;
using Inngest.Tests.Functions;
using Microsoft.Extensions.DependencyInjection;
using static Inngest.Tests.Testing.TestHosts;

namespace Inngest.Tests.Testing;

// The host runs a function by its class, as the step invokes one, and a typed invoke mock checks both what it
// returns and which function the step invokes.
public sealed class TestHostByClassTests
{
    private static readonly InngestEvent Paid = new(OrderPaid.EventName, new OrderPaid("o_1", 10m));

    [Fact]
    public async Task Runs_a_function_by_its_class_and_reads_the_typed_result()
    {
        await using var host = new InngestTestHost(Services());

        var run = await host.RunAsync<ScoreAccount>(new InngestEvent("inngest/function.invoked", new ScoreRequest("acme")), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        run.FunctionId.ShouldBe("score-account");
        run.GetOutput<AccountScore>().ShouldBe(new AccountScore("acme", 40));
    }

    [Fact]
    public async Task Runs_a_function_by_its_class_with_mocks()
    {
        await using var host = new InngestTestHost(Services());

        var run = await host.RunAsync<ScoreAccount>(
            new InngestEvent("inngest/function.invoked", new ScoreRequest("acme")),
            new StepMocks().Run("compute-score", 7),
            Cancel);

        run.GetOutput<AccountScore>().ShouldBe(new AccountScore("acme", 7));
        run.GetStep("compute-score").Mocked.ShouldBeTrue();
    }

    [Fact]
    public async Task Runs_a_batch_by_the_class_of_the_function()
    {
        await using var host = new InngestTestHost(Services());
        InngestEvent[] events = [Paid, new(OrderPaid.EventName, new OrderPaid("o_2", 5m))];

        var plain = await host.RunAsync<CountPaid>(events, Cancel);
        var mocked = await host.RunAsync<CountPaid>(events, new StepMocks(), Cancel);

        plain.GetOutput<int>().ShouldBe(2);
        mocked.GetOutput<int>().ShouldBe(2);
    }

    [Fact]
    public async Task Executes_a_step_of_a_function_found_by_its_class()
    {
        await using var host = new InngestTestHost(Services());
        var request = new InngestEvent("inngest/function.invoked", new ScoreRequest("acme"));

        var plain = await host.ExecuteStepAsync<ScoreAccount>("compute-score", request, Cancel);
        var mocked = await host.ExecuteStepAsync<ScoreAccount>("compute-score", request, new StepMocks().Run("compute-score", 3), Cancel);

        plain.Status.ShouldBe(TestRunStatus.Stopped);
        plain.GetStep("compute-score").GetOutput<int>().ShouldBe(40);
        mocked.GetStep("compute-score").GetOutput<int>().ShouldBe(3);
    }

    [Fact]
    public async Task A_typed_invoke_mock_answers_an_invoke_by_class()
    {
        await using var host = new InngestTestHost(Services());
        var mocks = new StepMocks()
            .Invoke<ScoreAccount, AccountScore>("score-acme", new AccountScore("acme", 40))
            .Invoke<ScoreAccount, AccountScore>("score-globex", new AccountScore("globex", 60));

        var run = await host.RunAsync<Summarize>(Paid, mocks, Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        run.GetOutput<int>().ShouldBe(100);
        run.GetStep("score-acme").GetOutput<AccountScore>().ShouldBe(new AccountScore("acme", 40));
        run.Steps.ShouldAllBe(step => step.Mocked);
    }

    [Fact]
    public async Task A_typed_invoke_mock_for_another_function_fails_the_test()
    {
        await using var host = new InngestTestHost(Services());
        var mocks = new StepMocks()
            .Invoke<RankAccount, AccountScore>("score-acme", new AccountScore("acme", 40))
            .Invoke<ScoreAccount, AccountScore>("score-globex", new AccountScore("globex", 60));

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => host.RunAsync<Summarize>(Paid, mocks, Cancel));

        exception.Message.ShouldBe(
            "Step \"score-acme\" invokes \"test-app-score-account\", but its mock answers for RankAccount, which is \"test-app-rank\".");
    }

    [Fact]
    public async Task An_untyped_invoke_mock_still_answers_an_invoke_by_class()
    {
        await using var host = new InngestTestHost(Services());
        var mocks = new StepMocks()
            .Invoke("score-acme", new { accountId = "acme", score = 1 })
            .Invoke("score-globex", new { accountId = "globex", score = 2 });

        var run = await host.RunAsync<Summarize>(Paid, mocks, Cancel);

        run.GetOutput<int>().ShouldBe(3);
    }

    [Fact]
    public async Task A_class_that_is_not_registered_cannot_be_run()
    {
        await using var host = new InngestTestHost(Services());

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => host.RunAsync<FindAccount>(Paid, Cancel));

        exception.Message.ShouldBe("No function is registered as FindAccount. Register it with AddFunction<FindAccount>().");
    }

    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddInngest(options => options.AppId = AppId)
            .AddFunction<ScoreAccount>()
            .AddFunction<RankAccount>(new FunctionOptions { Id = "rank", Triggers = [] })
            .AddFunction<Summarize>()
            .AddFunction<CountPaid>();
        return services;
    }

    [InngestFunction("count-paid")]
    [EventTrigger<OrderPaid>]
    [BatchEvents(10, "5s")]
    internal sealed class CountPaid : InngestFunction<OrderPaid, int>
    {
        public override Task<int> ExecuteAsync(FunctionContext<OrderPaid> context, CancellationToken cancellationToken) =>
            Task.FromResult(context.Events.Count);
    }
}
