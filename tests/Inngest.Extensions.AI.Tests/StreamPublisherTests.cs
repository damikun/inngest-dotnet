using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Inngest.Extensions.AI.Tests;

// The text of a streaming model call goes to Inngest as chunks of up to 1 KB, at most 1,000 to a stream, gathered from the
// updates of the model, which come every few characters.
public sealed class StreamPublisherTests
{
    private static readonly RealtimeTopic Topic = new("chat:1", "tokens");
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    private readonly FakeTimeProvider _time = new();
    private readonly FakeLogger _logger = new();
    private readonly RecordingRealtime _realtime = new();

    [Fact]
    public async Task Updates_that_arrive_within_the_interval_go_out_as_one_chunk()
    {
        var publisher = Publisher();

        publisher.Write("Once ");
        publisher.Write("upon ");
        publisher.Write("a time");
        _realtime.Chunks.ShouldBeEmpty();
        _time.Advance(Interval);
        await _realtime.WaitForChunksAsync(1);
        await publisher.CompleteAsync();

        _realtime.Chunks.ShouldBe(["Once upon a time"]);
    }

    [Fact]
    public async Task Updates_that_arrive_further_apart_go_out_as_chunks_of_their_own()
    {
        var publisher = Publisher();

        publisher.Write("Once ");
        _time.Advance(Interval);
        await _realtime.WaitForChunksAsync(1);
        publisher.Write("upon ");
        _time.Advance(Interval);
        await _realtime.WaitForChunksAsync(2);
        await publisher.CompleteAsync();

        _realtime.Chunks.ShouldBe(["Once ", "upon "]);
    }

    [Fact]
    public async Task A_full_chunk_goes_out_at_once_and_what_is_left_waits()
    {
        var publisher = Publisher();

        publisher.Write(new string('a', 2500));
        await _realtime.WaitForChunksAsync(2);

        _realtime.Chunks.Select(chunk => chunk.Length).ShouldBe([1024, 1024]);
        await publisher.CompleteAsync();
        _realtime.Chunks.Select(chunk => chunk.Length).ShouldBe([1024, 1024, 452]);
    }

    [Fact]
    public async Task A_chunk_never_holds_more_than_1KB_and_never_splits_a_character()
    {
        var publisher = Publisher();
        var text = string.Concat(Enumerable.Repeat("😀é", 700)) + "!";

        publisher.Write(text);
        await publisher.CompleteAsync();

        string.Concat(_realtime.Chunks).ShouldBe(text);
        _realtime.Chunks.ShouldAllBe(chunk => Encoding.UTF8.GetByteCount(chunk) <= 1024);
        _realtime.Chunks.All(chunk => !char.IsLowSurrogate(chunk[0]) && !char.IsHighSurrogate(chunk[^1])).ShouldBeTrue();
    }

    [Fact]
    public async Task A_high_surrogate_at_the_end_waits_for_its_other_half()
    {
        var publisher = Publisher();

        publisher.Write("a\uD83D");
        _time.Advance(Interval);
        await _realtime.WaitForChunksAsync(1);
        publisher.Write("\uDE00");
        _time.Advance(Interval);
        await _realtime.WaitForChunksAsync(2);
        await publisher.CompleteAsync();

        _realtime.Chunks.ShouldBe(["a", "😀"]);
    }

    [Fact]
    public async Task Completing_sends_what_waits_and_ends_the_stream()
    {
        var publisher = Publisher();

        publisher.Write("Hi");
        await publisher.CompleteAsync();

        _realtime.Chunks.ShouldBe(["Hi"]);
    }

    [Fact]
    public async Task The_text_waits_ten_times_as_long_once_the_stream_has_500_chunks()
    {
        var publisher = Publisher();
        publisher.Write(new string('a', 1024 * 500 + 10));
        await _realtime.WaitForChunksAsync(500);

        // The rest of the long text has waited for its interval, which is the longer one now.
        _time.Advance(Interval);
        _realtime.Chunks.Count.ShouldBe(500);
        _time.Advance(TimeSpan.FromMilliseconds(900));
        await _realtime.WaitForChunksAsync(501);
        await publisher.CompleteAsync();

        _realtime.Chunks[500].ShouldBe(new string('a', 10));
    }

    [Fact]
    public async Task A_text_that_outgrows_the_limit_is_published_up_to_it_and_the_rest_is_left_out_with_one_warning()
    {
        var publisher = Publisher();

        publisher.Write(new string('a', 1024 * 1000 + 5000));
        publisher.Write("never");
        await publisher.CompleteAsync();

        _realtime.Chunks.Count.ShouldBe(1000);
        _realtime.Chunks.ShouldAllBe(chunk => chunk.Length == 1024);
        var warning = _logger.Collector.GetSnapshot().ShouldHaveSingleItem();
        (warning.Level, warning.Message).ShouldBe((LogLevel.Warning, "The text of step chat is longer than the 1000 chunks of up to 1024 bytes that Inngest takes in a stream, so the rest is not published to topic tokens of channel chat:1."));
    }

    [Fact]
    public async Task A_publish_that_fails_is_logged_and_the_text_after_it_is_dropped()
    {
        var realtime = new RecordingRealtime { Fail = new InngestException("Inngest answered 500.") };
        var publisher = Publisher(realtime);

        publisher.Write("Hello");
        _time.Advance(Interval);
        await publisher.CompleteAsync();

        realtime.Chunks.ShouldBeEmpty();
        var warning = _logger.Collector.GetSnapshot().ShouldHaveSingleItem();
        warning.Message.ShouldBe("The text of step chat could not be streamed to topic tokens of channel chat:1, and the model call goes on without: Inngest answered 500.");
    }

    private StreamPublisher Publisher(RecordingRealtime? realtime = null) =>
        new(realtime ?? _realtime, Topic, "chat", _logger, _time, Interval, TestContext.Current.CancellationToken);
}
