using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Inngest.Soak.Workload;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Inngest.Soak;

// The whole HTTP path: a real Kestrel socket, MapInngest, signature verification and response signing in cloud
// mode, with an HttpClient that signs each call the way Inngest does. Client and server share the process, so the
// memory figures include the client's.
internal static class HttpSoak
{
    public const string SigningKey = "signkey-test-8a3f1c2e9b7d4a6f0e5c3b1a9d7f2e4c6b8a0d2f4e6c8a1b3d5f7e9c0a2b4d6f";

    private static readonly MediaTypeHeaderValue Json = new("application/json");

    public static async Task<int> RunAsync(CommandLine options, CancellationToken cancellationToken)
    {
        var concurrency = options.GetInt("concurrency", 64);
        await using var app = await StartAppAsync(cancellationToken).ConfigureAwait(false);
        var endpoint = new Uri(new Uri(app.Urls.Single()), "/api/inngest");
        using var client = CreateClient(concurrency);

        // One URL per step id, built up front so that requests share them.
        var urls = new Dictionary<string, Uri>(StringComparer.Ordinal);
        foreach (var call in CallShapes.ForRun(0))
        {
            urls[call.StepId] = new Uri($"{endpoint}?fnId={SoakServices.CheckoutCompositeId}&stepId={call.StepId}");
        }

        return await SoakRunner.RunAsync(
            "http",
            options,
            defaultInvocations: 400_000,
            async (call, checkBody, token) =>
            {
                using var request = SignedRequest(urls[call.StepId], call.Body);
                using var response = await client.SendAsync(request, token).ConfigureAwait(false);
                var body = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                return response.Headers.Contains("X-Inngest-Signature")
                    ? SoakRunner.Check(call, (int)response.StatusCode, body, checkBody)
                    : $"the {(int)response.StatusCode} answer is not signed";
            },
            cancellationToken).ConfigureAwait(false);
    }

    // The sample app's setup in cloud mode, on a loopback port Kestrel picks.
    public static async Task<WebApplication> StartAppAsync(CancellationToken cancellationToken, Action<IInngestBuilder>? configure = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(static kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        var inngest = builder.Services.AddSoakApp(static options =>
        {
            options.IsDev = false;
            options.SigningKey = SigningKey;
            options.EventKey = "soak-event-key";
            options.BaseUrl = new Uri("http://127.0.0.1:9/");
        });
        configure?.Invoke(inngest);

        var app = builder.Build();
        app.MapInngest("/api/inngest");
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        return app;
    }

    public static HttpClient CreateClient(int connections) =>
        new(new SocketsHttpHandler { MaxConnectionsPerServer = connections, UseCookies = false, UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

    // X-Inngest-Signature as Inngest sends it: t=<unix seconds>&s=<hex HMAC-SHA256 of the body followed by t>, keyed
    // with the signing key minus its signkey-<env>- prefix.
    public static HttpRequestMessage SignedRequest(Uri url, byte[] body)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(SigningKey["signkey-test-".Length..]));
        hmac.AppendData(body);
        hmac.AppendData(Encoding.UTF8.GetBytes(timestamp));

        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = Json;
        request.Headers.TryAddWithoutValidation("X-Inngest-Signature", $"t={timestamp}&s={Convert.ToHexStringLower(hmac.GetHashAndReset())}");
        return request;
    }
}
