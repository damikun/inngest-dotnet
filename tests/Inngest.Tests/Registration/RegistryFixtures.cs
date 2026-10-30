using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Inngest.Configuration;
using Inngest.Functions;
using Inngest.Internal;
using Inngest.Registration;

namespace Inngest.Tests.Registration;

internal static class RegistryFixtures
{
    public const string AppId = "my-app";
    public const string DefaultServeUrl = "https://example.com/api/inngest";

    // The smallest valid function. Tests vary it with a with-expression.
    public static FunctionOptions Orders { get; } = new()
    {
        Id = "orders",
        Triggers = [Trigger.Event("order/created")],
    };

    public static InngestSettings Settings(string appId = AppId, string? appVersion = null) => new()
    {
        AppId = appId,
        AppVersion = appVersion,
        Mode = InngestMode.Dev,
        ApiBaseUrl = new Uri("http://localhost:8288"),
        EventApiBaseUrl = new Uri("http://localhost:8288"),
        SerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web),
    };

    // The registry never runs a function, so resolving one is a mistake in the test.
    public static FunctionDefinition Define(FunctionOptions options, string? failureHandlerFor = null) =>
        new(options, static _ => throw new NotSupportedException("The registry does not run functions."))
        {
            FailureHandlerFor = failureHandlerFor,
        };

    // A function with a failure handler, as AddFunction registers it: the function and its "-failure" companion.
    public static FunctionDefinition[] WithFailureHandler(FunctionOptions options) =>
    [
        Define(options),
        Define(FailureFunction.CreateOptions(options), failureHandlerFor: options.Id),
    ];

    public static FunctionRegistry Registry(params FunctionOptions[] options) =>
        new(options.Select(option => Define(option)), Settings());

    public static string Write(
        IFunctionRegistry registry,
        string serveUrl = DefaultServeUrl,
        string framework = "aspnetcore",
        string? deployType = null)
    {
        using var writer = new PooledBufferWriter();
        registry.WriteRegistration(writer, new Uri(serveUrl), framework, deployType);
        return Encoding.UTF8.GetString(writer.WrittenSpan);
    }

    public static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    // Removes the whitespace of an expected body that is written out indented to be readable.
    public static string Compact(string json)
    {
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            document.RootElement.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // The JSON object a sync reports for the first function of a registry holding just these options.
    public static string Function(FunctionOptions options) => FunctionAt(Write(Registry(options)), 0);

    public static JsonElement FunctionElement(string body, int index) => Parse(body).GetProperty("functions")[index];

    public static string FunctionAt(string body, int index) => FunctionElement(body, index).GetRawText();

    public static string[] FunctionIds(string body) =>
        [.. Parse(body).GetProperty("functions").EnumerateArray().Select(function => function.GetProperty("id").GetString()!)];

    // The JSON of one member of an object, or null when the object has no such member.
    public static string? RawProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetRawText() : null;

    // The JSON a sync reports for one option of a function, or null when the option is left out.
    public static string? Property(FunctionOptions options, string name) =>
        RawProperty(FunctionElement(Write(Registry(options)), 0), name);

    // The runtime of the first function in a sync body.
    public static string Runtime(string body) =>
        FunctionElement(body, 0).GetProperty("steps").GetProperty("step").GetProperty("runtime").GetRawText();
}
