using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Evaluation.Harness;
using Inngest.IntegrationTests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.Evaluation;

// The app in cloud mode against a self-hosted server (inngest start) that signs every request with the signing key.
[Trait("Category", "Evaluation")]
[Trait("Area", "Cloud mode")]
public sealed class CloudScenarios(EvaluationEnvironment environment) : EvaluationScenario<CloudFixture>(environment)
{
    [Fact(Timeout = 60_000)]
    public async Task A_signed_run_with_a_step_a_sleep_a_wait_and_an_invoke_completes()
    {
        var client = Fixture.App.Services.GetRequiredService<IInngestClient>();
        var sent = await client.SendAsync(new InngestEvent(CloudFunctions.Flow, new ValueEvent(TestId, 4)), Cancellation);
        var run = await WaitForRunAsync(sent.Ids.ShouldHaveSingleItem());
        await WaitForStepAsync(run.RunId, StepOps.WaitForEvent, timeout: TimeSpan.FromSeconds(20));

        await client.SendAsync(new InngestEvent(CloudFunctions.Resume, new Resume(TestId, "signed")), Cancellation);
        var ended = await WaitForEndAsync(sent.Ids[0], TimeSpan.FromSeconds(30));

        ended.Status.ShouldBe(RunStatus.Completed);
        ended.OutputAs<CloudFlowResult>().ShouldBe(new CloudFlowResult(5, "signed", 15, "5/signed/15"));
        var calls = Fixture.App.Requests.Snapshot()
            .Where(request => request.Method == "POST" && (request.Target.Contains("fnId=eval-cloud-flow", StringComparison.Ordinal) || request.Target.Contains("fnId=eval-cloud-child", StringComparison.Ordinal)))
            .ToList();
        calls.Count.ShouldBeGreaterThanOrEqualTo(6);
        calls.ShouldAllBe(call => call.Signed && call.ResponseSigned && (call.StatusCode == 200 || call.StatusCode == 206));
    }

    // Past 4 MB the SDK loads the run's state from the API, authenticated with the hashed signing key.
    [Fact(Timeout = 120_000)]
    public async Task A_signed_run_past_4_MB_loads_its_state_with_the_hashed_signing_key()
    {
        var client = Fixture.App.Services.GetRequiredService<IInngestClient>();
        var sent = await client.SendAsync(new InngestEvent(CloudFunctions.BigState, new RunEvent(TestId)), Cancellation);
        var run = await WaitForEndAsync(sent.Ids.ShouldHaveSingleItem(), TimeSpan.FromSeconds(60));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<BigStateResult>().Hashes.ShouldBe(
            [.. Enumerable.Range(1, 3).Select(index => StepDataFunctions.Sha256(StepDataFunctions.LargeText($"{TestId}-{index}", LargeStateFunctions.ChunkLength)))]);
        var expected = $"Bearer {HashedKey(Fixture.SigningKey)}";
        Fixture.App.Services.GetRequiredService<OutgoingRequests>().Snapshot()
            .ShouldContain(request => request.PathAndQuery == $"/v0/runs/{run.RunId}/actions" && request.StatusCode == 200 && request.Authorization == expected);
    }

    // What Inngest compares the bearer token with: the SHA-256 of the hex-decoded key.
    public static string HashedKey(string key) => Convert.ToHexStringLower(SHA256.HashData(Convert.FromHexString(key)));
}

// A signing key rotation in progress: the server signs with the old key; the app has the new key and the old one as
// its fallback.
[Trait("Category", "Evaluation")]
[Trait("Area", "Cloud mode")]
public sealed class RotationScenarios(EvaluationEnvironment environment) : EvaluationScenario<RotationFixture>(environment)
{
    private static readonly HttpClient Http = new();

    [Fact(Timeout = 60_000)]
    public async Task During_a_key_rotation_the_app_syncs_and_runs_on_the_fallback_key()
    {
        var run = await RunToEndAsync(RotationFunctions.Steps, new ValueEvent(TestId, 20), TimeSpan.FromSeconds(30));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<int>().ShouldBe(41);

        // The register call tried the new key first, which the server does not know yet, and then the old one.
        var registers = Fixture.App.Services.GetRequiredService<OutgoingRequests>().Snapshot().Where(request => request.PathAndQuery.StartsWith("/fn/register", StringComparison.Ordinal)).ToList();
        registers.ShouldContain(request => request.Authorization == $"Bearer {CloudScenarios.HashedKey(Fixture.NewKey)}" && request.StatusCode == 401);
        registers.ShouldContain(request => request.Authorization == $"Bearer {CloudScenarios.HashedKey(Fixture.OldKey)}" && request.StatusCode == 200);
        var calls = Fixture.App.Requests.Snapshot().Where(request => request.Method == "POST" && request.Target.Contains("fnId=eval-rot-steps", StringComparison.Ordinal)).ToList();
        calls.Count.ShouldBeGreaterThanOrEqualTo(3);
        calls.ShouldAllBe(call => call.Signed && call.ResponseSigned && call.StatusCode != 401);
    }

    // Either key verifies a call, and the answer is signed with the key that verified it, so the server that signed
    // with the old key can check it. A key that is neither is refused.
    [Fact(Timeout = 60_000)]
    public async Task During_a_key_rotation_the_app_accepts_both_keys_and_signs_with_the_one_that_verified()
    {
        foreach (var key in new[] { Fixture.OldKey, Fixture.NewKey })
        {
            var body = CallBody("rotation");
            using var response = await CallAsync(body, Signatures.Create(body, key, DateTimeOffset.UtcNow));

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var content = await response.Content.ReadAsByteArrayAsync(Cancellation);
            JsonSerializer.Deserialize<string>(content).ShouldBe("Hello, rotation!");
            response.Headers.TryGetValues("X-Inngest-Signature", out var signature).ShouldBeTrue();
            Signatures.IsValid(signature.ShouldHaveSingleItem(), content, key).ShouldBeTrue();
        }

        var forged = CallBody("intruder");
        using var refused = await CallAsync(forged, Signatures.Create(forged, CloudFixture.NewKey(), DateTimeOffset.UtcNow));
        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // A call shaped like the ones the server sends, for the step-less hello function.
    private byte[] CallBody(string name)
    {
        var runId = $"01JROT{Convert.ToHexString(RandomNumberGenerator.GetBytes(10))}";
        var @event = new
        {
            data = new { testId = TestId, text = name },
            id = runId,
            name = "eval/rot.hello",
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            user = new { },
        };

        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            ctx = new
            {
                attempt = 0,
                disable_immediate_execution = false,
                fn_id = Guid.NewGuid(),
                max_attempts = 1,
                run_id = runId,
                stack = new { current = 0, stack = Array.Empty<string>() },
                step_id = "step",
                use_api = false,
            },
            @event,
            events = new[] { @event },
            steps = new { },
            use_api = false,
            version = 2,
        });
    }

    private async Task<HttpResponseMessage> CallAsync(byte[] body, string signature)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Fixture.App.LocalUrl}?fnId=eval-{RotationFunctions.HelloId}&stepId=step")
        {
            Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } },
        };
        request.Headers.Add("X-Inngest-Signature", signature);
        return await Http.SendAsync(request, Cancellation);
    }
}
