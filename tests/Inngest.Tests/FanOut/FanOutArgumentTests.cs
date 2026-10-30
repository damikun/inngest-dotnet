namespace Inngest.Tests.FanOut;

// Arguments are checked on the caller's thread, before any step exists, and the limits of a run are reported by name.
public sealed class FanOutArgumentTests
{
    private static readonly Func<int, CancellationToken, Task<int>> Identity = (item, _) => Task.FromResult(item);

    [Fact]
    public void The_arguments_are_checked_when_the_method_is_called()
    {
        IStepTools step = new InlineSteps();
        int[] items = [1, 2];

        Should.Throw<ArgumentNullException>(() => FanOutExtensions.MapAsync(null!, "id", items, Identity)).ParamName.ShouldBe("step");
        Should.Throw<ArgumentException>(() => step.MapAsync("", items, Identity)).ParamName.ShouldBe("id");
        Should.Throw<ArgumentNullException>(() => step.MapAsync("id", null!, Identity)).ParamName.ShouldBe("items");
        Should.Throw<ArgumentNullException>(() => step.MapAsync("id", items, (Func<int, CancellationToken, Task<int>>)null!)).ParamName.ShouldBe("handler");
        Should.Throw<ArgumentNullException>(() => step.ForEachAsync("id", items, null!)).ParamName.ShouldBe("handler");
        Should.Throw<ArgumentNullException>(() => step.MapOutcomesAsync("id", items, (Func<int, CancellationToken, Task<int>>)null!)).ParamName.ShouldBe("handler");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_limit_below_one_is_refused(int limit)
    {
        IStepTools step = new InlineSteps();
        var options = new FanOutOptions { MaxParallelism = limit };

        Should.Throw<ArgumentOutOfRangeException>(() => step.MapAsync("id", new[] { 1 }, Identity, options)).ParamName.ShouldBe("options");
        Should.Throw<ArgumentOutOfRangeException>(() => step.MapOutcomesAsync("id", new[] { 1 }, Identity, options)).ParamName.ShouldBe("options");
        Should.Throw<ArgumentOutOfRangeException>(() => step.MapInChunksAsync<ChargeInvoices, Invoice, Receipt>("id", [new Invoice("a", 1m)], 1, options))
            .ParamName.ShouldBe("options");
    }

    [Fact]
    public async Task A_run_has_at_most_as_many_steps_as_Inngest_allows_so_a_longer_list_is_refused_with_the_way_out()
    {
        var step = new InlineSteps();

        var exception = Should.Throw<ArgumentException>(() => step.MapAsync("id", new int[1001], Identity));

        exception.ParamName.ShouldBe("items");
        exception.Message.ShouldContain("1001 items");
        exception.Message.ShouldContain("1000 steps");
        exception.Message.ShouldContain("MapInChunksAsync");
        (await step.MapAsync("id", new int[1000], Identity)).Count.ShouldBe(1000);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1001)]
    public void A_chunk_size_the_chunks_run_cannot_have_is_refused(int chunkSize)
    {
        IStepTools step = new InlineSteps();

        var exception = Should.Throw<ArgumentOutOfRangeException>(
            () => step.MapInChunksAsync<ChargeInvoices, Invoice, Receipt>("id", [new Invoice("a", 1m)], chunkSize));

        exception.ParamName.ShouldBe("chunkSize");
        exception.Message.ShouldContain("1000");
    }

    [Fact]
    public async Task More_chunks_than_steps_a_run_may_have_are_refused()
    {
        var step = new InlineSteps();
        var invoices = new Invoice[1001];

        var exception = Should.Throw<ArgumentException>(() => step.MapInChunksAsync<ChargeInvoices, Invoice, Receipt>("id", invoices, 1));
        var exceptionForOutcomes = Should.Throw<ArgumentException>(() => step.MapOutcomesInChunksAsync<ChargeInvoices, Invoice, Receipt>("id", invoices, 1));

        exception.ParamName.ShouldBe("items");
        exception.Message.ShouldContain("1001 chunks");
        exceptionForOutcomes.Message.ShouldContain("1001 chunks");

        // 1000 chunks are allowed; the fake then fails on the first invoke because it cannot invoke by class.
        await Should.ThrowAsync<NotSupportedException>(() => step.MapInChunksAsync<ChargeInvoices, Invoice, Receipt>("id", invoices[..1000], 1));
    }

    [Fact]
    public async Task A_list_without_items_is_done_at_once_and_needs_no_function()
    {
        var step = new InlineSteps();

        (await step.MapInChunksAsync<ChargeInvoices, Invoice, Receipt>("id", [], 10)).ShouldBeEmpty();
        (await step.MapOutcomesInChunksAsync<ChargeInvoices, Invoice, Receipt>("id", [], 10)).ShouldBeEmpty();
        (await step.MapOutcomesAsync("id", Array.Empty<int>(), Identity)).ShouldBeEmpty();
        await step.ForEachAsync("id", Array.Empty<int>(), (_, _) => Task.CompletedTask);
        step.Calls.ShouldBeEmpty();
    }
}
