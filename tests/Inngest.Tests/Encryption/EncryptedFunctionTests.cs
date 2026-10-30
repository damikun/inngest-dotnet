using System.Net;
using System.Text.Json;
using Inngest.Encryption;
using Inngest.Execution;
using Inngest.Functions;
using Inngest.Tests.Client;
using Inngest.Tests.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Inngest.Tests.Encryption;

// Real calls through the execution engine, with the middleware added the way an app adds it: the event that comes in,
// the output of steps and of the function that go out, the results that are replayed, and the events a step sends.
public sealed class EncryptedFunctionTests
{
    private static readonly LibSodiumEncryptionService Service = new([JsVectors.Key]);

    private readonly FakeHttpHandler _handler = new();

    [Fact]
    public async Task A_function_reads_the_event_data_that_the_typescript_package_encrypted()
    {
        await using var services = Build();
        JsonElement data = default;
        JsonElement raw = default;
        var function = TestFunction.Define(context =>
        {
            data = context.Event.Data.Clone();
            raw = context.RawEvent;
            return Task.FromResult<object?>(null);
        });

        var result = await CallAsync(services, function, Call.New().Event(JsVectors.EncryptedEvent));

        result.StatusCode.ShouldBe(200);
        data.GetProperty("public_field").GetString().ShouldBe("visible");
        data.GetProperty("encrypted").GetProperty("secret").GetString().ShouldBe("data");
        data.GetProperty("encrypted").GetProperty("list").GetArrayLength().ShouldBe(3);
        data.GetProperty("secret_data").GetProperty("other").GetString().ShouldBe("field");
        raw.GetProperty("data").GetProperty("encrypted").GetProperty("__ENCRYPTED__").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Typed_event_data_gets_the_decrypted_field()
    {
        await using var services = Build();
        Thing? seen = null;
        var function = TestFunction.Define<Thing>(context =>
        {
            seen = context.Event.Data;
            return Task.FromResult<object?>(null);
        });

        await CallAsync(services, function, Call.New().Event(JsVectors.EncryptedEvent));

        seen.ShouldNotBeNull().Count.ShouldBe(3);
        seen.Encrypted.Secret.ShouldBe("data");
        seen.Encrypted.List.ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task Every_event_of_a_batch_is_decrypted()
    {
        await using var services = Build();
        IReadOnlyList<InngestEvent<Thing>>? seen = null;
        var function = TestFunction.Define<Thing>(context =>
        {
            seen = context.Events;
            return Task.FromResult<object?>(null);
        });

        await CallAsync(services, function, Call.New().Events(JsVectors.EncryptedEvent, JsVectors.EncryptedCustomFieldEvent, JsVectors.EncryptedAesEvent));

        seen.ShouldNotBeNull().Select(@event => @event.Data.Encrypted.Secret).ShouldBe(["data", "data", "data"]);
    }

    [Fact]
    public async Task A_step_output_is_encrypted_on_its_way_to_inngest_and_decrypted_when_it_is_replayed()
    {
        await using var services = Build();
        string? seen = null;
        var function = TestFunction.Define(async context =>
        {
            seen = await context.Step.RunAsync("token", () => "s3cret");
            return new Greeting(seen, new Nested(42));
        });

        var first = await CallAsync(services, function, Call.New());
        var stored = first.Op().GetProperty("data").GetRawText();
        var second = await CallAsync(services, function, Call.New().Data("token", stored));

        first.OpCodes.ShouldBe(["StepRun"]);
        stored.ShouldStartWith("{\"__ENCRYPTED__\":true,\"__STRATEGY__\":\"inngest/libsodium\",\"data\":\"");
        EncryptionHarness.Decrypt(Service, stored).ShouldBe("\"s3cret\"");
        seen.ShouldBe("s3cret");
        second.StatusCode.ShouldBe(200);
        EncryptionHarness.Decrypt(Service, second.Body).ShouldBe("""{"hello":"s3cret","nested":{"num":42}}""");
    }

    [Fact]
    public async Task A_result_that_the_typescript_package_encrypted_is_replayed_decrypted()
    {
        await using var services = Build();
        Greeting? seen = null;
        var function = TestFunction.Define(async context =>
        {
            seen = await context.Step.RunAsync<Greeting>("a", NotRun);
            return "done";
        });

        var result = await CallAsync(services, function, Call.New().Data("a", JsVectors.LibSodium[0].Envelope));

        result.StatusCode.ShouldBe(200);
        seen.ShouldBe(new Greeting("world", new Nested(42)));
    }

    [Fact]
    public async Task A_result_written_with_a_key_that_was_replaced_is_replayed_through_the_fallback_key()
    {
        await using var services = Build(options =>
        {
            options.Key = JsVectors.SecondKey;
            options.FallbackDecryptionKeys = [JsVectors.Key];
        });
        Greeting? seen = null;
        var function = TestFunction.Define(async context =>
        {
            seen = await context.Step.RunAsync<Greeting>("a", NotRun);
            return await context.Step.RunAsync("b", () => "after");
        });

        var first = await CallAsync(services, function, Call.New().Data("a", JsVectors.LibSodium[0].Envelope));
        var written = first.Op().GetProperty("data").GetRawText();

        seen.ShouldBe(new Greeting("world", new Nested(42)));
        EncryptionHarness.Decrypt(new LibSodiumEncryptionService([JsVectors.SecondKey]), written).ShouldBe("\"after\"");
    }

    [Fact]
    public async Task The_event_a_wait_matched_has_its_data_decrypted()
    {
        await using var services = Build();
        InngestEvent<Thing>? matched = null;
        var function = TestFunction.Define(async context =>
        {
            matched = await context.Step.WaitForEventAsync<Thing>("approval", new WaitForEventOptions("app/thing.happened", TimeSpan.FromHours(1)));
            return matched?.Name;
        });

        var result = await CallAsync(services, function, Call.New().Memo("approval", JsVectors.EncryptedEvent));

        result.StatusCode.ShouldBe(200);
        matched.ShouldNotBeNull().Data.Encrypted.Secret.ShouldBe("data");
        EncryptionHarness.Decrypt(Service, result.Body).ShouldBe("\"app/thing.happened\"");
    }

    [Fact]
    public async Task The_output_of_a_function_that_was_invoked_is_decrypted_for_the_caller()
    {
        await using var services = Build();
        string? seen = null;
        var function = TestFunction.Define(async context =>
        {
            seen = await context.Step.InvokeAsync<string>("child", new InvokeOptions("child-fn"));
            return "done";
        });

        await CallAsync(services, function, Call.New().Data("child", JsVectors.LibSodium[1].Envelope));

        seen.ShouldBe("s3cret");
    }

    [Fact]
    public async Task The_output_of_a_function_is_encrypted_unless_there_is_none()
    {
        await using var services = Build();

        var output = await CallAsync(services, TestFunction.Define(_ => Task.FromResult<object?>(new Nested(7))), Call.New());
        var none = await CallAsync(services, TestFunction.Define(_ => Task.FromResult<object?>(null)), Call.New());

        EncryptionHarness.Decrypt(Service, output.Body).ShouldBe("""{"num":7}""");
        none.Body.ShouldBe("null");
    }

    [Fact]
    public async Task Events_a_step_sends_are_encrypted_on_the_wire_and_so_is_the_result_of_the_step()
    {
        _handler.Respond(HttpStatusCode.OK, ClientFixture.Accepted);
        await using var services = Build();
        var function = TestFunction.Define(async context =>
            (await context.Step.SendEventAsync("notify", new InngestEvent("app/notified", new { PublicField = 1, Encrypted = new { Secret = "x" } }))).Ids);

        var result = await CallAsync(services, function, Call.New());

        result.OpCodes.ShouldBe(["StepRun"]);
        using var sent = JsonDocument.Parse(_handler.Single.BodyText);
        var data = sent.RootElement[0].GetProperty("data");
        data.GetProperty("publicField").GetInt32().ShouldBe(1);
        data.GetProperty("encrypted").GetProperty("__STRATEGY__").GetString().ShouldBe("inngest/libsodium");
        EncryptionHarness.Decrypt(Service, data.GetProperty("encrypted").GetRawText()).ShouldBe("""{"secret":"x"}""");
        EncryptionHarness.Decrypt(Service, result.Op().GetProperty("data").GetRawText()).ShouldBe("""{"ids":["01HQ0000000000000000000001"]}""");
    }

    [Fact]
    public async Task Events_sent_outside_a_run_are_encrypted_too()
    {
        _handler.Respond(HttpStatusCode.OK, ClientFixture.Accepted);
        await using var services = Build();

        await services.GetRequiredService<IInngestClient>().SendAsync(new InngestEvent("app/notified", new { Encrypted = new { Secret = "x" } }));

        using var sent = JsonDocument.Parse(_handler.Single.BodyText);
        EncryptionHarness.Decrypt(Service, sent.RootElement[0].GetProperty("data").GetProperty("encrypted").GetRawText()).ShouldBe("""{"secret":"x"}""");
    }

    [Fact]
    public async Task Decrypt_only_writes_everything_as_it_is_and_still_reads_what_is_encrypted()
    {
        _handler.Respond(HttpStatusCode.OK, ClientFixture.Accepted);
        await using var services = Build(options => options.DecryptOnly = true);
        Greeting? seen = null;
        var function = TestFunction.Define(async context =>
        {
            seen = await context.Step.RunAsync<Greeting>("a", NotRun);
            await context.Step.SendEventAsync("notify", new InngestEvent("app/notified", new { Encrypted = "plain" }));
            return "done";
        });

        var result = await CallAsync(services, function, Call.New().Data("a", JsVectors.LibSodium[0].Envelope));
        var output = await CallAsync(services, TestFunction.Define(_ => Task.FromResult<object?>("plain-function-output")), Call.New());

        seen.ShouldBe(new Greeting("world", new Nested(42)));
        result.Op().GetProperty("data").GetRawText().ShouldBe("""{"ids":["01HQ0000000000000000000001"]}""");
        _handler.Single.BodyText.ShouldContain("\"encrypted\":\"plain\"");
        output.Body.ShouldBe("\"plain-function-output\"");
    }

    [Fact]
    public async Task A_result_that_no_key_decrypts_fails_the_function_with_an_error_that_does_not_reveal_the_key()
    {
        await using var services = Build(options => options.Key = "another-key-material");
        var function = TestFunction.Define(async context => await context.Step.RunAsync<Greeting>("a", NotRun));

        var result = await CallAsync(services, function, Call.New().Data("a", JsVectors.LibSodium[0].Envelope));

        result.StatusCode.ShouldBe(500);
        result.Json.GetProperty("message").GetString().ShouldBe("The encrypted value could not be decrypted with any of the configured keys.");
        result.Body.ShouldNotContain("another-key-material");
    }

    [Fact]
    public async Task An_event_that_no_key_decrypts_fails_the_call_so_that_inngest_retries_it()
    {
        await using var services = Build(options => options.Key = "another-key-material");
        var called = false;
        var function = TestFunction.Define(_ =>
        {
            called = true;
            return Task.FromResult<object?>(null);
        });

        var result = await CallAsync(services, function, Call.New().Event(JsVectors.EncryptedEvent));

        called.ShouldBeFalse();
        result.StatusCode.ShouldBe(500);
        result.NoRetry.ShouldNotBe(true);
        result.Json.GetProperty("message").GetString().ShouldBe("The encrypted value could not be decrypted with any of the configured keys.");
        result.Body.ShouldNotContain("another-key-material");
    }

    private ServiceProvider Build(Action<EncryptionOptions>? configure = null) =>
        ClientServices.Build(_handler, new FakeTimeProvider(), inngest => inngest.AddEncryption(options =>
        {
            options.Key = JsVectors.Key;
            configure?.Invoke(options);
        }));

    private static async Task<CallResult> CallAsync(IServiceProvider services, FunctionDefinition function, Call call)
    {
        using var host = new EngineHost();
        return await EngineHost.ResultOf(host.Engine.ExecuteAsync(
            new ExecutionRequest { Function = function, Body = call.ToBytes(), StepId = "step", Services = services },
            TestContext.Current.CancellationToken));
    }

    // The handler of a step that is replayed, which must not run.
    private static Greeting NotRun() => throw new InvalidOperationException("The step was memoized.");

    private sealed record Greeting(string Hello, Nested Nested);

    private sealed record Nested(int Num);

    private sealed record Sealed(string Secret, int[] List);

    private sealed record Thing(int Count, Sealed Encrypted);
}
