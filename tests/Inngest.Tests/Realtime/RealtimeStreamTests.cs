using System.Net;
using System.Text;
using Inngest.Realtime;
using Inngest.Tests.Client;
using Inngest.Tests.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Inngest.Tests.Realtime;

public sealed class RealtimeStreamTests
{
    private static readonly RealtimeTopic Tokens = new("chat:42", "tokens");

    [Fact]
    public async Task A_stream_is_posted_as_text_stream_and_the_whole_text_comes_back()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK);
        await using var provider = ClientServices.Build(handler, new FakeTimeProvider(DateTimeOffset.UnixEpoch));

        var text = await provider.GetRequiredService<IInngestRealtime>().PublishStreamAsync(Tokens, Pieces("Hel", "", "lo ", "wörld"));

        text.ShouldBe("Hello wörld");
        var request = handler.Single;
        request.Uri.AbsoluteUri.ShouldBe("http://localhost:8288/v1/realtime/publish?channel=chat%3A42&topic=tokens");
        request.Header("Content-Type").ShouldBe("text/stream");
        request.BodyText.ShouldBe("Hello wörld");
    }

    [Fact]
    public async Task Long_chunks_go_out_in_pieces_below_1KB_that_end_on_whole_characters()
    {
        // 300 four-byte emoji: 1,200 bytes whose surrogate pairs a cut by bytes or chars would split.
        var text = string.Concat(Enumerable.Repeat("😀", 300)) + "é";
        var writes = new RecordingStream();

        await new TextStreamContent(Pieces(text)).CopyToAsync(writes, TestContext.Current.CancellationToken);

        writes.Pieces.ShouldAllBe(piece => piece.Length <= 1024);
        writes.Pieces.Count.ShouldBe(2);
        string.Concat(writes.Pieces.Select(piece => new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(piece))).ShouldBe(text);
    }

    [Fact]
    public async Task A_stream_step_publishes_with_the_run_and_memoizes_the_text()
    {
        var api = new FakeRealtimeApi();
        using var host = new EngineHost(services => services.AddSingleton<IRealtimeApi>(api));
        var function = TestFunction.Define(async context => await context.Step.PublishStreamAsync("reply", Tokens, Pieces("Hello", " world")));

        var result = await host.CallAsync(function, Call.New());

        result.OpCodes.ShouldBe(["StepRun"]);
        result.Op().GetProperty("opts").GetRawText().ShouldBe("""{"type":"step.realtime.publish"}""");
        result.Op().GetProperty("data").GetString().ShouldBe("Hello world");
        api.Published.ShouldBe([(Tokens, (string?)"01JRUN", "Hello world")]);
    }

    [Fact]
    public async Task A_replayed_stream_step_does_not_read_its_chunks_again()
    {
        var api = new FakeRealtimeApi();
        var reads = 0;
        using var host = new EngineHost(services => services.AddSingleton<IRealtimeApi>(api));
        var function = TestFunction.Define(async context => await context.Step.PublishStreamAsync("reply", Tokens, Counted()));

        var result = await host.CallAsync(function, Call.New().Data("reply", "\"Hello world\""));

        result.Body.ShouldBe("\"Hello world\"");
        reads.ShouldBe(0);
        api.Published.ShouldBeEmpty();

        async IAsyncEnumerable<string> Counted()
        {
            reads++;
            yield return "never";
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_typed_topic_publishes_its_data_type_from_steps_and_from_anywhere()
    {
        var api = new FakeRealtimeApi();
        var status = new RealtimeTopic<Progress>("order:7", "status");
        using var host = new EngineHost(services => services.AddSingleton<IRealtimeApi>(api));
        var function = TestFunction.Define(async context => (await context.Step.PublishAsync("status", status, new Progress("Packed", 50))).Percent);

        var result = await host.CallAsync(function, Call.New());
        var realtime = new InngestRealtime(api, new RealtimeSubscriber(api, TimeProvider.System, NullLogger<RealtimeSubscriber>.Instance), EngineHost.Settings, NullLogger<InngestRealtime>.Instance);
        await realtime.PublishAsync(status, new Progress("Shipped", 100), TestContext.Current.CancellationToken);

        result.Op().GetProperty("data").GetRawText().ShouldBe("""{"text":"Packed","percent":50}""");
        api.Published.Select(publish => publish.Body).ShouldBe(["""{"text":"Packed","percent":50}""", """{"text":"Shipped","percent":100}"""]);
        api.Published.ShouldAllBe(publish => publish.Topic == status.Untyped);
    }

    private static async IAsyncEnumerable<string> Pieces(params string[] pieces)
    {
        foreach (var piece in pieces)
        {
            await Task.Yield();
            yield return piece;
        }
    }

    private sealed record Progress(string Text, int Percent);

    // Keeps every write the content makes, to check where it cuts the text.
    private sealed class RecordingStream : MemoryStream
    {
        public List<byte[]> Pieces { get; } = [];

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Pieces.Add(buffer.ToArray());
            return base.WriteAsync(buffer, cancellationToken);
        }
    }
}
