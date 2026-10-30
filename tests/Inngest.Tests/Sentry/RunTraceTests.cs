using System.Text.RegularExpressions;
using Inngest.Sentry;
using Sentry;

namespace Inngest.Tests.Sentry;

// A run id is mapped to a trace id the way the TypeScript middleware maps it. The expected ids below are what its
// function, ulidToTraceId in @inngest/middleware-sentry 1.0.0, returns for the same input.
public sealed class RunTraceTests
{
    [Theory]
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FAV", "01563e3ab5d3d6764c61efb99302bd5b")]
    [InlineData("01JBS7Q4R2X9M5N8T3V6W0Y1ZA", "0192f27b9302ea685aa343d9b80f07ea")]
    [InlineData("01JD0Z5K9XQ8R3M7N2P4T6V8WB", "019341f2cd3dba303a1ea2b1346da38b")]
    [InlineData("00000000000000000000000000", "00000000000000000000000000000000")]
    [InlineData("7ZZZZZZZZZZZZZZZZZZZZZZZZZ", "ffffffffffffffffffffffffffffffff")]
    // The first character holds three bits, so a larger one loses its top two, as it does there.
    [InlineData("8ZZZZZZZZZZZZZZZZZZZZZZZZZ", "1fffffffffffffffffffffffffffffff")]
    public void A_ULID_is_the_trace_id_it_encodes(string runId, string traceId) =>
        RunTrace.IdOf(runId).ToString().ShouldBe(traceId);

    [Fact]
    public void Case_does_not_matter()
    {
        RunTrace.IdOf("01jd0z5k9xq8r3m7n2p4t6v8wb").ShouldBe(RunTrace.IdOf("01JD0Z5K9XQ8R3M7N2P4T6V8WB"));
        RunTrace.IdOf("01k8m3zq4r5s6t7v8w9x0y1z2a").ToString().ShouldBe("019a283fdc982e4da3ed1c4f41e0fc4a");
    }

    [Theory]
    [InlineData("01JRUN")]
    [InlineData("")]
    [InlineData("01JD0Z5K9XQ8R3M7N2P4T6V8WBX")]
    [InlineData("01JD0Z5K9XQ8R3M7N2P4T6V8WI")]
    public void An_id_that_is_no_ULID_still_gets_a_trace_id_of_its_own_that_never_changes(string runId)
    {
        var traceId = RunTrace.IdOf(runId);

        traceId.ToString().ShouldMatch("^[0-9a-f]{32}$");
        traceId.ShouldNotBe(SentryId.Empty);
        RunTrace.IdOf(runId).ShouldBe(traceId);
        RunTrace.IdOf(runId + "0").ShouldNotBe(traceId);
    }

    [Fact]
    public void The_header_continues_the_trace_from_a_span_that_never_existed_and_makes_no_sampling_decision()
    {
        var header = RunTrace.HeaderOf("01JD0Z5K9XQ8R3M7N2P4T6V8WB");

        header.TraceId.ToString().ShouldBe("019341f2cd3dba303a1ea2b1346da38b");
        header.SpanId.ToString().ShouldBe("1000000000000000");
        header.IsSampled.ShouldBeNull();
        Regex.IsMatch(header.ToString(), "^[0-9a-f]{32}-1000000000000000$").ShouldBeTrue();
    }
}
