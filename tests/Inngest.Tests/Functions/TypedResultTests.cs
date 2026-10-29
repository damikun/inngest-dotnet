using Inngest.Functions;
using Inngest.Registration;
using Inngest.Tests.Client;
using Inngest.Tests.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Tests.Functions;

// A function class with a result type returns it as its output, and another function can invoke it by its class: the
// step finds the id in the registry, however the class was registered.
public sealed class TypedResultTests
{
    private const string Score = """{"name":"inngest/function.invoked","data":{"accountId":"acme"},"id":"01JINVOKE","ts":1793268000000}""";

    [Fact]
    public async Task A_typed_result_is_the_output_of_the_function()
    {
        using var host = new EngineHost();
        var function = Define(new ScoreAccount());

        var result = await host.CallAsync(function, Call.New().Event(Score).Data("compute-score", "40"));

        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe("""{"accountId":"acme","score":40}""");
    }

    [Theory]
    [InlineData("acme", "\"ACME\"")]
    [InlineData("missing", "null")]
    public async Task A_typed_result_may_be_null_when_its_type_allows_it(string accountId, string expected)
    {
        using var host = new EngineHost();
        var function = Define(new FindAccount());

        var result = await host.CallAsync(function, Call.New().Event($$"""{"name":"shop/find","data":{"accountId":"{{accountId}}"},"ts":1793268000000}"""));

        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe(expected);
    }

    [Fact]
    public async Task A_typed_function_fails_as_an_untyped_one_does()
    {
        using var host = new EngineHost();

        var result = await host.CallAsync(Define(new FailingScore()), Call.New().Event(Score));

        result.StatusCode.ShouldBe(500);
        result.Body.ShouldContain("The scoring service is down.");
    }

    [Fact]
    public async Task Invoking_by_class_invokes_the_id_the_class_is_registered_under()
    {
        using var host = HostWith(inngest => inngest.AddFunction<ScoreAccount>());
        var function = TestFunction.Define(async context =>
        {
            await context.Step.InvokeAsync<ScoreAccount, AccountScore>("score", new ScoreRequest("acme"), TimeSpan.FromMinutes(5));
            return null;
        });

        var result = await host.CallAsync(function, Call.New());

        var op = result.Op();
        op.GetProperty("op").GetString().ShouldBe("InvokeFunction");
        var opts = op.GetProperty("opts");
        opts.GetProperty("function_id").GetString().ShouldBe("test-app-score-account");
        opts.GetProperty("payload").GetRawText().ShouldBe("""{"data":{"accountId":"acme"}}""");
        opts.GetProperty("timeout").GetString().ShouldBe("5m");
    }

    [Fact]
    public async Task Invoking_by_class_works_for_a_class_registered_with_options_in_code()
    {
        using var host = HostWith(inngest => inngest.AddFunction<RankAccount>(new FunctionOptions { Id = "rank", Triggers = [] }));
        var function = TestFunction.Define(async context =>
        {
            await context.Step.InvokeAsync<RankAccount, AccountScore>(new StepOptions("rank") { Name = "Rank the account" });
            return null;
        });

        var result = await host.CallAsync(function, Call.New());

        var op = result.Op();
        op.GetProperty("displayName").GetString().ShouldBe("Rank the account");
        op.GetProperty("opts").GetProperty("function_id").GetString().ShouldBe("test-app-rank");
        op.GetProperty("opts").GetProperty("payload").GetRawText().ShouldBe("{}");
        op.GetProperty("opts").TryGetProperty("timeout", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Invoking_by_class_returns_the_invoked_function_result_in_its_type()
    {
        using var host = HostWith(inngest => inngest.AddFunction<ScoreAccount>());
        var function = TestFunction.Define(async context =>
        {
            var score = await context.Step.InvokeAsync<ScoreAccount, AccountScore>("score", new ScoreRequest("acme"));
            return $"{score.AccountId}:{score.Score}";
        });

        var result = await host.CallAsync(function, Call.New().Data("score", """{"accountId":"acme","score":40}"""));

        result.Body.ShouldBe("\"acme:40\"");
    }

    [Fact]
    public async Task Invoking_a_class_that_is_not_registered_fails_the_call_and_says_how_to_register_it()
    {
        using var host = HostWith(inngest => inngest.AddFunction<RankAccount>(new FunctionOptions { Id = "rank", Triggers = [] }));
        var function = TestFunction.Define(async context =>
        {
            await context.Step.InvokeAsync<ScoreAccount, AccountScore>("score", new ScoreRequest("acme"));
            return null;
        });

        var result = await host.CallAsync(function, Call.New());

        result.StatusCode.ShouldBe(500);
        result.Json.GetProperty("message").GetString().ShouldBe("No function is registered as ScoreAccount. Register it with AddFunction<ScoreAccount>().");
    }

    [Fact]
    public void A_class_registered_under_two_ids_cannot_be_found_by_its_class()
    {
        using var host = HostWith(inngest => inngest
            .AddFunction<RankAccount>(new FunctionOptions { Id = "rank", Triggers = [] })
            .AddFunction<RankAccount>(new FunctionOptions { Id = "rank-again", Triggers = [] }));
        var registry = host.Services.GetRequiredService<IFunctionRegistry>();

        Should.Throw<InvalidOperationException>(() => registry.FunctionIdOf(typeof(RankAccount))).Message.ShouldBe(
            "RankAccount is registered as \"rank\" and \"rank-again\", so its class does not say which function to use. Use the function's id instead.");
    }

    [Fact]
    public void The_registry_finds_classes_however_they_were_registered_but_not_delegates()
    {
        using var host = HostWith(inngest => inngest
            .AddFunction<ScoreAccount>()
            .AddFunction<RankAccount>(new FunctionOptions { Id = "rank", Triggers = [] })
            .AddFunction<FindAccount>(options => options with { Id = "find" })
            .AddFunction(new FunctionOptions { Id = "delegate", Triggers = [] }, (_, _) => Task.FromResult<object?>(null)));
        var registry = host.Services.GetRequiredService<IFunctionRegistry>();

        registry.FunctionIdOf(typeof(ScoreAccount)).ShouldBe("score-account");
        registry.FunctionIdOf(typeof(RankAccount)).ShouldBe("rank");
        registry.FunctionIdOf(typeof(FindAccount)).ShouldBe("find");
        registry.Functions.Single(function => function.Id == "delegate").FunctionType.ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => registry.FunctionIdOf(typeof(Summarize)));
    }

    [Fact]
    public async Task A_fake_step_tools_that_does_not_implement_invoke_by_class_says_so()
    {
        IStepTools step = new UnusedStepTools();

        (await Should.ThrowAsync<NotSupportedException>(() => step.InvokeAsync<ScoreAccount, AccountScore>("score")))
            .Message.ShouldBe("UnusedStepTools does not implement InvokeAsync by function class.");
        await Should.ThrowAsync<NotSupportedException>(() => step.InvokeAsync<ScoreAccount, AccountScore>(new StepOptions("score")));
    }

    private static FunctionDefinition Define(InngestFunction function) =>
        new(new FunctionOptions { Id = "fn", Triggers = [] }, _ => function);

    // An engine whose services hold the app's registrations, which invoke by class looks the id up in.
    private static EngineHost HostWith(Action<IInngestBuilder> configure) =>
        new(services => configure(services.AddInngest(options => options.AppId = EngineHost.Settings.AppId)));
}
