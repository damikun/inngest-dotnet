using System.Text.Json;
using Inngest.Testing;
using Microsoft.Extensions.DependencyInjection;
using static Inngest.Tests.Testing.TestHosts;

namespace Inngest.Tests.FanOut;

// An app that serializes with a source-generated context and no reflection, as a trimmed or Native AOT app does. The
// engine writes the output of a function and reads the result of an invoke by the runtime type of the value, so the types
// the guide tells such an app to list have to be the types that cross the wire. FanOutJsonContext lists exactly those.
public sealed class FanOutSourceGenerationTests
{
    private static readonly Invoice[] Invoices = [new("inv-0", 1m), new("inv-1", 2m), new("inv-2", 3m), new("inv-3", 4m)];

    [Fact]
    public async Task A_chunk_runs_and_returns_its_outcomes_with_only_the_listed_types_in_the_resolver()
    {
        await using var host = new InngestTestHost(Services(received: null));
        var chunk = new InngestEvent("inngest/function.invoked", new FanOutChunk<Invoice>(2, Invoices[2..], false));

        var run = await host.RunAsync<ChargeInvoices>(chunk, Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed, run.Error?.ToString());
        var outcomes = run.GetOutput<FanOutOutcome<Receipt>[]>()!;
        outcomes.Select(outcome => outcome.Index).ShouldBe([2, 3]);
        outcomes.Select(outcome => outcome.Value).ShouldBe([new Receipt("inv-2", "receipt-inv-2"), new Receipt("inv-3", "receipt-inv-3")]);
    }

    [Fact]
    public async Task A_parent_sends_the_chunks_and_reads_their_outcomes_with_only_the_listed_types_in_the_resolver()
    {
        Receipt[]? received = null;
        await using var host = new InngestTestHost(Services(results => received = results));
        var mocks = new StepMocks()
            .Invoke<ChargeInvoices, FanOutOutcome<Receipt>[]>("charge[0-1]", [new(0, new Receipt("inv-0", "r-0"), null), new(1, new Receipt("inv-1", "r-1"), null)])
            .Invoke<ChargeInvoices, FanOutOutcome<Receipt>[]>("charge[2-3]", [new(2, new Receipt("inv-2", "r-2"), null), new(3, new Receipt("inv-3", "r-3"), null)]);

        var run = await host.RunAsync("fn", Event(), mocks, Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed, run.Error?.ToString());
        received.ShouldBe([new Receipt("inv-0", "r-0"), new Receipt("inv-1", "r-1"), new Receipt("inv-2", "r-2"), new Receipt("inv-3", "r-3")]);

        // The event data of a chunk is written as the record, with its items.
        var data = run.GetStep("charge[2-3]").Options.GetProperty("payload").GetProperty("data");
        data.GetProperty("offset").GetInt32().ShouldBe(2);
        data.GetProperty("items").GetArrayLength().ShouldBe(2);
    }

    // The function the app writes returns nothing, so that the context needs only the types of the chunks.
    private static ServiceCollection Services(Action<Receipt[]>? received)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new Charger());
        services.AddInngest(options =>
            {
                options.AppId = AppId;
                options.SerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = FanOutJsonContext.Default };
            })
            .AddFunction(Options(retries: 0), async (context, _) =>
            {
                received?.Invoke(await context.Step.MapInChunksAsync<ChargeInvoices, Invoice, Receipt>("charge", Invoices, 2));
                return null;
            })
            .AddFunction<ChargeInvoices>(new FunctionOptions { Id = "charge-invoices", Triggers = [], Retries = 0 });
        return services;
    }
}
