using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inngest.Tests.FanOut;

// What the chunks put on the wire: the event data that starts a chunk's run, and the outcomes it returns.
public sealed class FanOutSerializationTests
{
    private static readonly JsonSerializerOptions Reflection = new(JsonSerializerDefaults.Web);

    private static readonly JsonSerializerOptions SourceGenerated = new(JsonSerializerDefaults.Web) { TypeInfoResolver = FanOutJsonContext.Default };

    [Fact]
    public void An_outcome_that_succeeded_is_its_index_and_value()
    {
        var outcome = new FanOutOutcome<Receipt>(3, new Receipt("inv-3", "r-3"), null);

        var json = JsonSerializer.Serialize(outcome, Reflection);

        json.ShouldBe("""{"index":3,"value":{"invoiceId":"inv-3","reference":"r-3"}}""");
        outcome.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void An_outcome_that_failed_is_its_index_and_error()
    {
        var outcome = new FanOutOutcome<Receipt>(4, default, new FunctionError { Name = "PaymentError", Message = "declined", Stack = "at Charge()" });

        var json = JsonSerializer.Serialize(outcome, Reflection);

        json.ShouldBe("""{"index":4,"error":{"name":"PaymentError","message":"declined","stack":"at Charge()","cause":null}}""");
        outcome.Succeeded.ShouldBeFalse();
    }

    [Fact]
    public void A_list_of_outcomes_reads_back_the_way_it_was_written()
    {
        FanOutOutcome<Receipt>[] outcomes =
        [
            new(0, new Receipt("inv-0", "r-0"), null),
            new(1, default, new FunctionError { Name = "PaymentError", Message = "declined", Cause = new FunctionError { Name = "HttpRequestException", Message = "reset" } }),
        ];

        var read = JsonSerializer.Deserialize<IReadOnlyList<FanOutOutcome<Receipt>>>(JsonSerializer.Serialize(outcomes, Reflection), Reflection)!;

        read.Count.ShouldBe(2);
        read[0].ShouldBe(outcomes[0]);
        read[1].Index.ShouldBe(1);
        read[1].Value.ShouldBeNull();
        read[1].Error.ShouldBe(outcomes[1].Error);
    }

    [Fact]
    public void An_outcome_of_a_value_type_keeps_its_value()
    {
        var read = JsonSerializer.Deserialize<FanOutOutcome<int>>(JsonSerializer.Serialize(new FanOutOutcome<int>(2, 0, null), Reflection), Reflection);

        read.ShouldBe(new FanOutOutcome<int>(2, 0, null));
    }

    [Fact]
    public void A_chunk_reads_back_the_way_it_was_written()
    {
        var chunk = new FanOutChunk<Invoice>(30, [new Invoice("inv-30", 5m), new Invoice("inv-31", 6m)], true);

        var json = JsonSerializer.Serialize(chunk, Reflection);
        var read = JsonSerializer.Deserialize<FanOutChunk<Invoice>>(json, Reflection)!;

        json.ShouldBe("""{"offset":30,"items":[{"id":"inv-30","amount":5},{"id":"inv-31","amount":6}],"failFast":true}""");
        read.Offset.ShouldBe(30);
        read.Items.ShouldBe(chunk.Items);
        read.FailFast.ShouldBeTrue();
    }

    [Fact]
    public void An_app_that_serializes_with_source_generation_adds_the_types_of_its_chunks_to_its_context()
    {
        var chunk = new FanOutChunk<Invoice>(0, [new Invoice("inv-0", 1m)], false);
        FanOutOutcome<Receipt>[] outcomes = [new(0, new Receipt("inv-0", "r-0"), null), new(1, default, new FunctionError { Name = "E", Message = "m" })];

        var chunkJson = JsonSerializer.Serialize(chunk, SourceGenerated);
        var outcomesJson = JsonSerializer.Serialize<IReadOnlyList<FanOutOutcome<Receipt>>>(outcomes, SourceGenerated);

        chunkJson.ShouldBe(JsonSerializer.Serialize(chunk, Reflection));
        outcomesJson.ShouldBe(JsonSerializer.Serialize(outcomes, Reflection));
        JsonSerializer.Deserialize<FanOutChunk<Invoice>>(chunkJson, SourceGenerated)!.Items.ShouldBe(chunk.Items);
        JsonSerializer.Deserialize<IReadOnlyList<FanOutOutcome<Receipt>>>(outcomesJson, SourceGenerated)![1].Error!.Message.ShouldBe("m");
    }
}

[JsonSerializable(typeof(FanOutChunk<Invoice>))]
[JsonSerializable(typeof(IReadOnlyList<FanOutOutcome<Receipt>>))]
internal sealed partial class FanOutJsonContext : JsonSerializerContext;
