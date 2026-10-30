using System.Text;
using System.Text.Json;
using Inngest.Execution;
using Inngest.Serving;
using Inngest.Tests.Serving.Fakes;

namespace Inngest.Tests.Serving;

// The JSON a durable endpoint request becomes, in the shape the dev server accepts and replays.
public sealed class EndpointCallsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 21, 0, 0, TimeSpan.Zero);

    private static EndpointRequest Request => new(
        "https://api.example.com",
        "POST",
        "/orders/42",
        "203.0.113.7",
        "application/json",
        "source=spike&n=1",
        """{"orderId":"123"}"""u8.ToArray(),
        "POST /orders/{id}");

    [Fact]
    public void The_event_records_the_request_with_its_body_in_base64()
    {
        using var document = JsonDocument.Parse(EndpointCalls.WriteEvent(Request, Now));
        var root = document.RootElement;

        root.GetProperty("name").GetString().ShouldBe("inngest/http.request");
        root.GetProperty("ts").GetInt64().ShouldBe(Now.ToUnixTimeMilliseconds());
        var data = root.GetProperty("data");
        data.GetProperty("domain").GetString().ShouldBe("https://api.example.com");
        data.GetProperty("method").GetString().ShouldBe("POST");
        data.GetProperty("path").GetString().ShouldBe("/orders/42");
        data.GetProperty("ip").GetString().ShouldBe("203.0.113.7");
        data.GetProperty("content_type").GetString().ShouldBe("application/json");
        data.GetProperty("query_params").GetString().ShouldBe("source=spike&n=1");
        Encoding.UTF8.GetString(data.GetProperty("body").GetBytesFromBase64()).ShouldBe("""{"orderId":"123"}""");
        data.GetProperty("fn").GetString().ShouldBe("POST /orders/{id}");
    }

    [Fact]
    public void A_request_without_a_body_records_an_empty_one()
    {
        using var document = JsonDocument.Parse(EndpointCalls.WriteEvent(Request with { Body = ReadOnlyMemory<byte>.Empty }, Now));

        document.RootElement.GetProperty("data").GetProperty("body").GetString().ShouldBe(string.Empty);
    }

    [Fact]
    public void The_first_call_carries_the_event_and_the_attempts_the_retries_allow()
    {
        var @event = EndpointCalls.WriteEvent(Request, Now);

        using var document = JsonDocument.Parse(EndpointCalls.WriteStartCall(@event, "01JRUN", retries: 3));
        var run = RunRequest.Parse(document.RootElement);

        run.RunId.ShouldBe("01JRUN");
        run.Attempt.ShouldBe(0);
        run.MaxAttempts.ShouldBe(4);
        run.UseApi.ShouldBeFalse();
        run.DisableImmediateExecution.ShouldBeFalse();
        run.EventName.ShouldBe("inngest/http.request");
        run.Events.ShouldHaveSingleItem().GetProperty("name").GetString().ShouldBe("inngest/http.request");
        run.Memo.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null, null, false, false)]
    [InlineData("01JRUN", null, false, false)]
    [InlineData("01JRUN", "", false, false)]
    [InlineData("01JRUN", "t=1&s=ab", false, true)]
    [InlineData(null, "t=1&s=ab", false, false)]
    [InlineData("01JRUN", null, true, true)]
    [InlineData("01JRUN", "", true, true)]
    [InlineData("", "", true, false)]
    public void A_resume_carries_the_run_id_and_outside_dev_mode_a_signature(string? runId, string? signature, bool isDev, bool resume)
    {
        var request = new FakeServeRequest("POST", "http://localhost:3000/orders/42");
        if (runId is not null)
        {
            request.WithHeader("X-Run-ID", runId);
        }

        if (signature is not null)
        {
            request.WithHeader("X-Inngest-Signature", signature);
        }

        EndpointCalls.IsResume(request, isDev).ShouldBe(resume);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_resume_loads_the_run_from_the_API_and_leaves_the_attempts_to_Inngest(bool forceStepPlan)
    {
        using var document = JsonDocument.Parse(EndpointCalls.WriteResumeCall("01JRUN", forceStepPlan));
        var run = RunRequest.Parse(document.RootElement);

        run.RunId.ShouldBe("01JRUN");
        run.UseApi.ShouldBeTrue();
        run.MaxAttempts.ShouldBeNull();
        run.DisableImmediateExecution.ShouldBe(forceStepPlan);
    }
}
