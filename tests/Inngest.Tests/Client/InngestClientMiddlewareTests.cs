using System.Net;
using System.Text.Json;
using Inngest.Client;
using Inngest.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Inngest.Tests.Client;

public sealed class InngestClientMiddlewareTests
{
    private static readonly InngestEvent[] OneEvent = [new InngestEvent("app/user.created", new { Id = 1 })];

    private static ServiceProvider Build(FakeHttpHandler handler, FakeTimeProvider time, Action<IInngestBuilder>? configureInngest = null, Action<IServiceCollection>? configureServices = null) =>
        ClientServices.Build(handler, time, configureInngest, configureServices);

    private static FakeTimeProvider NewClock() => new(new DateTimeOffset(2024, 1, 18, 14, 1, 44, TimeSpan.Zero));

    [Fact]
    public async Task Sending_through_the_container_posts_to_the_event_api()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, ClientFixture.Accepted);
        await using var provider = Build(handler, NewClock());

        var result = await provider.GetRequiredService<IInngestClient>().SendAsync(OneEvent[0]);

        result.Ids.ShouldBe(["01HQ0000000000000000000001"]);
        handler.Single.Uri.AbsoluteUri.ShouldBe("http://localhost:8288/e/NO_EVENT_KEY_SET");
        provider.GetRequiredService<IInngestClient>().AppId.ShouldBe("test-app");
    }

    [Fact]
    public async Task The_first_registered_middleware_is_the_outermost()
    {
        var trace = new List<string>();
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, ClientFixture.Accepted);
        await using var provider = Build(handler, NewClock(), inngest =>
        {
            inngest.AddMiddleware(new TraceMiddleware("first", trace));
            inngest.AddMiddleware(new TraceMiddleware("second", trace));
            inngest.AddMiddleware(new TraceMiddleware("third", trace));
        });

        await provider.GetRequiredService<IInngestClient>().SendAsync(OneEvent);

        trace.ShouldBe(["first:before", "second:before", "third:before", "third:after", "second:after", "first:after"]);
    }

    [Fact]
    public async Task Middleware_wraps_the_whole_send_including_its_retries()
    {
        var trace = new List<string>();
        var time = NewClock();
        var handler = new FakeHttpHandler()
            .Respond(HttpStatusCode.InternalServerError, """{"status":500}""")
            .Respond(HttpStatusCode.InternalServerError, """{"status":500}""")
            .Respond(HttpStatusCode.OK, ClientFixture.Accepted);
        await using var provider = Build(handler, time, inngest => inngest.AddMiddleware(new TraceMiddleware("only", trace)));

        var result = await time.AdvanceUntilCompletedAsync(provider.GetRequiredService<IInngestClient>().SendAsync(OneEvent));

        result.Ids.ShouldBe(["01HQ0000000000000000000001"]);
        handler.Requests.Count.ShouldBe(3);
        trace.ShouldBe(["only:before", "only:after"]);
    }

    [Fact]
    public async Task Middleware_can_change_the_events_that_are_sent()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, ClientFixture.Accepted);
        await using var provider = Build(handler, NewClock(), inngest => inngest.AddMiddleware(new DelegateMiddleware((_, events, next) =>
            next([.. events, new InngestEvent("app/audit", new { Source = "middleware" })]))));

        await provider.GetRequiredService<IInngestClient>().SendAsync(OneEvent);

        handler.Single.BodyText.ShouldBe(
            """[{"name":"app/user.created","data":{"id":1},"ts":1705586504000},{"name":"app/audit","data":{"source":"middleware"},"ts":1705586504000}]""");
    }

    [Fact]
    public async Task Each_middleware_sees_the_events_left_by_the_one_outside_it()
    {
        var seen = new List<string>();
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, ClientFixture.Accepted);
        await using var provider = Build(handler, NewClock(), inngest =>
        {
            inngest.AddMiddleware(new DelegateMiddleware((_, events, next) =>
            {
                seen.Add("outer:" + string.Join(",", events.Select(e => e.Name)));
                return next([.. events.Select(e => e with { Name = e.Name + ".v2" })]);
            }));
            inngest.AddMiddleware(new DelegateMiddleware((_, events, next) =>
            {
                seen.Add("inner:" + string.Join(",", events.Select(e => e.Name)));
                return next(events);
            }));
        });

        await provider.GetRequiredService<IInngestClient>().SendAsync(OneEvent);

        seen.ShouldBe(["outer:app/user.created", "inner:app/user.created.v2"]);
        handler.Single.BodyText.ShouldContain("\"name\":\"app/user.created.v2\"");
    }

    [Fact]
    public async Task The_events_a_middleware_adds_are_validated()
    {
        var handler = new FakeHttpHandler();
        await using var provider = Build(handler, NewClock(), inngest => inngest.AddMiddleware(new DelegateMiddleware((_, events, next) =>
            next([new InngestEvent("inngest/reserved")]))));

        await Should.ThrowAsync<ArgumentException>(provider.GetRequiredService<IInngestClient>().SendAsync(OneEvent));

        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Middleware_can_answer_without_sending()
    {
        var handler = new FakeHttpHandler();
        await using var provider = Build(handler, NewClock(), inngest => inngest.AddMiddleware(new DelegateMiddleware((_, _, _) =>
            Task.FromResult(new SendEventsResult(["cached"])))));

        var result = await provider.GetRequiredService<IInngestClient>().SendAsync(OneEvent);

        result.Ids.ShouldBe(["cached"]);
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Middleware_that_drops_every_event_causes_no_request()
    {
        var handler = new FakeHttpHandler();
        await using var provider = Build(handler, NewClock(), inngest => inngest.AddMiddleware(new DelegateMiddleware((_, _, next) => next([]))));

        var result = await provider.GetRequiredService<IInngestClient>().SendAsync(OneEvent);

        result.Ids.ShouldBeEmpty();
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Middleware_sees_the_failure_of_a_send_and_can_replace_the_result()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.BadRequest, """{"status":400,"error":"nope"}""");
        InngestException? observed = null;
        await using var provider = Build(handler, NewClock(), inngest => inngest.AddMiddleware(new DelegateMiddleware(async (_, events, next) =>
        {
            try
            {
                return await next(events);
            }
            catch (InngestException exception)
            {
                observed = exception;
                return new SendEventsResult(["fallback"]);
            }
        })));

        var result = await provider.GetRequiredService<IInngestClient>().SendAsync(OneEvent);

        result.Ids.ShouldBe(["fallback"]);
        observed.ShouldNotBeNull().StatusCode.ShouldBe(400);
    }

    [Fact]
    public async Task Middleware_may_run_the_rest_of_the_chain_more_than_once_with_the_same_seed()
    {
        var handler = new FakeHttpHandler()
            .Respond(HttpStatusCode.BadRequest, """{"status":400,"error":"first"}""")
            .Respond(HttpStatusCode.OK, ClientFixture.Accepted);
        await using var provider = Build(handler, NewClock(), inngest => inngest.AddMiddleware(new DelegateMiddleware(async (_, events, next) =>
        {
            try
            {
                return await next(events);
            }
            catch (InngestException)
            {
                return await next(events);
            }
        })));

        await provider.GetRequiredService<IInngestClient>().SendAsync(OneEvent);

        handler.Requests.Select(request => request.Header("X-Inngest-Event-Id-Seed")).Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public async Task Middleware_gets_no_function_context_for_a_send_outside_a_run()
    {
        FunctionContext? seen = null;
        var called = false;
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, ClientFixture.Accepted);
        await using var provider = Build(handler, NewClock(), inngest => inngest.AddMiddleware(new DelegateMiddleware((context, events, next) =>
        {
            called = true;
            seen = context;
            return next(events);
        })));

        await provider.GetRequiredService<IInngestClient>().SendAsync(OneEvent);

        called.ShouldBeTrue();
        seen.ShouldBeNull();
    }

    [Fact]
    public async Task Middleware_gets_the_function_context_of_the_run_that_sends()
    {
        FunctionContext? seen = null;
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, ClientFixture.Accepted);
        await using var provider = Build(handler, NewClock(), inngest => inngest.AddMiddleware(new DelegateMiddleware((context, events, next) =>
        {
            seen = context;
            return next(events);
        })));
        var client = provider.GetRequiredService<InngestClient>();
        var context = CreateContext(provider);

        await client.SendAsync(OneEvent, context, provider, CancellationToken.None);

        seen.ShouldBeSameAs(context);
    }

    [Fact]
    public async Task Resolves_middleware_from_the_provider_it_is_given_and_creates_no_scope()
    {
        var probes = new List<ScopeProbe>();
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, ClientFixture.Accepted);
        await using var provider = Build(
            handler,
            NewClock(),
            inngest => inngest.AddMiddleware<ProbeMiddleware>(),
            services =>
            {
                services.AddSingleton(probes);
                services.AddScoped<ScopeProbe>();
            });
        var counting = new CountingScopeFactory(provider.GetRequiredService<IServiceScopeFactory>());
        var client = new InngestClient(
            new FakeHttpClientFactory(handler),
            provider.GetRequiredService<InngestSettings>(),
            NewClock(),
            counting,
            NullLogger<InngestClient>.Instance,
            provider.GetRequiredService<IServiceProviderIsService>());

        await using var runScope = provider.CreateAsyncScope();
        await client.SendAsync(OneEvent, context: null, runScope.ServiceProvider, CancellationToken.None);

        probes.ShouldHaveSingleItem().ShouldBeSameAs(runScope.ServiceProvider.GetRequiredService<ScopeProbe>());
        counting.Created.ShouldBe(0);
        probes[0].Disposed.ShouldBeFalse();
    }

    [Fact]
    public async Task A_send_outside_a_run_resolves_middleware_in_a_scope_of_its_own_that_ends_with_the_send()
    {
        var probes = new List<ScopeProbe>();
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, ClientFixture.Accepted).Respond(HttpStatusCode.OK, ClientFixture.Accepted);
        await using var provider = Build(
            handler,
            NewClock(),
            inngest => inngest.AddMiddleware<ProbeMiddleware>(),
            services =>
            {
                services.AddSingleton(probes);
                services.AddScoped<ScopeProbe>();
            });
        var client = provider.GetRequiredService<IInngestClient>();

        await client.SendAsync(OneEvent);
        await client.SendAsync(OneEvent);

        probes.Count.ShouldBe(2);
        probes[0].ShouldNotBeSameAs(probes[1]);
        probes.ShouldAllBe(probe => probe.Disposed);
    }

    [Fact]
    public async Task The_scope_of_a_send_is_disposed_even_when_the_send_fails()
    {
        var probes = new List<ScopeProbe>();
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.BadRequest, """{"status":400}""");
        await using var provider = Build(
            handler,
            NewClock(),
            inngest => inngest.AddMiddleware<ProbeMiddleware>(),
            services =>
            {
                services.AddSingleton(probes);
                services.AddScoped<ScopeProbe>();
            });

        await Should.ThrowAsync<InngestException>(provider.GetRequiredService<IInngestClient>().SendAsync(OneEvent));

        probes.ShouldHaveSingleItem().Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task Creates_no_scope_when_no_middleware_is_registered()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, ClientFixture.Accepted);
        await using var provider = Build(handler, NewClock());
        var counting = new CountingScopeFactory(provider.GetRequiredService<IServiceScopeFactory>());
        var client = new InngestClient(
            new FakeHttpClientFactory(handler),
            provider.GetRequiredService<InngestSettings>(),
            NewClock(),
            counting,
            NullLogger<InngestClient>.Instance,
            provider.GetRequiredService<IServiceProviderIsService>());

        await client.SendAsync(OneEvent[0]);

        counting.Created.ShouldBe(0);
    }

    [Fact]
    public async Task Creates_one_scope_per_send_when_the_container_cannot_say_whether_there_is_middleware()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, ClientFixture.Accepted).Respond(HttpStatusCode.OK, ClientFixture.Accepted);
        await using var provider = Build(handler, NewClock());
        var counting = new CountingScopeFactory(provider.GetRequiredService<IServiceScopeFactory>());
        var client = new InngestClient(
            new FakeHttpClientFactory(handler),
            provider.GetRequiredService<InngestSettings>(),
            NewClock(),
            counting,
            NullLogger<InngestClient>.Instance);

        await client.SendAsync(OneEvent[0]);
        await client.SendAsync(OneEvent[0]);

        counting.Created.ShouldBe(2);
    }

    [Fact]
    public async Task An_empty_send_does_not_reach_the_middleware()
    {
        var called = false;
        var handler = new FakeHttpHandler();
        await using var provider = Build(handler, NewClock(), inngest => inngest.AddMiddleware(new DelegateMiddleware((_, events, next) =>
        {
            called = true;
            return next(events);
        })));

        await provider.GetRequiredService<IInngestClient>().SendAsync(Array.Empty<InngestEvent>());

        called.ShouldBeFalse();
    }

    [Fact]
    public async Task A_send_that_cannot_work_without_an_event_key_does_not_reach_the_middleware()
    {
        var called = false;
        var handler = new FakeHttpHandler();
        await using var provider = Build(
            handler,
            NewClock(),
            inngest => inngest.AddMiddleware(new DelegateMiddleware((_, events, next) =>
            {
                called = true;
                return next(events);
            })),
            services => services.AddSingleton(TestSettings.Cloud()));

        await Should.ThrowAsync<InngestException>(provider.GetRequiredService<IInngestClient>().SendAsync(OneEvent));

        called.ShouldBeFalse();
    }

    private sealed class DelegateMiddleware(
        Func<FunctionContext?, IReadOnlyList<InngestEvent>, Func<IReadOnlyList<InngestEvent>, Task<SendEventsResult>>, Task<SendEventsResult>> wrap) : InngestMiddleware
    {
        public override Task<SendEventsResult> WrapSendEventAsync(
            FunctionContext? context,
            IReadOnlyList<InngestEvent> events,
            Func<IReadOnlyList<InngestEvent>, Task<SendEventsResult>> next,
            CancellationToken cancellationToken) => wrap(context, events, next);
    }

    private sealed class TraceMiddleware(string name, List<string> trace) : InngestMiddleware
    {
        public override async Task<SendEventsResult> WrapSendEventAsync(
            FunctionContext? context,
            IReadOnlyList<InngestEvent> events,
            Func<IReadOnlyList<InngestEvent>, Task<SendEventsResult>> next,
            CancellationToken cancellationToken)
        {
            trace.Add($"{name}:before");
            var result = await next(events);
            trace.Add($"{name}:after");
            return result;
        }
    }

    // Records the scoped service that the container injected, which tells which scope the middleware came from.
    private sealed class ProbeMiddleware(ScopeProbe probe, List<ScopeProbe> probes) : InngestMiddleware
    {
        public override Task<SendEventsResult> WrapSendEventAsync(
            FunctionContext? context,
            IReadOnlyList<InngestEvent> events,
            Func<IReadOnlyList<InngestEvent>, Task<SendEventsResult>> next,
            CancellationToken cancellationToken)
        {
            probes.Add(probe);
            return next(events);
        }
    }

    private sealed class ScopeProbe : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class CountingScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        public int Created { get; private set; }

        public IServiceScope CreateScope()
        {
            Created++;
            return inner.CreateScope();
        }
    }

    private static FunctionContext CreateContext(IServiceProvider services) => new FunctionContext<JsonElement>
    {
        FunctionId = "checkout",
        RunId = "01HQ0000000000000000000009",
        Attempt = 0,
        Step = new UnusedStepTools(),
        Logger = NullLogger.Instance,
        Services = services,
        Event = new InngestEvent<JsonElement> { Name = "cart/paid", Data = default },
        Events = [],
    };
}
