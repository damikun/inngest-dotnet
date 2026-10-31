using System.Security.Claims;
using System.Text.Json;
using Inngest.Functions;
using Inngest.Tests.Execution;
using Inngest.UserContext;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Inngest.Tests.UserContext;

// Real calls through the execution engine: the user of the triggering event becomes ICurrentUser and a scoped ClaimsPrincipal.
public sealed class RestoreTests
{
    private static readonly string[] Allowed = ["sub", "name", "email", "role"];

    private static UserContextApp NewApp(Action<UserContextOptions>? configure = null, Action<IInngestBuilder>? inngest = null) =>
        new(configure: options =>
            {
                options.Claims = Allowed;
                configure?.Invoke(options);
            },
            configureInngest: inngest);

    private static Call Event(string user, string name = UserContextApp.EventName) => Call.New().Event(UserContextApp.EventJson(user, name));

    // A singleton that finds out who is running, the way a logging or audit service would.
    private sealed class Audit(ICurrentUser user)
    {
        public string? Who() => user.Principal.Identity?.Name;
    }

    private static readonly string[] AdaClaims = ["sub=u-42", "name=Ada Lovelace", "email=ada@example.com", "role=admin", "role=support"];

    [Fact]
    public async Task A_valid_user_becomes_the_current_user_and_the_principal_of_the_run()
    {
        await using var app = NewApp();

        var result = await app.CallAsync(UserContextApp.Observe(), Event(app.Seal(UserContextApp.Ada())), runStart: app.Time.GetUtcNow());

        result.StatusCode.ShouldBe(200);
        var seen = UserContextApp.SeenIn(result);
        seen.IsAuthenticated.ShouldBeTrue();
        seen.IdentityAuthenticated.ShouldBeTrue();
        seen.Claims.ShouldBe(AdaClaims);
        seen.Name.ShouldBe("Ada Lovelace");
        seen.IsAdmin.ShouldBeTrue();
        seen.IssuedAt.ShouldBe(app.Time.GetUtcNow().ToUnixTimeSeconds());
        seen.AuthenticationType.ShouldBe("Inngest.UserContext");
        seen.Principal.ShouldBe(5);
    }

    [Fact]
    public async Task The_default_name_and_role_types_of_the_original_identity_are_the_ones_the_restored_one_has()
    {
        await using var app = NewApp(options => options.Claims = [ClaimTypes.Name, ClaimTypes.Role, "sub"]);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "Ada"), new Claim(ClaimTypes.Role, "admin"), new Claim("sub", "u-1")], "Cookies"));

        var result = await app.CallAsync(UserContextApp.Observe(), Event(app.Seal(principal)), app.Time.GetUtcNow());

        var seen = UserContextApp.SeenIn(result);
        seen.Name.ShouldBe("Ada");
        seen.IsAdmin.ShouldBeTrue();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"external_id":"u-42"}""")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    public async Task An_event_without_a_user_runs_with_nobody(string user)
    {
        await using var app = NewApp();

        var result = await app.CallAsync(UserContextApp.Observe(), Event(user));

        result.StatusCode.ShouldBe(200);
        var seen = UserContextApp.SeenIn(result);
        seen.IsAuthenticated.ShouldBeFalse();
        seen.IdentityAuthenticated.ShouldBeFalse();
        seen.Claims.ShouldBeEmpty();
        seen.Name.ShouldBeNull();
        seen.IssuedAt.ShouldBeNull();
        seen.Principal.ShouldBe(0);
        app.Logs.GetSnapshot().Where(record => record.Level >= LogLevel.Warning).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_event_without_any_user_property_runs_with_nobody()
    {
        await using var app = NewApp();

        var result = await app.CallAsync(UserContextApp.Observe(), Call.New().Event("""{"data":{},"id":"01JTEST","name":"test/event","ts":1793268000000}"""));

        UserContextApp.SeenIn(result).IsAuthenticated.ShouldBeFalse();
    }

    [Theory]
    [InlineData("tampered", "it could not be unprotected")]
    [InlineData("expired", "it has expired")]
    [InlineData("other event", "it was made for another event")]
    [InlineData("unknown version", "its format is not known to this version")]
    [InlineData("malformed", "it is not valid")]
    public async Task A_user_that_cannot_be_used_runs_as_nobody_and_one_warning_says_why_without_the_payload(string problem, string reason)
    {
        await using var app = NewApp();
        var now = app.Time.GetUtcNow();
        var sealedUser = app.Seal(UserContextApp.Ada());
        var text = JsonDocument.Parse(sealedUser).RootElement.GetProperty(UserContextCodec.Key).GetProperty("t").GetString()!;
        var (user, runStart) = problem switch
        {
            "tampered" => (UserContextApp.Envelope(text[..^3] + (text[^3] == 'A' ? 'B' : 'A') + text[^2..]), now),
            "expired" => (sealedUser, now.AddDays(8)),
            "other event" => (app.Seal(UserContextApp.Ada(), "shop/other"), now),
            "unknown version" => (UserContextApp.Envelope(text, 2), now),
            _ => ($$"""{"{{UserContextCodec.Key}}":"nonsense"}""", now),
        };

        var result = await app.CallAsync(UserContextApp.Observe(), Event(user), runStart);

        result.StatusCode.ShouldBe(200);
        var seen = UserContextApp.SeenIn(result);
        seen.IsAuthenticated.ShouldBeFalse();
        seen.Claims.ShouldBeEmpty();
        var warning = app.Logs.GetSnapshot().Where(record => record.Level >= LogLevel.Warning).ShouldHaveSingleItem();
        warning.Message.ShouldContain(reason);
        warning.Message.ShouldContain(UserContextApp.EventName);
        warning.Category.ShouldBe("Inngest.Function.fn");
        foreach (var record in app.Logs.GetSnapshot())
        {
            record.Message.ShouldNotContain(text);
            record.Message.ShouldNotContain("u-42");
            record.Message.ShouldNotContain("ada@example.com");
            record.Exception.ShouldBeNull();
        }
    }

    [Fact]
    public async Task A_user_made_by_an_app_with_other_keys_runs_as_nobody()
    {
        await using var sender = NewApp();
        await using var worker = NewApp();

        var result = await worker.CallAsync(UserContextApp.Observe(), Event(sender.Seal(UserContextApp.Ada())), runStart: worker.Time.GetUtcNow());

        UserContextApp.SeenIn(result).IsAuthenticated.ShouldBeFalse();
        worker.Logs.GetSnapshot().Where(record => record.Level >= LogLevel.Warning).ShouldHaveSingleItem().Message.ShouldContain("unprotected");
    }

    [Fact]
    public async Task The_warning_is_written_once_for_the_run_and_not_for_every_call()
    {
        await using var app = NewApp();
        var function = TestFunction.Define(async context =>
        {
            await context.Step.RunAsync("a", () => Task.FromResult(1));
            await context.Step.RunAsync("b", () => Task.FromResult(2));
            return UserContextApp.SeenBy(context);
        });
        var user = UserContextApp.Envelope("not protected");

        await app.CallAsync(function, Event(user));
        await app.CallAsync(function, Event(user).Data("a", "1"));
        var last = await app.CallAsync(function, Event(user).Data("a", "1").Data("b", "2"));

        last.StatusCode.ShouldBe(200);
        app.Logs.GetSnapshot().Where(record => record.Level >= LogLevel.Warning).Count().ShouldBe(1);
    }

    [Theory]
    [InlineData("expired", 400, true)]
    [InlineData("other event", 400, true)]
    [InlineData("unknown version", 400, true)]
    [InlineData("tampered", 500, false)]
    public async Task A_run_can_be_made_to_fail_on_a_user_it_cannot_use(string problem, int status, bool final)
    {
        await using var app = NewApp(options => options.OnInvalid = InvalidUserContextAction.FailRun);
        var now = app.Time.GetUtcNow();
        var sealedUser = app.Seal(UserContextApp.Ada());
        var text = JsonDocument.Parse(sealedUser).RootElement.GetProperty(UserContextCodec.Key).GetProperty("t").GetString()!;
        var (user, runStart) = problem switch
        {
            "expired" => (sealedUser, now.AddDays(8)),
            "other event" => (app.Seal(UserContextApp.Ada(), "shop/other"), now),
            "unknown version" => (UserContextApp.Envelope(text, 2), now),
            _ => (UserContextApp.Envelope(text[..^3] + (text[^3] == 'A' ? 'B' : 'A') + text[^2..]), now),
        };
        var ran = false;

        var result = await app.CallAsync(UserContextApp.Observe(before: _ => ran = true), Event(user), runStart);

        ran.ShouldBeFalse();
        result.StatusCode.ShouldBe(status);
        result.NoRetry.ShouldBe(final);
        result.Body.ShouldContain("cannot be used");
        result.Body.ShouldNotContain("u-42");
    }

    [Fact]
    public async Task Failing_on_an_invalid_user_does_not_touch_runs_that_have_none_or_a_valid_one()
    {
        await using var app = NewApp(options => options.OnInvalid = InvalidUserContextAction.FailRun);

        var none = await app.CallAsync(UserContextApp.Observe(), Event("{}"));
        var valid = await app.CallAsync(UserContextApp.Observe(), Event(app.Seal(UserContextApp.Ada())), runStart: app.Time.GetUtcNow());

        none.StatusCode.ShouldBe(200);
        valid.StatusCode.ShouldBe(200);
        UserContextApp.SeenIn(valid).IsAuthenticated.ShouldBeTrue();
    }

    [Fact]
    public async Task A_run_that_started_in_time_keeps_its_user_on_every_later_call_however_late()
    {
        await using var app = NewApp(options => options.Lifetime = TimeSpan.FromHours(1));
        var started = app.Time.GetUtcNow();
        var user = Event(app.Seal(UserContextApp.Ada()));
        var function = TestFunction.Define(async context =>
        {
            var before = UserContextApp.SeenBy(context);
            await context.Step.RunAsync("sleep-then-work", () => Task.FromResult(1));
            return new { Before = before, After = UserContextApp.SeenBy(context) };
        });

        var first = await app.CallAsync(function, user, started);
        app.Time.Advance(TimeSpan.FromDays(30));
        var second = await app.CallAsync(function, Event(app.Seal(UserContextApp.Ada())).Data("sleep-then-work", "1"), started);

        first.StatusCode.ShouldBe(206);
        second.StatusCode.ShouldBe(200);
        var seen = second.Json;
        seen.GetProperty("before").GetProperty("isAuthenticated").GetBoolean().ShouldBeTrue();
        seen.GetProperty("after").GetProperty("isAuthenticated").GetBoolean().ShouldBeTrue();
        seen.GetProperty("after").GetProperty("claims").GetArrayLength().ShouldBe(5);
    }

    [Fact]
    public async Task A_run_created_after_the_expiry_gets_nobody_even_with_a_payload_that_is_fresh_to_the_clock()
    {
        await using var app = NewApp(options => options.Lifetime = TimeSpan.FromHours(1));
        var user = app.Seal(UserContextApp.Ada());

        var result = await app.CallAsync(UserContextApp.Observe(), Event(user), runStart: app.Time.GetUtcNow().AddHours(2));

        UserContextApp.SeenIn(result).IsAuthenticated.ShouldBeFalse();
    }

    [Fact]
    public async Task An_event_that_asks_to_start_after_the_expiry_gets_nobody_though_its_run_was_created_in_time()
    {
        // Whoever holds the event key can send a copied payload with a timestamp years ahead, and Inngest creates the run
        // right away. The run would start long after the payload expired.
        await using var app = NewApp(options => options.Lifetime = TimeSpan.FromHours(1));
        var created = app.Time.GetUtcNow();
        var user = app.Seal(UserContextApp.Ada());

        var result = await app.CallAsync(UserContextApp.Observe(), Call.New().Event(UserContextApp.EventJson(user, timestamp: created.AddYears(3))), created);

        UserContextApp.SeenIn(result).IsAuthenticated.ShouldBeFalse();
        app.Logs.GetSnapshot().Where(record => record.Level >= LogLevel.Warning).ShouldHaveSingleItem().Message.ShouldContain("it has expired");
    }

    [Fact]
    public async Task An_event_that_asks_to_start_within_the_lifetime_keeps_its_user()
    {
        await using var app = NewApp(options => options.Lifetime = TimeSpan.FromHours(1));
        var created = app.Time.GetUtcNow();
        var user = app.Seal(UserContextApp.Ada());

        var result = await app.CallAsync(UserContextApp.Observe(), Call.New().Event(UserContextApp.EventJson(user, timestamp: created.AddMinutes(30))), created);

        UserContextApp.SeenIn(result).IsAuthenticated.ShouldBeTrue();
    }

    [Fact]
    public async Task A_timestamp_in_the_past_does_not_make_a_late_run_younger()
    {
        await using var app = NewApp(options => options.Lifetime = TimeSpan.FromHours(1));
        var user = app.Seal(UserContextApp.Ada());
        var created = app.Time.GetUtcNow().AddHours(2);

        var result = await app.CallAsync(UserContextApp.Observe(), Call.New().Event(UserContextApp.EventJson(user, timestamp: created.AddDays(-5))), created);

        UserContextApp.SeenIn(result).IsAuthenticated.ShouldBeFalse();
    }

    [Fact]
    public async Task A_run_delayed_by_a_queue_after_it_was_created_in_time_keeps_its_user()
    {
        await using var app = NewApp(options => options.Lifetime = TimeSpan.FromHours(1));
        var created = app.Time.GetUtcNow();
        var user = Call.New().Event(UserContextApp.EventJson(app.Seal(UserContextApp.Ada()), timestamp: created));
        app.Time.Advance(TimeSpan.FromDays(3));

        // Its first call comes three days after it was created and after the payload expired.
        var result = await app.CallAsync(UserContextApp.Observe(), user, created);

        UserContextApp.SeenIn(result).IsAuthenticated.ShouldBeTrue();
    }

    [Fact]
    public async Task A_run_id_that_is_not_a_ulid_is_judged_by_the_clock_of_the_call()
    {
        await using var app = NewApp(options => options.Lifetime = TimeSpan.FromHours(1));
        var user = app.Seal(UserContextApp.Ada());

        var early = await app.CallAsync(UserContextApp.Observe(), Event(user));
        app.Time.Advance(TimeSpan.FromHours(2));
        var late = await app.CallAsync(UserContextApp.Observe(), Event(user));

        UserContextApp.SeenIn(early).IsAuthenticated.ShouldBeTrue();
        UserContextApp.SeenIn(late).IsAuthenticated.ShouldBeFalse();
    }

    [Fact]
    public async Task Services_created_before_the_function_starts_see_the_user_when_they_use_it()
    {
        await using var app = NewApp();
        var function = new FunctionDefinition(UserContextApp.Options(), services =>
        {
            var current = services.GetRequiredService<ICurrentUser>();
            var principalAtCreation = services.GetRequiredService<ClaimsPrincipal>();
            return new DelegateFunction<JsonElement>((_, _) => Task.FromResult<object?>(new
            {
                Current = current.IsAuthenticated,
                PrincipalAtCreation = principalAtCreation.Identity?.IsAuthenticated == true,
            }));
        });

        var result = await app.CallAsync(function, Event(app.Seal(UserContextApp.Ada())), runStart: app.Time.GetUtcNow());

        result.Json.GetProperty("current").GetBoolean().ShouldBeTrue();
        result.Json.GetProperty("principalAtCreation").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task A_step_handler_sees_the_same_user_as_the_function()
    {
        await using var app = NewApp();
        var function = TestFunction.Define(async context =>
            await context.Step.RunAsync("who", () => Task.FromResult(UserContextApp.SeenBy(context))));

        var result = await app.CallAsync(function, Event(app.Seal(UserContextApp.Ada())), runStart: app.Time.GetUtcNow());

        result.OpCodes.ShouldBe(["StepRun"]);
        result.Op().GetProperty("data").GetProperty("isAuthenticated").GetBoolean().ShouldBeTrue();
        result.Op().GetProperty("data").GetProperty("principal").GetInt32().ShouldBe(5);
    }

    [Fact]
    public async Task A_scope_made_inside_a_step_sees_the_same_user_as_the_function()
    {
        // What a scoped step does: its handler gets a scope next to the call's, made from the same factory.
        await using var app = NewApp();
        var function = TestFunction.Define(async context =>
            await context.Step.RunAsync("in-a-scope", async () =>
            {
                await using var scope = context.Services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
                var user = scope.ServiceProvider.GetRequiredService<ICurrentUser>();
                return new
                {
                    SameService = ReferenceEquals(user, context.Services.GetRequiredService<ICurrentUser>()),
                    user.IsAuthenticated,
                    Claims = user.Principal.Claims.Count(),
                    FromThePrincipalService = scope.ServiceProvider.GetRequiredService<ClaimsPrincipal>().Claims.Count(),
                    SamePrincipal = ReferenceEquals(user.Principal, context.Services.GetRequiredService<ICurrentUser>().Principal),
                };
            }));

        var result = await app.CallAsync(function, Event(app.Seal(UserContextApp.Ada())), runStart: app.Time.GetUtcNow());

        var data = result.Op().GetProperty("data");
        data.GetProperty("sameService").GetBoolean().ShouldBeTrue();
        data.GetProperty("isAuthenticated").GetBoolean().ShouldBeTrue();
        data.GetProperty("claims").GetInt32().ShouldBe(5);
        data.GetProperty("fromThePrincipalService").GetInt32().ShouldBe(5);
        data.GetProperty("samePrincipal").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_singleton_and_code_run_off_the_scheduler_see_the_user_of_the_run_that_calls_them()
    {
        await using var app = NewApp(inngest: inngest => inngest.Services.AddSingleton<Audit>());
        var function = TestFunction.Define(async context =>
            await context.Step.RunAsync("audit", async () =>
            {
                var audit = context.Services.GetRequiredService<Audit>();
                var inThePool = await Task.Run(() => audit.Who());
                return new { Direct = audit.Who(), InThePool = inThePool };
            }));

        var result = await app.CallAsync(function, Event(app.Seal(UserContextApp.Ada())), runStart: app.Time.GetUtcNow());

        result.Op().GetProperty("data").GetProperty("direct").GetString().ShouldBe("Ada Lovelace");
        result.Op().GetProperty("data").GetProperty("inThePool").GetString().ShouldBe("Ada Lovelace");
    }

    [Fact]
    public async Task Concurrent_runs_each_see_their_own_user()
    {
        await using var app = NewApp(inngest: inngest => inngest.Services.AddSingleton<Audit>());
        var gate = new TaskCompletionSource();
        var function = TestFunction.Define(async context =>
        {
            await context.Step.RunAsync("audit", async () =>
            {
                await gate.Task;
                return context.Services.GetRequiredService<Audit>().Who();
            });
            return "done";
        });
        var ada = Event(app.Seal(UserContextApp.Ada()));
        var grace = Event(app.Seal(UserContextApp.Principal("Cookies", new Claim("sub", "u-7"), new Claim("name", "Grace Hopper"))));

        var adaCall = app.CallAsync(function, ada, runStart: app.Time.GetUtcNow());
        var graceCall = app.CallAsync(function, grace, runStart: app.Time.GetUtcNow());
        gate.SetResult();
        var results = await Task.WhenAll(adaCall, graceCall);

        results[0].Op().GetProperty("data").GetString().ShouldBe("Ada Lovelace");
        results[1].Op().GetProperty("data").GetString().ShouldBe("Grace Hopper");
    }

    [Fact]
    public async Task Outside_a_run_the_current_user_is_nobody_and_the_view_of_the_last_run_is_not_kept()
    {
        await using var app = NewApp();
        var user = app.Services.GetRequiredService<ICurrentUser>();

        await app.CallAsync(UserContextApp.Observe(), Event(app.Seal(UserContextApp.Ada())), runStart: app.Time.GetUtcNow());

        user.IsAuthenticated.ShouldBeFalse();
        user.IssuedAt.ShouldBeNull();
        user.Principal.Claims.ShouldBeEmpty();
        user.Principal.ShouldNotBeSameAs(user.Principal);
    }

    [Fact]
    public async Task Each_call_has_a_user_of_its_own()
    {
        await using var app = NewApp();
        var function = UserContextApp.Observe();

        var first = await app.CallAsync(function, Event(app.Seal(UserContextApp.Ada())), runStart: app.Time.GetUtcNow());
        var second = await app.CallAsync(function, Event("{}"));

        UserContextApp.SeenIn(first).IsAuthenticated.ShouldBeTrue();
        UserContextApp.SeenIn(second).IsAuthenticated.ShouldBeFalse();
    }

    [Fact]
    public async Task A_function_that_batches_events_has_no_single_user()
    {
        await using var app = NewApp(inngest: inngest =>
        {
            inngest.AddFunction(
                new FunctionOptions { Id = "batched", Triggers = [Trigger.Event(UserContextApp.EventName)], BatchEvents = new BatchEvents(10, TimeSpan.FromSeconds(5)) },
                (context, _) => Task.FromResult<object?>(UserContextApp.SeenBy(context)));
            inngest.AddFunction(
                new FunctionOptions { Id = "single", Triggers = [Trigger.Event(UserContextApp.EventName)] },
                (context, _) => Task.FromResult<object?>(UserContextApp.SeenBy(context)));
        });
        var ada = UserContextApp.EventJson(app.Seal(UserContextApp.Ada()));
        var grace = UserContextApp.EventJson(app.Seal(UserContextApp.Principal("Cookies", new Claim("sub", "u-7"))));

        var batch = await app.CallAsync(app.Function("batched"), Call.New().Event(ada).Events(ada, grace), app.Time.GetUtcNow());
        var oneEvent = await app.CallAsync(app.Function("batched"), Call.New().Event(ada), app.Time.GetUtcNow());
        var single = await app.CallAsync(app.Function("single"), Call.New().Event(ada), app.Time.GetUtcNow());

        UserContextApp.SeenIn(batch).IsAuthenticated.ShouldBeFalse();
        UserContextApp.SeenIn(oneEvent).IsAuthenticated.ShouldBeFalse();
        UserContextApp.SeenIn(single).IsAuthenticated.ShouldBeTrue();
    }

    [Fact]
    public async Task A_failure_handler_gets_the_user_of_the_event_that_started_the_failed_run()
    {
        UserContextApp.Seen? seen = null;
        await using var app = NewApp(inngest: inngest =>
        {
            inngest.AddFunction(
                new FunctionOptions { Id = "fn", Triggers = [Trigger.Event(UserContextApp.EventName)] },
                (_, _) => throw new NonRetriableException("boom"),
                (context, _) =>
                {
                    seen = UserContextApp.SeenBy(context);
                    return Task.CompletedTask;
                });
        });
        var failedRun = app.Time.GetUtcNow();
        var original = UserContextApp.EventJson(app.Seal(UserContextApp.Ada()));
        app.Time.Advance(TimeSpan.FromDays(30));

        // The run failed 30 days after it started; the handler's own run is as new as the failure.
        var result = await app.CallAsync(app.Function("fn-failure"), Call.New().Event(FailedEvent("test-app-fn", RunId(failedRun), original)), app.Time.GetUtcNow());

        result.StatusCode.ShouldBe(200);
        seen.ShouldNotBeNull().IsAuthenticated.ShouldBeTrue();
        seen.Claims.ShouldBe(AdaClaims);
    }

    [Fact]
    public async Task Other_functions_that_listen_for_failures_do_not_get_a_user()
    {
        await using var app = NewApp(inngest: inngest =>
        {
            inngest.AddFunction(
                new FunctionOptions { Id = "alert", Triggers = [Trigger.Event(SystemEvents.FunctionFailed)] },
                (context, _) => Task.FromResult<object?>(UserContextApp.SeenBy(context)));
        });
        var now = app.Time.GetUtcNow();
        var original = UserContextApp.EventJson(app.Seal(UserContextApp.Ada()));

        var result = await app.CallAsync(app.Function("alert"), Call.New().Event(FailedEvent("test-app-fn", RunId(now), original)), now);

        UserContextApp.SeenIn(result).IsAuthenticated.ShouldBeFalse();
    }

    [Fact]
    public async Task The_failure_handler_of_a_function_that_batches_gets_no_user()
    {
        UserContextApp.Seen? seen = null;
        await using var app = NewApp(inngest: inngest =>
        {
            inngest.AddFunction(
                new FunctionOptions { Id = "batched", Triggers = [Trigger.Event(UserContextApp.EventName)], BatchEvents = new BatchEvents(10, TimeSpan.FromSeconds(5)) },
                (_, _) => throw new NonRetriableException("boom"),
                (context, _) =>
                {
                    seen = UserContextApp.SeenBy(context);
                    return Task.CompletedTask;
                });
        });
        var now = app.Time.GetUtcNow();
        var original = UserContextApp.EventJson(app.Seal(UserContextApp.Ada()));

        await app.CallAsync(app.Function("batched-failure"), Call.New().Event(FailedEvent("test-app-batched", RunId(now), original)), now);

        seen.ShouldNotBeNull().IsAuthenticated.ShouldBeFalse();
    }

    [Fact]
    public async Task A_failure_handler_judges_the_user_by_the_run_that_failed()
    {
        UserContextApp.Seen? seen = null;
        await using var app = NewApp(inngest: inngest =>
        {
            inngest.AddFunction(
                new FunctionOptions { Id = "fn", Triggers = [Trigger.Event(UserContextApp.EventName)] },
                (_, _) => throw new NonRetriableException("boom"),
                (context, _) =>
                {
                    seen = UserContextApp.SeenBy(context);
                    return Task.CompletedTask;
                });
        });
        var original = UserContextApp.EventJson(app.Seal(UserContextApp.Ada()));
        var tooLate = app.Time.GetUtcNow().AddDays(8);

        await app.CallAsync(app.Function("fn-failure"), Call.New().Event(FailedEvent("test-app-fn", RunId(tooLate), original)), app.Time.GetUtcNow());

        seen.ShouldNotBeNull().IsAuthenticated.ShouldBeFalse();
    }

    [Fact]
    public async Task A_failure_handler_judges_the_user_by_the_timestamp_of_the_event_of_the_run_that_failed_too()
    {
        UserContextApp.Seen? seen = null;
        await using var app = NewApp(options => options.Lifetime = TimeSpan.FromHours(1), inngest =>
        {
            inngest.AddFunction(
                new FunctionOptions { Id = "fn", Triggers = [Trigger.Event(UserContextApp.EventName)] },
                (_, _) => throw new NonRetriableException("boom"),
                (context, _) =>
                {
                    seen = UserContextApp.SeenBy(context);
                    return Task.CompletedTask;
                });
        });
        var now = app.Time.GetUtcNow();
        var original = UserContextApp.EventJson(app.Seal(UserContextApp.Ada()), timestamp: now.AddYears(3));

        await app.CallAsync(app.Function("fn-failure"), Call.New().Event(FailedEvent("test-app-fn", RunId(now), original)), now);

        seen.ShouldNotBeNull().IsAuthenticated.ShouldBeFalse();
    }

    [Fact]
    public async Task A_failure_handler_runs_as_nobody_under_FailRun_when_the_run_that_failed_failed_on_its_user()
    {
        UserContextApp.Seen? seen = null;
        await using var app = NewApp(
            options => options.OnInvalid = InvalidUserContextAction.FailRun,
            inngest => inngest.AddFunction(
                new FunctionOptions { Id = "fn", Triggers = [Trigger.Event(UserContextApp.EventName)] },
                (_, _) => Task.FromResult<object?>("never"),
                (context, _) =>
                {
                    seen = UserContextApp.SeenBy(context);
                    return Task.CompletedTask;
                }));
        var created = app.Time.GetUtcNow();
        var original = UserContextApp.EventJson(app.Seal(UserContextApp.Ada()));

        // The run was created after the payload expired, so FailRun failed it, and Inngest then sent function.failed.
        var late = created.AddDays(8);
        var main = await app.CallAsync(app.Function("fn"), Call.New().Event(original), late);
        var handler = await app.CallAsync(app.Function("fn-failure"), Call.New().Event(FailedEvent("test-app-fn", RunId(late), original)), late);

        main.StatusCode.ShouldBe(400);
        handler.StatusCode.ShouldBe(200);
        seen.ShouldNotBeNull().IsAuthenticated.ShouldBeFalse();
        app.Logs.GetSnapshot().Where(record => record.Level == LogLevel.Warning && record.Message.Contains("it has expired", StringComparison.Ordinal)).ShouldHaveSingleItem();
    }

    private static string RunId(DateTimeOffset time) => UserContextApp.RunIdAt(time);

    // The event Inngest sends when a run fails, which carries the event of the run that failed.
    private static string FailedEvent(string functionId, string failedRunId, string originalEvent) =>
        $$$"""{"name":"inngest/function.failed","id":"01JFAILED","ts":1793268000000,"user":{},"data":{"function_id":"{{{functionId}}}","run_id":"{{{failedRunId}}}","error":{"name":"Error","message":"boom"},"event":{{{originalEvent}}}}}""";
}
