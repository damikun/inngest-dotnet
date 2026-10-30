using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.TestHost;

namespace Parity;

// Registers each case of the function options builder in a real app, once with its options in code and once as
// attributes, asks the app for its configuration with an in-band sync, as Inngest does, and compares that with the
// JSON the builder shows.
internal static class Program
{
    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static async Task<int> Main()
    {
        var failures = 0;
        foreach (var @case in Cases.All)
        {
            var expected = JsonNode.Parse(@case.ExpectedJson)!.AsObject();
            foreach (var (form, register) in new[] { ("code first", @case.CodeFirst), ("attributes", @case.Attributes) })
            {
                var actual = await SyncAsync(register, @case.ServeUrl);
                foreach (var property in new[] { "url", "sdk", "framework", "capabilities", "functions" })
                {
                    var want = expected[property]?.ToJsonString(Compact);
                    var got = actual[property]?.ToJsonString(Compact);
                    if (want != got)
                    {
                        failures++;
                        Console.Error.WriteLine($"{@case.Name}, {form}: \"{property}\" differs.\n  builder: {want}\n  SDK:     {got}");
                    }
                }
            }
        }

        Console.WriteLine(failures == 0
            ? $"Registration JSON of {Cases.All.Length} builder cases matches the SDK, with the options in code and as attributes."
            : $"{failures} differences between the builder and the SDK.");
        return failures == 0 ? 0 : 1;
    }

    private static async Task<JsonObject> SyncAsync(Action<WebApplicationBuilder> register, string serveUrl)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["INNGEST_DEV"] = "1" });
        builder.Logging.ClearProviders();
        register(builder);

        await using var app = builder.Build();
        app.MapInngest("/api/inngest");
        await app.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/inngest")
        {
            Content = new StringContent(new JsonObject { ["url"] = serveUrl }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Inngest-Sync-Kind", "in_band");
        using var response = await app.GetTestClient().SendAsync(request);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }
}

internal sealed record Case(
    string Name,
    string ServeUrl,
    Action<WebApplicationBuilder> CodeFirst,
    Action<WebApplicationBuilder> Attributes,
    string ExpectedJson);
