using System.Buffers;
using System.Net;
using System.Text;
using System.Text.Json;
using Inngest.Encryption;
using Inngest.Tests.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Tests.AspNetCore;

// The output proxy in memory, with Inngest's output URL faked: what it asks Inngest, and what it hands the client.
public sealed class DurableEndpointProxyTests : IAsyncDisposable
{
    private const string EncryptionKey = "0123456789abcdef0123456789abcdef";

    private readonly FakeHttpHandler _inngest = new();
    private WebApplication? _app;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_endpoints_response_is_handed_on_with_CORS_headers()
    {
        _inngest.Respond(HttpStatusCode.Accepted, """{"shipped":true}""");
        var client = await StartAsync();

        using var response = await client.GetAsync("/inngest/output?runId=01JRUN&token=eyJ.run%2Btoken", CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldBe("""{"shipped":true}""");
        response.Headers.GetValues("Access-Control-Allow-Origin").ShouldBe(["*"]);
        response.Headers.GetValues("Access-Control-Allow-Methods").ShouldBe(["GET, OPTIONS"]);
        var request = _inngest.Single;
        request.Method.ShouldBe(HttpMethod.Get);
        request.Uri.AbsoluteUri.ShouldBe("http://localhost:8288/v1/http/runs/01JRUN/output?token=eyJ.run%2Btoken");
        request.Header("Authorization").ShouldBeNull();
    }

    [Fact]
    public async Task A_preflight_is_answered_without_asking_Inngest()
    {
        var client = await StartAsync();

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/inngest/output"), CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        response.Headers.GetValues("Access-Control-Allow-Headers").ShouldBe(["Content-Type"]);
        response.Headers.GetValues("Access-Control-Max-Age").ShouldBe(["86400"]);
        _inngest.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/inngest/output")]
    [InlineData("/inngest/output?runId=01JRUN")]
    [InlineData("/inngest/output?token=eyJ")]
    public async Task A_request_without_a_run_id_and_token_is_refused(string path)
    {
        var client = await StartAsync();

        using var response = await client.GetAsync(path, CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldBe("""{"error":"Missing runId or token query parameter"}""");
        response.Headers.GetValues("Access-Control-Allow-Origin").ShouldBe(["*"]);
        _inngest.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_refusal_of_Inngest_is_handed_on()
    {
        _inngest.Respond(HttpStatusCode.Unauthorized, """{"error":"invalid token"}""");
        var client = await StartAsync();

        using var response = await client.GetAsync("/inngest/output?runId=01JRUN&token=eyJ", CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldBe("""{"error":"invalid token"}""");
    }

    [Fact]
    public async Task Inngest_out_of_reach_is_a_bad_gateway()
    {
        _inngest.Fail(new HttpRequestException("Connection refused"));
        var client = await StartAsync();

        using var response = await client.GetAsync("/inngest/output?runId=01JRUN&token=eyJ", CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldBe("""{"error":"Failed to fetch run output"}""");
    }

    [Fact]
    public async Task An_encrypted_output_is_decrypted_and_the_recorded_response_replayed()
    {
        var recorded = """{"data":{"status":202,"headers":{"content-type":"text/plain; charset=utf-8","X-Packed-By":"endpoint"},"body":"secret note"}}""";
        _inngest.Respond(HttpStatusCode.OK, Seal(recorded));
        var client = await StartAsync(encrypted: true);

        using var response = await client.GetAsync("/inngest/output?runId=01JRUN&token=eyJ", CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        response.Content.Headers.ContentType!.ToString().ShouldBe("text/plain; charset=utf-8");
        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldBe("secret note");
        response.Headers.GetValues("Access-Control-Allow-Origin").ShouldBe(["*"]);
    }

    [Fact]
    public async Task An_encrypted_output_of_another_shape_is_answered_as_JSON()
    {
        _inngest.Respond(HttpStatusCode.OK, Seal("""{"total":42}"""));
        var client = await StartAsync(encrypted: true);

        using var response = await client.GetAsync("/inngest/output?runId=01JRUN&token=eyJ", CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldBe("""{"total":42}""");
    }

    [Fact]
    public async Task An_output_that_is_not_encrypted_passes_through_an_app_that_encrypts()
    {
        _inngest.Respond(HttpStatusCode.OK, "plain text", "text/plain");
        var client = await StartAsync(encrypted: true);

        using var response = await client.GetAsync("/inngest/output?runId=01JRUN&token=eyJ", CancellationToken);

        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldBe("plain text");
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/plain");
    }

    [Fact]
    public void Mapping_the_proxy_requires_AddInngest()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        using var app = builder.Build();

        Should.Throw<InvalidOperationException>(() => app.MapInngestDurableEndpointProxy("/inngest/output"))
            .Message.ShouldContain("AddInngest");
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    private static string Seal(string json)
    {
        var encryptor = new PayloadEncryptor(new EncryptionOptions { Key = EncryptionKey }, JsonSerializerOptions.Default);
        using var document = JsonDocument.Parse(json);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            encryptor.EncryptOutput(document.RootElement, writer).ShouldBeTrue();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private async Task<HttpClient> StartAsync(bool encrypted = false)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        var inngest = builder.Services.AddInngest(options =>
        {
            options.AppId = "shop";
            options.IsDev = true;
        });
        if (encrypted)
        {
            inngest.AddEncryption(options => options.Key = EncryptionKey);
        }

        builder.Services.AddHttpClient(InngestServiceCollectionExtensions.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _inngest);

        var app = _app = builder.Build();
        app.MapInngestDurableEndpointProxy("/inngest/output");
        await app.StartAsync(CancellationToken);
        return app.GetTestClient();
    }
}
