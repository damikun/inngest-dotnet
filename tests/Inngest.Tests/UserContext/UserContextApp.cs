using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Inngest.Execution;
using Inngest.Functions;
using Inngest.Registration;
using Inngest.Tests.Client;
using Inngest.Tests.Execution;
using Inngest.UserContext;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Inngest.Tests.UserContext;

// An app as a developer sets it up with AddUserContext: fake HTTP to Inngest, fake time and logging, in-memory Data
// Protection unless the test hands in a provider, which is how two apps share a key ring.
internal sealed class UserContextApp : IAsyncDisposable
{
    public const string EventName = "test/event";

    public static readonly DateTimeOffset Now = EngineHost.Now;

    private readonly ServiceProvider _services;

    public UserContextApp(
        IDataProtectionProvider? protection = null,
        Action<UserContextOptions>? configure = null,
        Action<IInngestBuilder>? configureInngest = null,
        Action<IServiceCollection>? configureServices = null,
        IUserContextSource? source = null)
    {
        _services = ClientServices.Build(
            Http,
            Time,
            inngest =>
            {
                inngest.Services.AddSingleton(protection ?? new EphemeralDataProtectionProvider());
                inngest.AddUserContext(configure);
                if (source is not null)
                {
                    inngest.Services.AddSingleton(source);
                }

                configureInngest?.Invoke(inngest);
            },
            services =>
            {
                services.AddFakeLogging();
                configureServices?.Invoke(services);
            });
        Logs = _services.GetFakeLogCollector();
        Http.RespondAlways(System.Net.HttpStatusCode.OK, ClientFixture.Accepted);
    }

    public FakeHttpHandler Http { get; } = new();

    public FakeTimeProvider Time { get; } = new(Now);

    public FakeLogCollector Logs { get; }

    public IServiceProvider Services => _services;

    public IInngestClient Client => _services.GetRequiredService<IInngestClient>();

    public UserContextCodec Codec => _services.GetRequiredService<UserContextCodec>();

    public IUserContextProtector Protector => _services.GetRequiredService<IUserContextProtector>();

    // A principal as ASP.NET Core authentication builds it for a user of the sample shop.
    public static ClaimsPrincipal Ada(params Claim[] more) => Principal("Cookies", [new("sub", "u-42"), new("name", "Ada Lovelace"), new("email", "ada@example.com"), new("role", "admin"), new("role", "support"), new("password", "hunter2"), .. more]);

    public static ClaimsPrincipal Principal(string? authenticationType, params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType, "name", "role"));

    // The user object of an event as the sender in this app would have sent it: the reserved key holding the envelope of
    // the principal's allowed claims, taken now.
    public string Seal(ClaimsPrincipal principal, string eventName = EventName)
    {
        var snapshot = Codec.Capture(principal) ?? throw new InvalidOperationException("Nothing to carry.");
        return Envelope(Codec.Protect(snapshot, eventName)!);
    }

    // The user object with the protected text replaced, for tests that need to change what the envelope holds.
    public static string Envelope(string protectedText, int version = UserContextCodec.Version) =>
        $$$"""{"{{{UserContextCodec.Key}}}":{"v":{{{version}}},"t":"{{{protectedText}}}"}}""";

    public static string EventJson(string user, string name = EventName) =>
        $$"""{"data":{"value":1},"id":"01JTEST","name":"{{name}}","ts":1793268000000,"user":{{user}}}""";

    // An id Inngest could have given a run created at this time.
    public static string RunIdAt(DateTimeOffset time)
    {
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        var milliseconds = time.ToUnixTimeMilliseconds();
        var chars = new char[26];
        for (var i = 9; i >= 0; i--)
        {
            chars[i] = alphabet[(int)(milliseconds & 31)];
            milliseconds >>= 5;
        }

        for (var i = 10; i < chars.Length; i++)
        {
            chars[i] = alphabet[(i * 7) % 32];
        }

        return new string(chars);
    }

    // Runs one call of the function in a scope of its own, as the serve endpoint does. The run is created at runStart,
    // or has the made-up id the other engine tests use.
    public async Task<CallResult> CallAsync(FunctionDefinition function, Call call, DateTimeOffset? runStart = null)
    {
        var body = Encoding.UTF8.GetString(call.ToBytes());
        if (runStart is { } start)
        {
            body = body.Replace("\"run_id\":\"01JRUN\"", $"\"run_id\":\"{RunIdAt(start)}\"", StringComparison.Ordinal);
        }

        await using var scope = _services.CreateAsyncScope();
        return await EngineHost.ResultOf(_services.GetRequiredService<IExecutionEngine>().ExecuteAsync(
            new ExecutionRequest
            {
                Function = function,
                Body = Encoding.UTF8.GetBytes(body),
                StepId = "step",
                Services = scope.ServiceProvider,
            },
            TestContext.Current.CancellationToken));
    }

    // A function whose output says what it saw as the user.
    public static FunctionDefinition Observe(Action<FunctionContext>? before = null, FunctionOptions? options = null) =>
        new(options ?? Options(), _ => new DelegateFunction<JsonElement>((context, _) =>
        {
            before?.Invoke(context);
            return Task.FromResult<object?>(SeenBy(context));
        }));

    public static FunctionOptions Options(string id = "fn", params FunctionTrigger[] triggers) =>
        new() { Id = id, Triggers = triggers.Length > 0 ? triggers : [Trigger.Event(EventName)] };

    public static Seen SeenBy(FunctionContext context)
    {
        var user = context.Services.GetRequiredService<ICurrentUser>();
        var principal = context.Services.GetRequiredService<ClaimsPrincipal>();
        return new Seen(
            user.IsAuthenticated,
            user.Principal.Identity?.IsAuthenticated == true,
            [.. user.Principal.Claims.Select(claim => $"{claim.Type}={claim.Value}")],
            user.Principal.Identity?.Name,
            user.Principal.IsInRole("admin"),
            user.IssuedAt?.ToUnixTimeSeconds(),
            user.Principal.Identity?.AuthenticationType,
            principal.Claims.Count());
    }

    // A function the app registered, as the registry has it.
    public FunctionDefinition Function(string id) =>
        _services.GetRequiredService<IFunctionRegistry>().TryGet($"test-app-{id}", out var function)
            ? function
            : throw new InvalidOperationException($"No function {id} is registered.");

    public static Seen SeenIn(CallResult result) =>
        JsonSerializer.Deserialize<Seen>(result.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    // The events of the nth request this app sent to Inngest, as JSON.
    public JsonElement[] SentEvents(int request = 0) =>
        [.. JsonDocument.Parse(Http.Requests[request].BodyText).RootElement.EnumerateArray().Select(@event => @event.Clone())];

    // What the protected text in the user of a sent event holds.
    public JsonElement PayloadOf(JsonElement user)
    {
        var text = user.GetProperty(UserContextCodec.Key).GetProperty("t").GetString()!;
        Protector.TryUnprotect(text, out var plaintext).ShouldBeTrue();
        return JsonDocument.Parse(plaintext!).RootElement.Clone();
    }

    public ValueTask DisposeAsync() => _services.DisposeAsync();

    // What a function observed. Principal is the number of claims of the ClaimsPrincipal service.
    public sealed record Seen(
        bool IsAuthenticated,
        bool IdentityAuthenticated,
        string[] Claims,
        string? Name,
        bool IsAdmin,
        long? IssuedAt,
        string? AuthenticationType,
        int Principal);
}
