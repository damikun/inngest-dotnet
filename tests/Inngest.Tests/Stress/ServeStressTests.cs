using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Inngest.Signing;
using Inngest.Tests.Execution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Inngest.Tests.Stress;

// The whole serve path in cloud mode under concurrent requests: signed calls, signed introspection and forged
// calls, interleaved. Every answer must be the one for its own request, and signed with the key that verified it.
public sealed class ServeStressTests
{
    private const string Key = "signkey-test-5f0c2a9e7b3d1c8a6e4f2b0d9c7a5e3f1b8d6c4a2e0f9b7d5c3a1e8f6b4d2c0a";
    private const int Requests = 2500;

    [Fact]
    public async Task Concurrent_signed_and_forged_requests_are_each_answered_on_their_own()
    {
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var signer = new RequestSigner(TimeProvider.System);
        var key = SigningKey.Create(Key)!;

        var answers = await Task.WhenAll(Enumerable.Range(0, Requests).Select(index => Task.Run(() => SendAsync(client, index))));

        for (var index = 0; index < Requests; index++)
        {
            var (status, headers, body) = answers[index];
            var run = index;
            if (index % 5 == 4)
            {
                status.ShouldBe(401);
                body.ShouldBe("""{"message":"Unauthorized"}""");
                headers.ShouldNotContainKey("X-Inngest-SDK");
                headers.ShouldNotContainKey("X-Inngest-Signature");
                continue;
            }

            signer.Verify(headers["X-Inngest-Signature"], Encoding.UTF8.GetBytes(body), key, null).IsValid.ShouldBeTrue();
            switch (index % 5)
            {
                case 0:
                    status.ShouldBe(206);
                    body.ShouldContain($"\"displayName\":\"a-{run}\"");
                    body.ShouldContain($"\"displayName\":\"b-{run}\"");
                    body.ShouldNotContain("\"StepRun\"");
                    break;
                case 1:
                    status.ShouldBe(206);
                    body.ShouldStartWith($"[{{\"id\":\"{Call.Hash($"b-{run}")}\",\"op\":\"StepRun\"");
                    body.ShouldContain($"\"data\":{run * 3},");
                    break;
                case 2:
                    status.ShouldBe(200);
                    body.ShouldBe((run * 5).ToString(CultureInfo.InvariantCulture));
                    break;
                default:
                    status.ShouldBe(200);
                    body.ShouldContain("\"authentication_succeeded\":true");
                    body.ShouldContain("\"app_id\":\"shop\"");
                    break;
            }
        }
    }

    private static async Task<(int Status, Dictionary<string, string> Headers, string Body)> SendAsync(HttpClient client, int index)
    {
        var call = Call.New().Event($$$"""{"data":{"run":{{{index}}}},"id":"01JSERVE{{{index:D8}}}","name":"test/event","ts":1793268000000,"user":{}}""");
        if (index % 5 is 1 or 2)
        {
            call.Data($"a-{index}", (index * 2).ToString(CultureInfo.InvariantCulture));
        }

        if (index % 5 == 2)
        {
            call.Data($"b-{index}", (index * 3).ToString(CultureInfo.InvariantCulture));
        }

        var body = call.ToBytes();
        using var request = index % 5 == 3
            ? new HttpRequestMessage(HttpMethod.Get, "/api/inngest")
            : new HttpRequestMessage(HttpMethod.Post, "/api/inngest?fnId=shop-checkout&stepId=step") { Content = new ByteArrayContent(body) };

        // GET bodies are not signed: Inngest signs an empty body for them.
        var signed = request.Method == HttpMethod.Get ? [] : body;
        var signature = Sign(signed);
        if (index % 5 == 4)
        {
            // Signed for one body and sent with another, as a tampered or replayed request would be.
            signature = Sign(Encoding.UTF8.GetBytes("{}"));
        }

        request.Headers.TryAddWithoutValidation("X-Inngest-Signature", signature);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var headers = response.Headers.ToDictionary(static header => header.Key, static header => string.Join(", ", header.Value), StringComparer.OrdinalIgnoreCase);
        return ((int)response.StatusCode, headers, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static string Sign(byte[] body)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        byte[] signed = [.. body, .. Encoding.UTF8.GetBytes(timestamp)];
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(Key["signkey-test-".Length..]), signed);
        return $"t={timestamp}&s={Convert.ToHexStringLower(mac)}";
    }

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.Sources.Clear();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services
            .AddInngest(static options =>
            {
                options.AppId = "shop";
                options.IsDev = false;
                options.SigningKey = Key;
                options.BaseUrl = new Uri("http://127.0.0.1:9/");
            })
            .AddFunction(new FunctionOptions { Id = "checkout", Triggers = [Trigger.Event("test/event")] }, static async (context, _) =>
            {
                var run = context.Event.Data.GetProperty("run").GetInt32();
                var a = context.Step.RunAsync($"a-{run}", () => run * 2);
                var b = context.Step.RunAsync($"b-{run}", () => run * 3);
                return await a + await b;
            });

        var app = builder.Build();
        app.MapInngest("/api/inngest");
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }
}
