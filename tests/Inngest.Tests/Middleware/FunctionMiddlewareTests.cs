using System.Net;
using System.Text.Json;
using Inngest.Client;
using Inngest.Execution;
using Inngest.Functions;
using Inngest.Middleware;
using Inngest.Tests.Client;
using Inngest.Tests.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Inngest.Tests.Middleware;

// Middleware of one function, as createFunction's middleware option in the TypeScript SDK: appended to the app's
// middleware, so the app's wraps it, and resolved for every call like the app's.
public sealed class FunctionMiddlewareTests
{
    private readonly List<string> _log = [];

    [Fact]
    public async Task App_middleware_wraps_the_functions_own()
    {
        using var host = new EngineHost(services => services.AddSingleton<InngestMiddleware>(new Recording("app", _log)));
        var function = Define(_ => Task.FromResult<object?>("done"), FunctionMiddleware.Of(_ => new Recording("fn", _log)));

        var result = await host.CallAsync(function, Call.New());

        result.Body.ShouldBe("\"app(fn(done))\"");
        _log.ShouldBe(["app:run-start", "fn:run-start", "app:before", "fn:before", "fn:after", "app:after"]);
    }

    [Fact]
    public async Task Steps_pass_through_the_app_middleware_first()
    {
        using var host = new EngineHost(services => services.AddSingleton<InngestMiddleware>(new Recording("app", _log)));
        var function = Define(async context => await context.Step.RunAsync("a", () => "A"), FunctionMiddleware.Of(_ => new Recording("fn", _log)));

        await host.CallAsync(function, Call.New().Data("a", "\"A\""));

        _log.Where(entry => entry.Contains("step", StringComparison.Ordinal)).ShouldBe(["app:step a", "fn:step a"]);
    }

    [Fact]
    public async Task Function_middleware_runs_only_for_its_function()
    {
        using var host = new EngineHost();
        var with = Define(_ => Task.FromResult<object?>("done"), FunctionMiddleware.Of(_ => new Recording("fn", _log)));
        var without = TestFunction.Define(_ => Task.FromResult<object?>("done"), id: "other");

        var plain = await host.CallAsync(without, Call.New());
        _log.ShouldBeEmpty();
        var wrapped = await host.CallAsync(with, Call.New());

        plain.Body.ShouldBe("\"done\"");
        wrapped.Body.ShouldBe("\"fn(done)\"");
    }

    [Fact]
    public async Task A_middleware_type_is_resolved_from_the_scope_of_each_call()
    {
        var seen = new List<InngestMiddleware>();
        using var host = new EngineHost(services =>
        {
            services.AddSingleton(seen);
            services.AddScoped<Scoped>();
        });
        var function = Define(async context => await context.Step.RunAsync("a", () => "A"), FunctionMiddleware.Of<Scoped>());

        await CallInScopeAsync(host, function, Call.New().Data("a", "\"A\""));
        await CallInScopeAsync(host, function, Call.New().Data("a", "\"A\""));

        // Each call wraps the function and the step with one instance of its own.
        seen.Count.ShouldBe(4);
        seen[0].ShouldBeSameAs(seen[1]);
        seen[2].ShouldBeSameAs(seen[3]);
        seen[0].ShouldNotBeSameAs(seen[2]);
    }

    [Fact]
    public async Task Events_a_step_sends_pass_through_the_functions_middleware_inside_the_apps()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, ClientFixture.Accepted);
        await using var provider = ClientServices.Build(handler, new FakeTimeProvider(), inngest => inngest.AddMiddleware(new Recording("app", _log)));
        using var host = new EngineHost();
        var function = Define(
            async context => (await context.Step.SendEventAsync("notify", new InngestEvent("app/notified"))).Ids,
            FunctionMiddleware.Of(_ => new Recording("fn", _log)));

        var result = await EngineHost.ResultOf(host.Engine.ExecuteAsync(
            new ExecutionRequest { Function = function, Body = Call.New().ToBytes(), StepId = "step", Services = provider },
            TestContext.Current.CancellationToken));

        result.OpCodes.ShouldBe(["StepRun"]);
        _log.Where(entry => entry.StartsWith("send", StringComparison.Ordinal)).ShouldBe(["send app", "send fn"]);
        handler.Single.BodyText.ShouldContain("app/notified");
    }

    [Fact]
    public async Task A_function_with_middleware_wraps_its_sends_when_the_app_has_none()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, ClientFixture.Accepted);
        await using var provider = ClientServices.Build(handler, new FakeTimeProvider());
        var client = provider.GetRequiredService<InngestClient>();
        var context = new FunctionContext<JsonElement>
        {
            FunctionId = "fn",
            RunId = "01JRUN",
            Attempt = 0,
            Step = new UnusedStepTools(),
            Logger = NullLogger.Instance,
            Services = provider,
            Event = new InngestEvent<JsonElement> { Name = "test/event", Data = default },
            Events = [],
            Middleware = [new Recording("fn", _log)],
        };

        await client.SendAsync([new InngestEvent("app/notified")], context, provider, CancellationToken.None);

        _log.ShouldBe(["send fn"]);
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public void Adding_a_function_registers_its_middleware_type_and_its_failure_handler_shares_the_list()
    {
        var services = new ServiceCollection();
        var options = new FunctionOptions { Id = "fn", Triggers = [Trigger.Event("test/event")], Middleware = [FunctionMiddleware.Of<Scoped>()] };

        services.AddInngest(inngest => inngest.AppId = "test-app")
            .AddFunction(options, (_, _) => Task.FromResult<object?>(null), onFailure: (_, _) => Task.CompletedTask);

        services.ShouldContain(service => service.ServiceType == typeof(Scoped) && service.Lifetime == ServiceLifetime.Scoped);
        var failure = services.Select(service => service.ImplementationInstance).OfType<FunctionDefinition>().Single(definition => definition.FailureHandlerFor == "fn");
        failure.Options.Middleware.ShouldBeSameAs(options.Middleware);
    }

    [Fact]
    public void A_type_listed_twice_or_a_null_entry_is_rejected_at_startup()
    {
        var options = new FunctionOptions { Id = "fn", Triggers = [Trigger.Event("test/event")] };

        Should.Throw<InvalidOperationException>(() => FunctionOptionsValidator.Validate(options with { Middleware = [FunctionMiddleware.Of<Scoped>(), FunctionMiddleware.Of<Scoped>()] }))
            .Message.ShouldBe("Function \"fn\": Middleware lists Scoped more than once.");
        Should.Throw<InvalidOperationException>(() => FunctionOptionsValidator.Validate(options with { Middleware = [null!] }))
            .Message.ShouldBe("Function \"fn\": Middleware contains a null entry.");
        Should.NotThrow(() => FunctionOptionsValidator.Validate(options with { Middleware = [FunctionMiddleware.Of<Scoped>(), FunctionMiddleware.Of(_ => new Recording("x", _log))] }));
    }

    // With neither app nor function middleware, a call gets no pipeline and no payload transforms.
    [Fact]
    public void A_call_without_middleware_resolves_none()
    {
        using var host = new EngineHost();

        var middleware = MiddlewarePipeline.Resolve(host.Services, forFunction: null);

        middleware.ShouldBeEmpty();
        PayloadTransforms.Find(middleware).ShouldBeNull();
        MiddlewarePipeline.Resolve(host.Services, forFunction: []).ShouldBeEmpty();
    }

    private static FunctionDefinition Define(Func<FunctionContext<JsonElement>, Task<object?>> handler, params FunctionMiddleware[] middleware) =>
        new(
            new FunctionOptions { Id = "fn", Triggers = [Trigger.Event("test/event")], Middleware = middleware },
            _ => new DelegateFunction<JsonElement>((context, _) => handler(context)));

    private static async Task CallInScopeAsync(EngineHost host, FunctionDefinition function, Call call)
    {
        await using var scope = host.Services.CreateAsyncScope();
        using var result = await host.Engine.ExecuteAsync(
            new ExecutionRequest { Function = function, Body = call.ToBytes(), StepId = "step", Services = scope.ServiceProvider },
            TestContext.Current.CancellationToken);
        result.StatusCode.ShouldBe(200);
    }

    private sealed class Recording(string name, List<string> log) : InngestMiddleware
    {
        public override ValueTask OnRunStartAsync(FunctionContext context)
        {
            log.Add($"{name}:run-start");
            return ValueTask.CompletedTask;
        }

        public override async Task<object?> WrapFunctionAsync(FunctionContext context, Func<Task<object?>> next)
        {
            log.Add($"{name}:before");
            var output = await next();
            log.Add($"{name}:after");
            return $"{name}({output})";
        }

        public override Task<object?> WrapStepAsync(FunctionContext context, StepInfo step, Func<Task<object?>> next)
        {
            log.Add($"{name}:step {step.Id}");
            return next();
        }

        public override Task<SendEventsResult> WrapSendEventAsync(
            FunctionContext? context,
            IReadOnlyList<InngestEvent> events,
            Func<IReadOnlyList<InngestEvent>, Task<SendEventsResult>> next,
            CancellationToken cancellationToken)
        {
            log.Add($"send {name}");
            return next(events);
        }
    }

    private sealed class Scoped(List<InngestMiddleware> seen) : InngestMiddleware
    {
        public override Task<object?> WrapFunctionAsync(FunctionContext context, Func<Task<object?>> next)
        {
            seen.Add(this);
            return next();
        }

        public override Task<object?> WrapStepAsync(FunctionContext context, StepInfo step, Func<Task<object?>> next)
        {
            seen.Add(this);
            return next();
        }
    }
}
