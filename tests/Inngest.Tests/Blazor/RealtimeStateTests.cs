namespace Inngest.Tests.Blazor;

// What a view keeps of the messages it receives, without a renderer.
public sealed class RealtimeStateTests
{
    private static RealtimeMessage<int> Data(int number, string topic = "status") =>
        new(RealtimeMessageKind.Data, "order:7", topic) { Data = number };

    private static RealtimeMessage<string> Chunk(string text, string topic = "tokens") =>
        new(RealtimeMessageKind.StreamChunk, "order:7", topic) { Text = text, StreamId = "s1" };

    [Fact]
    public void Messages_keep_the_newest_in_order_as_the_buffer_wraps()
    {
        var state = new RealtimeState<int> { Capacity = 3 };

        for (var number = 1; number <= 7; number++)
        {
            state.Add(Data(number));
        }

        Numbers(state).ShouldBe([5, 6, 7]);
        state.Messages.Count.ShouldBe(3);
        state.Messages[0].Data.ShouldBe(5);
        state.Messages[2].Data.ShouldBe(7);
        state.Latest!.Data.ShouldBe(7);
    }

    [Fact]
    public void Raising_the_limit_after_the_buffer_wrapped_keeps_the_order()
    {
        var state = new RealtimeState<int> { Capacity = 3 };
        for (var number = 1; number <= 5; number++)
        {
            state.Add(Data(number));
        }

        state.Capacity = 5;
        state.Add(Data(6));
        state.Add(Data(7));
        Numbers(state).ShouldBe([3, 4, 5, 6, 7]);
        state.Add(Data(8));

        Numbers(state).ShouldBe([4, 5, 6, 7, 8]);
    }

    [Fact]
    public void Lowering_the_limit_keeps_the_newest()
    {
        var state = new RealtimeState<int> { Capacity = 5 };
        for (var number = 1; number <= 7; number++)
        {
            state.Add(Data(number));
        }

        state.Capacity = 2;
        Numbers(state).ShouldBe([6, 7]);
        state.Add(Data(8));

        Numbers(state).ShouldBe([7, 8]);
    }

    [Fact]
    public void A_limit_of_zero_keeps_no_history_but_still_the_latest_messages()
    {
        var state = new RealtimeState<int> { Capacity = 0 };

        state.Add(Data(1, "status"));
        state.Add(Data(2, "audit"));

        state.Messages.ShouldBeEmpty();
        state.Latest!.Data.ShouldBe(2);
        state.LatestByTopic.Keys.Order().ShouldBe(["audit", "status"]);
    }

    [Fact]
    public void An_index_outside_the_messages_is_refused()
    {
        var state = new RealtimeState<int> { Capacity = 3 };
        state.Add(Data(1));

        Should.Throw<ArgumentOutOfRangeException>(() => state.Messages[1]);
        Should.Throw<ArgumentOutOfRangeException>(() => state.Messages[-1]);
    }

    [Fact]
    public void The_latest_message_of_each_topic_and_the_text_of_each_stream_are_kept_per_topic()
    {
        var state = new RealtimeState<string> { Capacity = 1 };

        state.Add(new RealtimeMessage<string>(RealtimeMessageKind.StreamStart, "order:7", "tokens") { StreamId = "s1" });
        state.Add(Chunk("Hel"));
        state.Add(Chunk("lo", "other"));
        state.Add(Chunk("lo"));
        state.Add(new RealtimeMessage<string>(RealtimeMessageKind.Data, "order:7", "status") { Data = "Packed" });

        state.TextByTopic.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}").ShouldBe(["other=lo", "tokens=Hello"]);
        state.LatestByTopic["tokens"].Text.ShouldBe("lo");
        state.LatestByTopic["status"].Data.ShouldBe("Packed");
        state.Messages.Count.ShouldBe(1);

        // The next stream of a topic starts its text over.
        state.Add(new RealtimeMessage<string>(RealtimeMessageKind.StreamStart, "order:7", "tokens") { StreamId = "s2" });
        state.Add(Chunk("Bye"));

        state.TextByTopic["tokens"].ShouldBe("Bye");
    }

    [Fact]
    public void Reading_the_text_again_only_turns_the_topics_that_grew_into_strings_again()
    {
        var state = new RealtimeState<string> { Capacity = 0 };
        state.Add(new RealtimeMessage<string>(RealtimeMessageKind.StreamStart, "order:7", "a") { StreamId = "s1" });
        state.Add(new RealtimeMessage<string>(RealtimeMessageKind.StreamStart, "order:7", "b") { StreamId = "s2" });
        state.Add(Chunk("first", "a"));
        state.Add(Chunk("second", "b"));
        var before = state.TextByTopic;
        var untouched = state.TextByTopic["b"];
        var grown = state.TextByTopic["a"];

        state.Add(Chunk(" more", "a"));

        state.TextByTopic["a"].ShouldBe("first more");
        ReferenceEquals(state.TextByTopic["b"], untouched).ShouldBeTrue();
        ReferenceEquals(state.TextByTopic["a"], grown).ShouldBeFalse();

        // Reading twice with nothing new between hands over the same strings.
        ReferenceEquals(state.TextByTopic["a"], state.TextByTopic["a"]).ShouldBeTrue();
        ReferenceEquals(state.TextByTopic, before).ShouldBeTrue();
    }

    [Fact]
    public void The_text_of_a_stream_stops_growing_at_its_limit_and_stays_one_piece()
    {
        var state = new RealtimeState<string> { Capacity = 0 };
        state.Add(new RealtimeMessage<string>(RealtimeMessageKind.StreamStart, "order:7", "tokens") { StreamId = "s1" });

        state.Add(Chunk(new string('a', (1 << 20) - 2)));
        state.Add(Chunk("bbbb"));
        state.Add(Chunk("c"));

        var text = state.TextByTopic["tokens"];
        text.Length.ShouldBe(1 << 20);
        text.EndsWith("aabb", StringComparison.Ordinal).ShouldBeTrue();
        state.Latest!.Text.ShouldBe("c");
    }

    [Fact]
    public void A_message_proves_the_connection_is_up_and_a_failure_is_the_last_word()
    {
        var state = new RealtimeState<int>();
        state.Begin(RealtimeConnectionState.Connecting);

        state.Add(Data(1));
        state.Connection.ShouldBe(RealtimeConnectionState.Open);
        state.Report(RealtimeConnectionState.Reconnecting);
        state.Connection.ShouldBe(RealtimeConnectionState.Reconnecting);
        state.Add(Data(2));
        state.Connection.ShouldBe(RealtimeConnectionState.Open);

        var failure = new InvalidOperationException("Refused.");
        state.Fail(failure);
        state.Report(RealtimeConnectionState.Open);
        state.Add(Data(3));

        state.Connection.ShouldBe(RealtimeConnectionState.Failed);
        state.Error.ShouldBe(failure);
    }

    [Fact]
    public void A_new_subscription_starts_from_nothing()
    {
        var state = new RealtimeState<int> { Capacity = 3 };
        state.Add(Data(1));
        state.Fail(new InvalidOperationException("Refused."));

        state.Begin(RealtimeConnectionState.Connecting);

        state.Messages.ShouldBeEmpty();
        state.Latest.ShouldBeNull();
        state.LatestByTopic.ShouldBeEmpty();
        state.TextByTopic.ShouldBeEmpty();
        state.Error.ShouldBeNull();
        state.Connection.ShouldBe(RealtimeConnectionState.Connecting);
        state.Add(Data(2));
        Numbers(state).ShouldBe([2]);
    }

    private static List<int> Numbers(RealtimeState<int> state) => [.. state.Messages.Select(message => message.Data)];
}
