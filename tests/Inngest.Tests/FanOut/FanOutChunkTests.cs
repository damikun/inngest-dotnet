using System.Text.Json;
using Inngest.Testing;
using Microsoft.Extensions.DependencyInjection;
using static Inngest.Tests.Testing.TestHosts;

namespace Inngest.Tests.FanOut;

// Lists run in chunks by child runs. The function of a chunk runs on the test host as any function does, and the parent
// that invokes it gets the chunks' outputs through invoke mocks.
public sealed class FanOutChunkTests
{
    private static readonly Invoice[] Invoices = [.. Enumerable.Range(0, 7).Select(i => new Invoice($"inv-{i}", i * 10m))];

    [Fact]
    public async Task The_function_of_a_chunk_runs_a_step_for_each_item_with_its_position_in_the_whole_list()
    {
        var charger = new Charger();
        await using var host = new InngestTestHost(Services(charger));

        var run = await host.RunAsync<ChargeInvoices>(Chunk(10, true, Invoices[..3]), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        run.Steps.Select(step => step.Id).ShouldBe(["item[10]", "item[11]", "item[12]"]);
        var outcomes = run.GetOutput<FanOutOutcome<Receipt>[]>()!;
        outcomes.Select(outcome => outcome.Index).ShouldBe([10, 11, 12]);
        outcomes.Select(outcome => outcome.Value).ShouldBe([new Receipt("inv-0", "receipt-inv-0"), new Receipt("inv-1", "receipt-inv-1"), new Receipt("inv-2", "receipt-inv-2")]);
        outcomes.ShouldAllBe(outcome => outcome.Succeeded);
        charger.Charged.Order().ShouldBe(["inv-0", "inv-1", "inv-2"]);
    }

    [Fact]
    public async Task A_chunk_that_collects_runs_every_item_and_returns_the_error_of_the_failed_one()
    {
        var charger = new Charger();
        await using var host = new InngestTestHost(Services(charger));
        Invoice[] items = [new("inv-a", 1m), new("bad-b", 2m), new("inv-c", 3m), new("inv-d", 4m)];

        var run = await host.RunAsync<ChargeInvoices>(Chunk(4, false, items), Cancel);

        var outcomes = run.GetOutput<FanOutOutcome<Receipt>[]>()!;
        outcomes.Select(outcome => outcome.Index).ShouldBe([4, 5, 6, 7]);
        outcomes.Select(outcome => outcome.Succeeded).ShouldBe([true, false, true, true]);
        outcomes[1].Error!.Message.ShouldBe("Card declined for bad-b");
        outcomes[1].Value.ShouldBeNull();
        charger.Charged.Order().ShouldBe(["bad-b", "inv-a", "inv-c", "inv-d"]);
    }

    [Fact]
    public async Task A_chunk_that_fails_fast_starts_no_item_after_the_failure_was_seen()
    {
        var charger = new Charger();
        await using var host = new InngestTestHost(Services(charger));
        Invoice[] items = [new("inv-a", 1m), new("bad-b", 2m), new("inv-c", 3m), new("inv-d", 4m), new("inv-e", 5m)];

        var run = await host.RunAsync<ChargeInvoices>(Chunk(0, true, items), Cancel);

        // The function of the chunk runs two items at a time. Item 2 was started when the result of item 0 was handed
        // back, before the failure of item 1 was, so it runs; items 3 and 4 never start.
        run.Status.ShouldBe(TestRunStatus.Completed);
        var outcomes = run.GetOutput<FanOutOutcome<Receipt>[]>()!;
        outcomes.Select(outcome => outcome.Index).ShouldBe([0, 1]);
        outcomes[1].Error!.Message.ShouldBe("Card declined for bad-b");
        charger.Charged.Order().ShouldBe(["bad-b", "inv-a", "inv-c"]);
    }

    [Fact]
    public async Task A_parent_gets_the_results_of_its_chunks_in_the_order_of_the_list()
    {
        var charger = new Charger();
        await using var host = new InngestTestHost(Services(charger));
        var mocks = await ChunkMocksAsync(host, failFast: true);

        var run = await host.RunAsync("fn", Event(), mocks, Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        run.GetOutput<Receipt[]>().ShouldBe([.. Invoices.Select(invoice => new Receipt(invoice.Id, $"receipt-{invoice.Id}"))]);
        run.Steps.Select(step => step.Id).ShouldBe(["charge[0-2]", "charge[3-5]", "charge[6-6]"]);
        run.Steps.ShouldAllBe(step => step.Type == StepType.Invoke);
    }

    [Fact]
    public async Task A_chunk_is_invoked_with_its_position_and_items_as_event_data()
    {
        await using var host = new InngestTestHost(Services(new Charger()));
        var mocks = await ChunkMocksAsync(host, failFast: true);

        var run = await host.RunAsync("fn", Event(), mocks, Cancel);

        var options = run.GetStep("charge[3-5]").Options;
        options.GetProperty("function_id").GetString().ShouldBe($"{AppId}-charge-invoices");
        var data = options.GetProperty("payload").GetProperty("data");
        data.GetProperty("offset").GetInt32().ShouldBe(3);
        data.GetProperty("failFast").GetBoolean().ShouldBeTrue();
        data.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()).ShouldBe(["inv-3", "inv-4", "inv-5"]);
        options.TryGetProperty("timeout", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task The_timeout_of_a_chunk_is_the_timeout_of_its_invoke_step()
    {
        await using var host = new InngestTestHost(Services(new Charger(), chunkTimeout: TimeSpan.FromMinutes(20)));
        var mocks = await ChunkMocksAsync(host, failFast: true);

        var run = await host.RunAsync("fn", Event(), mocks, Cancel);

        run.GetStep("charge[0-2]").Options.GetProperty("timeout").GetString().ShouldBe("20m");
    }

    [Fact]
    public async Task A_failed_item_fails_the_parent_with_the_step_of_the_item()
    {
        await using var host = new InngestTestHost(Services(new Charger()));
        var declined = new FunctionError { Name = "NonRetriableException", Message = "Card declined for inv-4" };
        var mocks = new StepMocks()
            .Invoke<ChargeInvoices, IReadOnlyList<FanOutOutcome<Receipt>>>("charge[0-2]", [.. Enumerable.Range(0, 3).Select(i => new FanOutOutcome<Receipt>(i, new Receipt($"inv-{i}", "r"), null))])
            .Invoke<ChargeInvoices, IReadOnlyList<FanOutOutcome<Receipt>>>("charge[3-5]", [new(3, new Receipt("inv-3", "r"), null), new(4, default, declined)])
            .Invoke<ChargeInvoices, IReadOnlyList<FanOutOutcome<Receipt>>>("charge[6-6]", [new(6, new Receipt("inv-6", "r"), null)]);

        var run = await host.RunAsync("fn", Event(), mocks, Cancel);

        run.Status.ShouldBe(TestRunStatus.Failed);
        var failure = run.Error.ShouldBeOfType<StepFailedException>();
        failure.StepId.ShouldBe("charge[4]");
        failure.Error.ShouldBe(declined);
    }

    [Fact]
    public async Task A_chunk_whose_run_fails_fails_the_parent_with_the_step_of_the_chunk()
    {
        await using var host = new InngestTestHost(Services(new Charger()));
        var mocks = (await ChunkMocksAsync(host, failFast: true))
            .InvokeThrows("charge[3-5]", new NonRetriableException("the chunk's run failed"));

        var run = await host.RunAsync("fn", Event(), mocks, Cancel);

        run.Status.ShouldBe(TestRunStatus.Failed);
        var failure = run.Error.ShouldBeOfType<StepFailedException>();
        failure.StepId.ShouldBe("charge[3-5]");
        failure.Error.Message.ShouldBe("the chunk's run failed");
    }

    [Fact]
    public async Task Outcomes_of_chunks_are_collected_and_a_failed_chunk_fails_each_of_its_items()
    {
        await using var host = new InngestTestHost(Services(new Charger(), collect: true));
        var declined = new FunctionError { Name = "NonRetriableException", Message = "Card declined for inv-1" };
        var mocks = new StepMocks()
            .Invoke<ChargeInvoices, IReadOnlyList<FanOutOutcome<Receipt>>>("charge[0-2]", [new(0, new Receipt("inv-0", "r"), null), new(1, default, declined), new(2, new Receipt("inv-2", "r"), null)])
            .Invoke<ChargeInvoices, IReadOnlyList<FanOutOutcome<Receipt>>>("charge[3-5]", [.. Enumerable.Range(3, 3).Select(i => new FanOutOutcome<Receipt>(i, new Receipt($"inv-{i}", "r"), null))])
            .InvokeThrows("charge[6-6]", new InvalidOperationException("the chunk's run failed"));

        var run = await host.RunAsync("fn", Event(), mocks, Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed, run.Error?.ToString());
        var outcomes = run.GetOutput<FanOutOutcome<Receipt>[]>()!;
        outcomes.Select(outcome => outcome.Index).ShouldBe([0, 1, 2, 3, 4, 5, 6]);
        outcomes.Select(outcome => outcome.Succeeded).ShouldBe([true, false, true, true, true, true, false]);
        outcomes[1].Error.ShouldBe(declined);
        outcomes[6].Error!.Message.ShouldBe("the chunk's run failed");
    }

    [Fact]
    public async Task A_result_with_the_wrong_number_of_outcomes_is_refused()
    {
        await using var host = new InngestTestHost(Services(new Charger(), retries: 0));
        var mocks = (await ChunkMocksAsync(host, failFast: true))
            .Invoke<ChargeInvoices, IReadOnlyList<FanOutOutcome<Receipt>>>("charge[3-5]", [new(3, new Receipt("inv-3", "r"), null), new(4, new Receipt("inv-4", "r"), null)]);

        var run = await host.RunAsync("fn", Event(), mocks, Cancel);

        run.Status.ShouldBe(TestRunStatus.Failed);
        run.Error.ShouldBeOfType<InvalidOperationException>().Message.ShouldContain("returned 2 outcomes");
    }

    [Fact]
    public async Task A_result_for_an_item_outside_the_chunk_is_refused()
    {
        await using var host = new InngestTestHost(Services(new Charger(), retries: 0));
        var mocks = (await ChunkMocksAsync(host, failFast: true))
            .Invoke<ChargeInvoices, IReadOnlyList<FanOutOutcome<Receipt>>>("charge[3-5]", [new(3, new Receipt("inv-3", "r"), null), new(3, new Receipt("inv-3", "r"), null), new(9, new Receipt("inv-9", "r"), null)]);

        var run = await host.RunAsync("fn", Event(), mocks, Cancel);

        run.Status.ShouldBe(TestRunStatus.Failed);
        run.Error.ShouldBeOfType<InvalidOperationException>().Message.ShouldContain("outcome for item 3");
    }

    [Fact]
    public async Task A_chunk_function_that_is_not_registered_is_reported_by_its_class()
    {
        var services = new ServiceCollection();
        services.AddInngest(options => options.AppId = AppId).AddFunction(Options(retries: 0), (context, _) => Parent(context, collect: false, null));
        await using var host = new InngestTestHost(services);

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Failed);
        run.Error.ShouldBeOfType<InvalidOperationException>().Message.ShouldContain("AddFunction<ChargeInvoices>()");
    }

    [Fact]
    public async Task A_chunk_function_is_registered_by_its_attributes_and_needs_no_trigger()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new Charger());
        services.AddInngest(options => options.AppId = AppId).AddFunction<AttributedChunk>();
        await using var host = new InngestTestHost(services);

        var run = await host.RunAsync<AttributedChunk>(Chunk(0, true, Invoices[..2]), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        run.FunctionId.ShouldBe("attributed-chunk");
        run.GetOutput<FanOutOutcome<Receipt>[]>()!.Select(outcome => outcome.Index).ShouldBe([0, 1]);
    }

    // The outputs of the chunk function for every chunk of three, as the parent's invoke steps would get them.
    private static async Task<StepMocks> ChunkMocksAsync(InngestTestHost host, bool failFast)
    {
        var mocks = new StepMocks();
        for (var offset = 0; offset < Invoices.Length; offset += 3)
        {
            var items = Invoices[offset..Math.Min(offset + 3, Invoices.Length)];
            var chunk = await host.RunAsync<ChargeInvoices>(Chunk(offset, failFast, items), Cancel);
            mocks = mocks.Invoke<ChargeInvoices, IReadOnlyList<FanOutOutcome<Receipt>>>(
                $"charge[{offset}-{offset + items.Length - 1}]",
                chunk.GetOutput<IReadOnlyList<FanOutOutcome<Receipt>>>()!);
        }

        return mocks;
    }

    private static InngestEvent Chunk(int offset, bool failFast, Invoice[] items) =>
        new("inngest/function.invoked", new FanOutChunk<Invoice>(offset, items, failFast));

    private static async Task<object?> Parent(FunctionContext<JsonElement> context, bool collect, TimeSpan? chunkTimeout)
    {
        var options = new FanOutOptions { MaxParallelism = 2, ChunkTimeout = chunkTimeout };
        return collect
            ? await context.Step.MapOutcomesInChunksAsync<ChargeInvoices, Invoice, Receipt>("charge", Invoices, 3, options)
            : await context.Step.MapInChunksAsync<ChargeInvoices, Invoice, Receipt>("charge", Invoices, 3, options);
    }

    private static ServiceCollection Services(Charger charger, bool collect = false, int? retries = null, TimeSpan? chunkTimeout = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(charger);
        services.AddInngest(options => options.AppId = AppId)
            .AddFunction(Options(retries: retries), (context, _) => Parent(context, collect, chunkTimeout))
            .AddFunction<ChargeInvoices>(new FunctionOptions { Id = "charge-invoices", Triggers = [], Retries = 0 });
        return services;
    }
}
