using System.Net;
using System.Text;
using Inngest.Tests.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Testing;

namespace Inngest.Extensions.AI.Tests;

// The text of a streaming model call goes to IInngestRealtime.PublishStreamAsync as the model writes it, and that gathers
// the updates, which come every few characters, into the pieces Inngest relays.
public sealed class StreamPublisherTests
{
    private static readonly RealtimeTopic Topic = new("chat:1", "tokens");

    private readonly FakeLogger _logger = new();
    private readonly RecordingRealtime _realtime = new();

    [Fact]
    public async Task Updates_go_to_the_stream_as_the_model_writes_them_and_completing_ends_it()
    {
        var publisher = Publisher();

        publisher.Write("Once ");
        publisher.Write("");
        publisher.Write("upon ");
        await _realtime.WaitForChunksAsync(2);
        publisher.Write("a time");
        await publisher.CompleteAsync();

        _realtime.Chunks.ShouldBe(["Once ", "upon ", "a time"]);
        _logger.Collector.GetSnapshot().ShouldBeEmpty();
    }

    // The updates are gathered once, so an update waits for more no longer than a stream from anywhere does.
    [Fact]
    public async Task An_update_reaches_Inngest_once_it_has_waited_100_ms_for_more()
    {
        var time = new TestTimeProvider(EngineHost.Now);
        var inngest = new Receiving();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddInngest(options =>
        {
            options.AppId = "test-app";
            options.IsDev = true;
            options.BaseUrl = new Uri("http://localhost:8288");
        });
        services.AddHttpClient("Inngest").ConfigurePrimaryHttpMessageHandler(() => inngest);
        await using var provider = services.BuildServiceProvider();
        var publisher = new StreamPublisher(provider.GetRequiredService<IInngestRealtime>(), Topic, "chat", _logger, TestContext.Current.CancellationToken);

        publisher.Write("Once ");
        publisher.Write("upon");
        await time.TimersCreated(1).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMilliseconds(99));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        inngest.Writes.ShouldBeEmpty();
        time.Advance(TimeSpan.FromMilliseconds(1));
        await inngest.WaitForWritesAsync(1);
        publisher.Write(" a time");
        await publisher.CompleteAsync();

        inngest.Writes.ShouldBe(["Once upon", " a time"]);
        _logger.Collector.GetSnapshot().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_publish_that_fails_is_logged_and_the_text_after_it_is_dropped()
    {
        var realtime = new RecordingRealtime { Fail = new InngestException("Inngest answered 500.") };
        var publisher = Publisher(realtime);

        publisher.Write("Hello");
        await publisher.CompleteAsync();
        publisher.Write(" world");

        realtime.Chunks.ShouldBeEmpty();
        var warning = _logger.Collector.GetSnapshot().ShouldHaveSingleItem();
        warning.Message.ShouldBe("The text of step chat could not be streamed to topic tokens of channel chat:1, and the model call goes on without: Inngest answered 500.");
    }

    private StreamPublisher Publisher(RecordingRealtime? realtime = null) =>
        new(realtime ?? _realtime, Topic, "chat", _logger, TestContext.Current.CancellationToken);

    // Takes the body of a publish as Inngest does, write by write as it arrives.
    private sealed class Receiving : HttpMessageHandler
    {
        private readonly Lock _gate = new();
        private readonly List<string> _writes = [];

        public IReadOnlyList<string> Writes
        {
            get
            {
                lock (_gate)
                {
                    return [.. _writes];
                }
            }
        }

        public async Task WaitForWritesAsync(int count)
        {
            using var giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (Writes.Count < count)
            {
                await Task.Delay(5, giveUp.Token);
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await request.Content!.CopyToAsync(new Body(this), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        private sealed class Body(Receiving owner) : MemoryStream
        {
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                lock (owner._gate)
                {
                    owner._writes.Add(Encoding.UTF8.GetString(buffer.Span));
                }

                return ValueTask.CompletedTask;
            }
        }
    }
}
