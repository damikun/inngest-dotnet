using System.Text.Json;
using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Evaluation.Harness;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.AI;

// What the dev server makes of the AI metadata of steps and of the inference steps of the model helpers.
public sealed class ModelTests(EvaluationEnvironment environment) : EvaluationScenario<ModelFixture>(environment)
{
    private const string MetadataQuery = """
        query ($runID: String!) {
          run(runID: $runID) {
            trace {
              childrenSpans {
                name stepID
                metadata { kind scope values }
                childrenSpans { name stepID metadata { kind scope values } }
              }
            }
          }
        }
        """;

    [Theory(Timeout = 120_000)]
    [InlineData(ModelFunctions.Usage)]
    [InlineData(ModelFunctions.CheckpointedUsage)]
    public async Task The_trace_shows_the_usage_of_a_model_call_on_its_step(string trigger)
    {
        var run = await RunToEndAsync(trigger, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<string>().ShouldBe("Hello there");
        var metadata = await Poll.UntilAsync(
            $"the chat step of run {run.RunId} to show its AI metadata",
            token => AIMetadataOfAsync(run.RunId, token),
            found => found is not null,
            DevServerClient.DefaultTimeout,
            Fixture.DescribeAsync,
            Cancellation);

        var values = metadata!.Value.GetProperty("values");
        metadata.Value.GetProperty("scope").GetString().ShouldBe("step");
        values.GetProperty("input_tokens").GetInt32().ShouldBe(21);
        values.GetProperty("output_tokens").GetInt32().ShouldBe(5);
        values.GetProperty("cache_read_tokens").GetInt32().ShouldBe(16);
        values.GetProperty("request_model").GetString().ShouldBe("gpt-4o-mini");
        values.GetProperty("response_model").GetString().ShouldBe("gpt-4o-mini-2024-07-18");
        values.GetProperty("operation_name").GetString().ShouldBe("chat");
        values.GetProperty("finish_reasons")[0].GetString().ShouldBe("stop");
    }

    [Fact(Timeout = 120_000)]
    public async Task The_dev_server_sends_the_request_of_each_model_where_the_model_says()
    {
        var run = await RunToEndAsync(ModelFunctions.Infer, new RunEvent(TestId), TimeSpan.FromSeconds(60));

        run.Status.ShouldBe(RunStatus.Completed, run.ToString());
        run.OutputAs<string[]>().ShouldBe(
            ["Hello from openai", "Hello from responses", "Hello from anthropic", "Hello from gemini", "Hello from grok", "Hello from azure"]);

        var requests = Observed.Values<ProviderRequest>("provider").ToDictionary(request => request.Provider);
        requests["openai"].PathAndQuery.ShouldBe("/v1/chat/completions");
        requests["openai"].Headers["authorization"].ShouldBe("Bearer sk-openai");
        requests["openai"].Body.GetProperty("model").GetString().ShouldBe("gpt-4o-mini");

        requests["responses"].PathAndQuery.ShouldBe("/v1/responses");
        requests["responses"].Body.GetProperty("input")[0].GetProperty("content").GetString().ShouldBe("Hi");

        requests["anthropic"].PathAndQuery.ShouldBe("/v1/messages");
        requests["anthropic"].Headers["x-api-key"].ShouldBe("sk-ant");
        requests["anthropic"].Headers["anthropic-version"].ShouldBe("2023-06-01");
        requests["anthropic"].Body.GetProperty("max_tokens").GetInt32().ShouldBe(256);

        requests["gemini"].PathAndQuery.ShouldStartWith("/v1/models/gemini-2.5-flash:generateContent?key=gemini-key");
        requests["gemini"].Body.TryGetProperty("model", out _).ShouldBeFalse();

        requests["grok"].Headers["authorization"].ShouldBe("Bearer xai-key");

        requests["azure"].PathAndQuery.ShouldBe("/v1/openai/deployments/gpt-4o/chat/completions?api-version=2024-10-21");
        requests["azure"].Headers["api-key"].ShouldBe("azure-key");
    }

    // The inngest.ai metadata of the first span that has any, at the two levels where steps appear.
    private async Task<JsonElement?> AIMetadataOfAsync(string runId, CancellationToken cancellationToken)
    {
        using var body = await Api.QueryAsync(MetadataQuery, new { runID = runId }, cancellationToken);
        var run = body.RootElement.GetProperty("data").GetProperty("run");
        if (run.ValueKind != JsonValueKind.Object || run.GetProperty("trace").ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var span in Spans(run.GetProperty("trace")))
        {
            if (span.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in metadata.EnumerateArray())
                {
                    if (entry.GetProperty("kind").GetString() == "inngest.ai")
                    {
                        return entry.Clone();
                    }
                }
            }
        }

        return null;
    }

    private static IEnumerable<JsonElement> Spans(JsonElement span)
    {
        if (!span.TryGetProperty("childrenSpans", out var children) || children.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var child in children.EnumerateArray())
        {
            yield return child;
            foreach (var grandchild in Spans(child))
            {
                yield return grandchild;
            }
        }
    }
}
