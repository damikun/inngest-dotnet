using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Inngest.IntegrationTests.Functions;
using Inngest.IntegrationTests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests;

// The app in cloud mode against a self-hosted server that signs every request with the app's signing key.
[Collection(SigningCollection.Name)]
public sealed class SigningTests(SigningFixture fixture) : IntegrationTest<SigningFixture>(fixture)
{
    private static readonly HttpClient Http = new();

    public enum BadSignature
    {
        Missing,
        WrongKey,
        Expired,
    }

    [Fact(Timeout = 30_000)]
    public async Task Syncs_through_a_signed_request_and_an_authenticated_registration()
    {
        var functions = await Api.GetAppFunctionsAsync(Fixture.AppId, Cancellation);

        functions.Select(function => function.Slug).ShouldBe(SigningFunctions.Slugs, ignoreOrder: true);
        var sync = Fixture.App.Requests.Snapshot().First(request => request.Method == "PUT");
        sync.ServerKind.ShouldBe("cloud");
        sync.Signed.ShouldBeTrue();
        sync.StatusCode.ShouldBe(200);
    }

    [Fact(Timeout = 30_000)]
    public async Task Runs_steps_over_signed_calls_and_signs_every_response()
    {
        var run = await RunToEndAsync("itests/signed.steps", new NumberEvent(TestId, 2));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<SequenceResult>().ShouldBe(new SequenceResult(3, 30, "3:30"));
        Observed.Names.ShouldBe(["first", "second"]);

        // One call per step plus the one that returns the output.
        var calls = Fixture.App.Requests.Snapshot()
            .Where(request => request.Method == "POST" && request.Target.Contains("fnId=itests-signed-steps", StringComparison.Ordinal))
            .ToList();
        calls.Count.ShouldBeGreaterThanOrEqualTo(3);
        calls.ShouldAllBe(call => call.Signed && call.ResponseSigned && (call.StatusCode == 200 || call.StatusCode == 206));
    }

    [Fact(Timeout = 30_000)]
    public async Task Client_sends_events_with_the_event_key()
    {
        var client = Fixture.App.Services.GetRequiredService<IInngestClient>();

        var sent = await client.SendAsync(new InngestEvent("itests/signed.hello", new GreetEvent(TestId, "cloud")), Cancellation);
        var run = await Api.WaitForRunEndAsync(sent.Ids.ShouldHaveSingleItem(), Cancellation);

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<Greeting>().ShouldBe(new Greeting("Hello, cloud!"));
    }

    [Fact(Timeout = 30_000)]
    public async Task App_runs_a_call_signed_with_its_key_and_signs_the_response()
    {
        var body = CallBody("direct");

        using var response = await CallAppAsync(body, Signatures.Create(body, Fixture.SigningKey, DateTimeOffset.UtcNow));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var responseBody = await response.Content.ReadAsByteArrayAsync(Cancellation);
        response.Headers.TryGetValues("X-Inngest-Signature", out var signature).ShouldBeTrue();
        Signatures.IsValid(signature.ShouldHaveSingleItem(), responseBody, Fixture.SigningKey).ShouldBeTrue();
        JsonSerializer.Deserialize<Greeting>(responseBody, JsonSerializerOptions.Web).ShouldBe(new Greeting("Hello, direct!"));
        Observed.Names.ShouldBe(["ran"]);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(BadSignature.Missing)]
    [InlineData(BadSignature.WrongKey)]
    [InlineData(BadSignature.Expired)]
    public async Task App_rejects_a_call_that_is_not_signed_with_its_key(BadSignature signature)
    {
        var body = CallBody("intruder");
        var header = signature switch
        {
            BadSignature.Missing => null,
            BadSignature.WrongKey => Signatures.Create(body, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), DateTimeOffset.UtcNow),
            BadSignature.Expired => Signatures.Create(body, Fixture.SigningKey, DateTimeOffset.UtcNow.AddMinutes(-10)),
            _ => throw new ArgumentOutOfRangeException(nameof(signature)),
        };

        using var response = await CallAppAsync(body, header);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        Observed.Names.ShouldBeEmpty();
    }

    // A call shaped like the ones the server sends (taken from captured requests), carrying this test's event.
    private byte[] CallBody(string name)
    {
        var runId = $"01JDIRECT{Convert.ToHexString(RandomNumberGenerator.GetBytes(9))[..17]}";
        var @event = new
        {
            data = new { name, testId = TestId },
            id = runId,
            name = "itests/signed.hello",
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            user = new { },
        };

        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            ctx = new
            {
                attempt = 0,
                disable_immediate_execution = false,
                env = string.Empty,
                fn_id = Guid.NewGuid(),
                max_attempts = 5,
                run_id = runId,
                stack = new { current = 0, stack = Array.Empty<string>() },
                step_id = "step",
                use_api = false,
            },
            @event,
            events = new[] { @event },
            steps = new { },
            use_api = false,
            version = -1,
        });
    }

    private async Task<HttpResponseMessage> CallAppAsync(byte[] body, string? signature)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Fixture.App.LocalUrl}?fnId=itests-signed-hello&stepId=step")
        {
            Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } },
        };
        if (signature is not null)
        {
            request.Headers.Add("X-Inngest-Signature", signature);
        }

        return await Http.SendAsync(request, Cancellation);
    }
}
