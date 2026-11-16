using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Inngest.Execution;
using Microsoft.Extensions.Logging;

namespace Inngest.Tests.Execution;

// Inngest's limits on a step's output, a run's state and the steps of a run, checked before Inngest refuses them.
public sealed class LimitTests : IDisposable
{
    private const int FourMebibytes = 4 * 1024 * 1024;

    private readonly EngineHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task A_step_output_over_4_MiB_fails_the_step_without_retries()
    {
        // With the quotes of the JSON string, one byte over.
        var function = TestFunction.Define(async context => await context.Step.RunAsync("export", () => new string('x', FourMebibytes - 1)));

        var result = await _host.CallAsync(function, Call.New());

        result.OpCodes.ShouldBe(["StepFailed"]);
        result.NoRetry.ShouldBe(true);
        var error = result.Op().GetProperty("error");
        error.GetProperty("name").GetString().ShouldBe(nameof(NonRetriableException));
        error.GetProperty("message").GetString().ShouldBe(
            "The output of step \"export\" is 4.01 MiB, more than the 4 MiB Inngest keeps for a step. Keep large data outside the run and return a reference to it.");
        result.Op().TryGetProperty("data", out _).ShouldBeFalse();
    }

    // Inngest measures the output as it stores it, in {"data":...}, which takes 9 bytes more.
    [Fact]
    public async Task A_step_output_of_4_MiB_with_its_data_object_is_kept()
    {
        var function = TestFunction.Define(async context => await context.Step.RunAsync("export", () => new string('x', FourMebibytes - 9 - 2)));

        var result = await _host.CallAsync(function, Call.New());

        result.OpCodes.ShouldBe(["StepRun"]);
    }

    [Fact]
    public async Task A_step_output_under_4_MiB_that_its_data_object_takes_over_fails_the_step_without_retries()
    {
        var function = TestFunction.Define(async context => await context.Step.RunAsync("export", () => new string('x', 4_194_300 - 2)));

        var result = await _host.CallAsync(function, Call.New());

        result.OpCodes.ShouldBe(["StepFailed"]);
        result.NoRetry.ShouldBe(true);
        var error = result.Op().GetProperty("error");
        error.GetProperty("name").GetString().ShouldBe(nameof(NonRetriableException));
        error.GetProperty("message").GetString().ShouldBe(
            "The output of step \"export\" is 4.01 MiB, more than the 4 MiB Inngest keeps for a step. Keep large data outside the run and return a reference to it.");
    }

    // UnsafeRelaxedJsonEscaping leaves <, > and & as they are, and Inngest stores each as \u003c and the like.
    [Theory]
    [InlineData("", "StepRun")]
    [InlineData("<", "StepFailed")]
    [InlineData(">", "StepFailed")]
    [InlineData("&", "StepFailed")]
    public async Task HTML_left_as_it_is_counts_as_Inngest_escapes_it(string html, string op)
    {
        var settings = EngineHost.Settings with
        {
            SerializerOptions = new JsonSerializerOptions(EngineHost.Settings.SerializerOptions) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping },
        };
        using var host = new EngineHost(settings: settings);
        var text = string.Concat(Enumerable.Repeat(html, 100));
        var function = TestFunction.Define(async context => await context.Step.RunAsync("export", () => new string('x', 4_194_000 - 2 - text.Length) + text));

        var result = await host.CallAsync(function, Call.New());

        result.OpCodes.ShouldBe([op]);
    }

    [Theory]
    [InlineData("<", 5)]
    [InlineData(">", 5)]
    [InlineData("&", 5)]
    [InlineData("\u2028", 3)]
    [InlineData("\u2029", 3)]
    [InlineData("\\u003c", 0)]
    [InlineData("\\u2028", 0)]
    [InlineData("'+`\u007f\u00e9\U0001F600 ", 0)]
    public void Inngest_measures_a_step_output_as_Go_writes_it_again(string text, int growth)
    {
        var data = Encoding.UTF8.GetBytes($"[\"{new string('x', 4_000_000)}\",\"{text}\"]");

        Invocation.StoredStepOutputBytes(data).ShouldBe(data.Length + 9 + growth);
    }

    // Go writes the output without the whitespace between its tokens.
    [Fact]
    public void Whitespace_between_tokens_does_not_count()
    {
        var data = Encoding.UTF8.GetBytes($"[\n  \"{new string(' ', FourMebibytes)}\" ,\t1\r\n]");

        Invocation.StoredStepOutputBytes(data).ShouldBe(data.Length + 9 - 7);
    }

    // Inngest holds a step's error, in {"error":...}, to the same 4 MiB and refuses the whole answer for a larger one.
    [Fact]
    public async Task A_step_error_over_4_MiB_has_its_message_cut_short()
    {
        const int Length = 5 * 1024 * 1024;
        var function = TestFunction.Define(async context =>
            await context.Step.RunAsync("export", () => Throw(new InvalidOperationException(new string('x', Length)))));

        var result = await _host.CallAsync(function, Call.New());

        result.OpCodes.ShouldBe(["StepError"]);
        result.NoRetry.ShouldBe(false);
        var error = result.Op().GetProperty("error");
        error.GetProperty("name").GetString().ShouldBe(nameof(InvalidOperationException));
        error.GetProperty("message").GetString().ShouldBe($"{new string('x', 16_384)}... ({Length - 16_384} more characters left out)");
        error.GetProperty("stack").GetString()!.ShouldContain(nameof(LimitTests));
        _host.Logs.GetSnapshot().ShouldContain(record => record.Id.Id == 108 && record.Exception!.Message.Length == Length);
    }

    [Fact]
    public async Task A_step_error_of_4_MiB_with_its_error_object_is_kept_whole()
    {
        var length = FourMebibytes - ErrorWithoutMessage.Length - 10;
        var function = TestFunction.Define(async context =>
            await context.Step.RunAsync("export", () => Throw(new ExportException(new string('x', length)))));

        var result = await _host.CallAsync(function, Call.New());

        result.Op().GetProperty("error").GetProperty("message").GetString()!.Length.ShouldBe(length);
    }

    [Fact]
    public async Task A_step_error_one_byte_over_4_MiB_with_its_error_object_is_cut_short()
    {
        var length = FourMebibytes - ErrorWithoutMessage.Length - 10 + 1;
        var function = TestFunction.Define(async context =>
            await context.Step.RunAsync("export", () => Throw(new ExportException(new string('x', length)))));

        var result = await _host.CallAsync(function, Call.New());

        result.Op().GetProperty("error").GetProperty("message").GetString()
            .ShouldBe($"{new string('x', 16_384)}... ({length - 16_384} more characters left out)");
    }

    [Theory]
    [InlineData("<", 5)]
    [InlineData("&", 5)]
    [InlineData("\u2028", 3)]
    [InlineData("\\u003c", 0)]
    [InlineData("\u00e9\U0001F600", 0)]
    public void Inngest_measures_a_step_error_in_its_error_object_as_Go_writes_it_again(string text, int growth)
    {
        var error = Encoding.UTF8.GetBytes($"{{\"name\":\"Error\",\"message\":\"{new string('x', 4_000_000)}{text}\",\"stack\":\"\"}}");

        Invocation.StoredStepErrorBytes(error).ShouldBe(error.Length + 10 + growth);
    }

    // Every text of the error is cut, through its causes, and what is left fits however the encoder writes it.
    [Theory]
    [InlineData(false, false, "\U0001F600")]
    [InlineData(false, true, "\U0001F600")]
    [InlineData(false, true, "\u2028")]
    [InlineData(true, false, "<")]
    [InlineData(true, true, "<")]
    [InlineData(true, true, "\u00e9")]
    public async Task A_step_error_cut_short_fits_in_4_MiB_however_its_texts_are_written(bool relaxed, bool recorded, string character)
    {
        var settings = relaxed
            ? EngineHost.Settings with
            {
                SerializerOptions = new JsonSerializerOptions(EngineHost.Settings.SerializerOptions) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping },
            }
            : EngineHost.Settings;
        using var host = new EngineHost(settings: settings);
        var text = string.Concat(Enumerable.Repeat(character, 160 * 1024 / character.Length));
        Exception thrown = recorded ? new StepFailedException("earlier", Recorded(text, levels: 8)) : Nested(text, levels: 8);
        var function = TestFunction.Define(async context => await context.Step.RunAsync("export", () => Throw(thrown)));

        var result = await host.CallAsync(function, Call.New());

        result.OpCodes.ShouldBe(["StepError"]);
        var error = result.Op().GetProperty("error");
        Invocation.StoredStepErrorBytes(Encoding.UTF8.GetBytes(error.GetRawText())).ShouldBeLessThanOrEqualTo(FourMebibytes);
        var levels = 0;
        for (var level = error; level.ValueKind == JsonValueKind.Object; level = level.TryGetProperty("cause", out var cause) ? cause : default)
        {
            levels++;
            foreach (var name in recorded ? ["name", "message", "stack", "code"] : new[] { "message", "stack", "code" })
            {
                level.GetProperty(name).GetString().ShouldEndWith(" more characters left out)");
            }

            level.GetProperty("data").GetProperty("code").GetString().ShouldEndWith(" more characters left out)");
        }

        levels.ShouldBe(6);
    }

    [Fact]
    public async Task A_text_is_not_cut_inside_a_surrogate_pair()
    {
        var message = new string('x', 16_383) + string.Concat(Enumerable.Repeat("\U0001F600", 700_000));
        var function = TestFunction.Define(async context => await context.Step.RunAsync("export", () => Throw(new ExportException(message))));

        var result = await _host.CallAsync(function, Call.New());

        result.Op().GetProperty("error").GetProperty("message").GetString()
            .ShouldBe($"{new string('x', 16_383)}... ({message.Length - 16_383} more characters left out)");
    }

    [Fact]
    public async Task A_step_that_takes_the_run_past_32_MiB_of_state_is_logged()
    {
        var chunk = new string('x', 3 * 1024 * 1024);
        var function = TestFunction.Define(async context =>
        {
            for (var i = 0; i < 11; i++)
            {
                await context.Step.RunAsync($"chunk-{i}", () => chunk);
            }

            return null;
        });
        var call = Call.New();
        for (var i = 0; i < 10; i++)
        {
            call = call.Data($"chunk-{i}", $"\"{chunk}\"");
        }

        var result = await _host.CallAsync(function, call);

        result.OpCodes.ShouldBe(["StepRun"]);
        var warning = _host.Logs.GetSnapshot().Where(static record => record.Id.Id == 171).ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldContain("Step chunk-10 of run 01JRUN");
    }

    [Fact]
    public async Task A_run_that_finds_its_1001st_step_is_logged_once()
    {
        var function = TestFunction.Define(async context =>
        {
            for (var i = 0; i < 1_002; i++)
            {
                await context.Step.RunAsync($"step-{i}", () => i);
            }

            return null;
        });
        var call = Call.New();
        for (var i = 0; i < 1_001; i++)
        {
            call = call.Data($"step-{i}", "0");
        }

        await _host.CallAsync(function, call);

        var warning = _host.Logs.GetSnapshot().Where(static record => record.Id.Id == 170).ShouldHaveSingleItem();
        warning.Message.ShouldContain("reached step 1001, \"step-1000\"");
    }

    private static int Throw(Exception exception) => throw exception;

    // The error of ExportException with an empty message.
    private const string ErrorWithoutMessage = """{"name":"ExportException","message":"","stack":"at Export()"}""";

    // Exceptions whose message, stack and code are the text, each the cause of the next.
    private static Exception Nested(string text, int levels)
    {
        Exception? exception = null;
        for (var i = 0; i < levels; i++)
        {
            exception = new TextException(text, exception);
        }

        return exception!;
    }

    // An error that Inngest recorded, all of its texts the text.
    private static FunctionError Recorded(string text, int levels)
    {
        FunctionError? error = null;
        for (var i = 0; i < levels; i++)
        {
            error = new FunctionError { Name = text, Message = text, Stack = text, Code = text, Cause = error };
        }

        return error!;
    }

    private sealed class ExportException(string message) : Exception(message)
    {
        public override string StackTrace => "at Export()";
    }

    private sealed class TextException(string text, Exception? cause) : Exception(text, cause), IHasErrorCode
    {
        public override string StackTrace => Message;

        public string Code => Message;
    }
}
