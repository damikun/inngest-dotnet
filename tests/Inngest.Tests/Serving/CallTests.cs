using Inngest.Tests.Serving.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Inngest.Tests.Serving;

public sealed class CallTests
{
    private const string Body = """{"ctx":{"attempt":0,"run_id":"01J0000000000000000000000"},"event":{"data":{"userId":1},"name":"user/signed.up"},"steps":{},"version":2}""";

    private readonly CommHandlerHarness _harness = new();

    [Fact]
    public async Task A_call_runs_the_function_named_by_fnId()
    {
        var request = Call("?fnId=shop-send-email-failure&stepId=step");

        using var response = await _harness.SendAsync(CommHandlerHarness.DevSettings, request);

        var call = _harness.Engine.Calls.ShouldHaveSingleItem();
        call.Function.ShouldBeSameAs(_harness.Registry.Definitions[1]);
        call.BodyText.ShouldBe(Body);
        call.StepId.ShouldBe("step");
        response.StatusCode.ShouldBe(200);
        response.BodyText().ShouldBe("\"done\"");
    }

    [Fact]
    public async Task The_step_id_falls_back_to_the_step_header()
    {
        var request = Call("?fnId=shop-send-email").WithHeader("X-Inngest-Step-ID", "e7d8a2f140845095749d60246ff1110c9d01d76a");

        using var response = await _harness.SendAsync(CommHandlerHarness.DevSettings, request);

        _harness.Engine.Calls.ShouldHaveSingleItem().StepId.ShouldBe("e7d8a2f140845095749d60246ff1110c9d01d76a");
    }

    [Fact]
    public async Task The_step_id_in_the_query_wins_over_the_header()
    {
        var request = Call("?fnId=shop-send-email&stepId=step").WithHeader("X-Inngest-Step-ID", "e7d8a2f140845095749d60246ff1110c9d01d76a");

        using var response = await _harness.SendAsync(CommHandlerHarness.DevSettings, request);

        _harness.Engine.Calls.ShouldHaveSingleItem().StepId.ShouldBe("step");
    }

    [Fact]
    public async Task The_engine_gets_the_request_job_and_generation_ids_of_the_call()
    {
        var request = Call("?fnId=shop-send-email&stepId=step")
            .WithHeader("X-Request-Id", "req-1")
            .WithHeader("X-Inngest-Job-Id", "job-1")
            .WithHeader("X-Inngest-Generation-Id", "3");

        using var response = await _harness.SendAsync(CommHandlerHarness.DevSettings, request);

        var call = _harness.Engine.Calls.ShouldHaveSingleItem();
        call.RequestId.ShouldBe("req-1");
        call.JobId.ShouldBe("job-1");
        call.GenerationId.ShouldBe(3);
    }

    [Fact]
    public async Task Missing_or_unreadable_ids_reach_the_engine_as_null()
    {
        var request = Call("?fnId=shop-send-email&stepId=step").WithHeader("X-Request-Id", "").WithHeader("X-Inngest-Generation-Id", "-1");

        using var response = await _harness.SendAsync(CommHandlerHarness.DevSettings, request);

        var call = _harness.Engine.Calls.ShouldHaveSingleItem();
        call.RequestId.ShouldBeNull();
        call.JobId.ShouldBeNull();
        call.GenerationId.ShouldBeNull();
    }

    [Fact]
    public async Task Without_a_step_id_the_engine_gets_none()
    {
        using var response = await _harness.SendAsync(CommHandlerHarness.DevSettings, Call("?fnId=shop-send-email&stepId="));

        _harness.Engine.Calls.ShouldHaveSingleItem().StepId.ShouldBeNull();
    }

    [Theory]
    [InlineData(200, """{"ok":true}""", null, null)]
    [InlineData(206, """[{"id":"e7d8a2f140845095749d60246ff1110c9d01d76a","op":"StepPlanned"}]""", null, null)]
    [InlineData(206, """[{"id":"e7d8a2f140845095749d60246ff1110c9d01d76a","op":"StepError"}]""", false, "30")]
    [InlineData(206, """[{"id":"e7d8a2f140845095749d60246ff1110c9d01d76a","op":"StepFailed"}]""", true, null)]
    [InlineData(500, """{"name":"Error","message":"boom","stack":""}""", false, "2026-10-29T13:00:00.000Z")]
    [InlineData(400, """{"name":"NonRetriableException","message":"nope","stack":""}""", true, null)]
    public async Task The_engine_result_becomes_the_response(int statusCode, string body, bool? noRetry, string? retryAfter)
    {
        _harness.Engine.Handler = (_, _) => Task.FromResult(FakeExecutionEngine.Result(statusCode, body, noRetry, retryAfter));

        using var response = await _harness.SendAsync(CommHandlerHarness.DevSettings, Call("?fnId=shop-send-email&stepId=step"));

        response.StatusCode.ShouldBe(statusCode);
        response.BodyText().ShouldBe(body);
        response.Header("X-Inngest-No-Retry").ShouldBe(noRetry switch { true => "true", false => "false", null => null });
        response.Header("Retry-After").ShouldBe(retryAfter);
        response.Header("X-Inngest-Req-Version").ShouldBe("2");
    }

    [Fact]
    public async Task The_response_owns_the_engine_result_and_releases_it()
    {
        var response = await _harness.SendAsync(CommHandlerHarness.DevSettings, Call("?fnId=shop-send-email&stepId=step"));
        var result = _harness.Engine.LastResult.ShouldNotBeNull();
        result.Body.WrittenCount.ShouldBe(6);

        response.Dispose();

        result.Body.WrittenCount.ShouldBe(0);
    }

    [Fact]
    public async Task An_unknown_function_answers_500()
    {
        using var response = await _harness.SendAsync(CommHandlerHarness.DevSettings, Call("?fnId=shop-missing&stepId=step"));

        response.StatusCode.ShouldBe(500);
        response.BodyText().ShouldBe(
            """{"type":"internal","name":"Error","message":"Function \"shop-missing\" was not found in this app; is the app synced correctly?","stack":""}""");
        response.Header("X-Inngest-No-Retry").ShouldBeNull();
        _harness.Engine.Calls.ShouldBeEmpty();
        _harness.Logger.LatestRecord.Id.Id.ShouldBe(404);
    }

    [Fact]
    public async Task A_call_without_a_function_id_answers_500()
    {
        using var response = await _harness.SendAsync(CommHandlerHarness.DevSettings, Call("?stepId=step"));

        response.StatusCode.ShouldBe(500);
        response.BodyText().ShouldBe("""{"type":"internal","name":"Error","message":"No function ID found in request","stack":""}""");
        _harness.Engine.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_call_without_a_body_answers_401_like_the_TypeScript_SDK()
    {
        using var response = await _harness.SendAsync(
            CommHandlerHarness.DevSettings,
            new FakeServeRequest("POST", "http://localhost:3000/api/inngest?fnId=shop-send-email&stepId=step"));

        response.StatusCode.ShouldBe(401);
        response.BodyText().ShouldBe("""{"message":"Unauthorized"}""");
        _harness.Engine.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_engine_failure_answers_500_with_the_exception()
    {
        _harness.Engine.Handler = (_, _) => throw new InvalidOperationException("The request body has no ctx.");

        using var response = await _harness.SendAsync(CommHandlerHarness.DevSettings, Call("?fnId=shop-send-email&stepId=step"));

        response.StatusCode.ShouldBe(500);
        response.Header("X-Inngest-No-Retry").ShouldBeNull();
        using var body = System.Text.Json.JsonDocument.Parse(response.Body);
        var root = body.RootElement;
        root.EnumerateObject().Select(property => property.Name).ShouldBe(["type", "name", "message", "stack"]);
        root.GetProperty("type").GetString().ShouldBe("internal");
        root.GetProperty("name").GetString().ShouldBe("InvalidOperationException");
        root.GetProperty("message").GetString().ShouldBe("The request body has no ctx.");
        root.GetProperty("stack").GetString().ShouldStartWith("System.InvalidOperationException: The request body has no ctx.");
        _harness.Logger.LatestRecord.Level.ShouldBe(LogLevel.Error);
        _harness.Logger.LatestRecord.Exception.ShouldBeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task An_engine_that_gives_up_on_its_own_answers_500()
    {
        _harness.Engine.Handler = (_, _) => throw new TaskCanceledException("Fetching the run state timed out.");

        using var response = await _harness.SendAsync(CommHandlerHarness.DevSettings, Call("?fnId=shop-send-email&stepId=step"));

        response.StatusCode.ShouldBe(500);
        response.BodyText().ShouldContain("\"name\":\"TaskCanceledException\"");
    }

    [Fact]
    public async Task A_cancelled_request_cancels_the_engine_and_is_not_answered()
    {
        using var cancellation = new CancellationTokenSource();
        _harness.Engine.Handler = async (_, cancellationToken) =>
        {
            await cancellation.CancelAsync();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        };
        var handler = _harness.CreateHandler(CommHandlerHarness.DevSettings);

        await Should.ThrowAsync<OperationCanceledException>(() => handler.HandleAsync(Call("?fnId=shop-send-email&stepId=step"), cancellation.Token));

        _harness.Engine.Calls.ShouldHaveSingleItem().CancellationToken.ShouldBe(cancellation.Token);
    }

    [Fact]
    public async Task Without_request_services_each_call_runs_in_its_own_scope()
    {
        var resolved = new List<ScopedDependency>();
        _harness.Engine.Handler = (request, _) =>
        {
            var dependency = request.Services.GetRequiredService<ScopedDependency>();
            dependency.IsDisposed.ShouldBeFalse();
            resolved.Add(dependency);
            return Task.FromResult(FakeExecutionEngine.Result(200, "null"));
        };
        var handler = _harness.CreateHandler(CommHandlerHarness.DevSettings);

        using (await handler.HandleAsync(Call("?fnId=shop-send-email&stepId=step"), TestContext.Current.CancellationToken))
        using (await handler.HandleAsync(Call("?fnId=shop-send-email&stepId=step"), TestContext.Current.CancellationToken))
        {
        }

        resolved.Count.ShouldBe(2);
        resolved[0].ShouldNotBeSameAs(resolved[1]);
        resolved.ShouldAllBe(dependency => dependency.IsDisposed);
        _harness.Engine.Calls.ShouldAllBe(call => call.Services != _harness.RootServices);
    }

    [Fact]
    public async Task Request_services_are_used_as_given()
    {
        using var scope = _harness.RootServices.CreateScope();
        var request = new FakeServeRequest("POST", "http://localhost:3000/api/inngest?fnId=shop-send-email&stepId=step", Body)
        {
            Services = scope.ServiceProvider,
        };

        using var response = await _harness.SendAsync(CommHandlerHarness.DevSettings, request);

        _harness.Engine.Calls.ShouldHaveSingleItem().Services.ShouldBeSameAs(scope.ServiceProvider);
        scope.ServiceProvider.GetRequiredService<ScopedDependency>().IsDisposed.ShouldBeFalse();
    }

    [Fact]
    public async Task The_trust_probe_answers_200_with_an_empty_signed_body()
    {
        var request = _harness.Sign(Call("?probe=trust"), CommHandlerHarness.PrimaryKey);

        using var response = await _harness.SendAsync(CommHandlerHarness.CloudSettings, request);

        response.StatusCode.ShouldBe(200);
        response.Body.IsEmpty.ShouldBeTrue();
        response.Header("Content-Type").ShouldBe("application/json");
        _harness.IsSignedWith(response, CommHandlerHarness.PrimaryKey).ShouldBeTrue();
        _harness.Engine.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_trust_probe_requires_a_valid_signature()
    {
        using var response = await _harness.SendAsync(CommHandlerHarness.CloudSettings, Call("?probe=trust"));

        response.StatusCode.ShouldBe(401);
    }

    [Fact]
    public async Task An_unknown_probe_answers_400()
    {
        using var response = await _harness.SendAsync(CommHandlerHarness.DevSettings, Call("?probe=health&fnId=shop-send-email"));

        response.StatusCode.ShouldBe(400);
        response.BodyText().ShouldBe("""{"name":"Error","message":"Unknown probe \"health\"","stack":""}""");
        _harness.Engine.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_signed_call_in_cloud_mode_runs_and_is_signed()
    {
        var request = _harness.Sign(Call("?fnId=shop-send-email&stepId=step"), CommHandlerHarness.PrimaryKey);

        using var response = await _harness.SendAsync(CommHandlerHarness.CloudSettings, request);

        response.StatusCode.ShouldBe(200);
        _harness.Engine.Calls.ShouldHaveSingleItem().BodyText.ShouldBe(Body);
        _harness.IsSignedWith(response, CommHandlerHarness.PrimaryKey).ShouldBeTrue();
    }

    private static FakeServeRequest Call(string query) =>
        new("POST", "http://localhost:3000/api/inngest" + query, Body);
}
