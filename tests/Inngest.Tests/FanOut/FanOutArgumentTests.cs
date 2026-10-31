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
    public async Task Inngest_fails_a_run_once_1000_steps_have_completed_so_a_list_of_1000_items_is_refused_with_the_way_out()
    {
        var step = new InlineSteps();

        var exception = Should.Throw<NonRetriableException>(() => step.MapAsync("id", new int[1000], Identity));
        var outcomes = Should.Throw<NonRetriableException>(() => step.MapOutcomesAsync("id", new int[1001], Identity));

        exception.Message.ShouldContain("1000 items");
        exception.Message.ShouldContain("once 1000 of its steps have completed");
        exception.Message.ShouldContain("every step of the run counts");
        exception.Message.ShouldContain("MapInChunksAsync");
        outcomes.Message.ShouldContain("1001 items");
        (await step.MapAsync("id", new int[999], Identity)).Length.ShouldBe(999);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_chunk_size_below_one_is_refused_as_an_argument(int chunkSize)
    {
        IStepTools step = new InlineSteps();

        var exception = Should.Throw<ArgumentOutOfRangeException>(
            () => step.MapInChunksAsync<ChargeInvoices, Invoice, Receipt>("id", [new Invoice("a", 1m)], chunkSize));

        exception.ParamName.ShouldBe("chunkSize");
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(1001)]
    public void A_chunk_size_that_makes_the_run_of_a_chunk_overflow_is_refused_without_retries(int chunkSize)
    {
        IStepTools step = new InlineSteps();

        var exception = Should.Throw<NonRetriableException>(
            () => step.MapOutcomesInChunksAsync<ChargeInvoices, Invoice, Receipt>("id", [new Invoice("a", 1m)], chunkSize));

        exception.Message.ShouldContain("chunkSize must be less than 1000");
    }

    [Fact]
    public async Task A_list_that_makes_1000_chunks_is_refused_without_retries()
    {
        var step = new InlineSteps();
        var invoices = new Invoice[1000];

        var exception = Should.Throw<NonRetriableException>(() => step.MapInChunksAsync<ChargeInvoices, Invoice, Receipt>("id", invoices, 1));
        var exceptionForOutcomes = Should.Throw<NonRetriableException>(() => step.MapOutcomesInChunksAsync<ChargeInvoices, Invoice, Receipt>("id", invoices, 1));

        exception.Message.ShouldContain("1000 chunks");
        exception.Message.ShouldContain("every step of the run counts");
        exceptionForOutcomes.Message.ShouldContain("1000 chunks");

        // 999 chunks are allowed; the fake then fails on the first invoke because it cannot invoke by class.
        await Should.ThrowAsync<NotSupportedException>(() => step.MapInChunksAsync<ChargeInvoices, Invoice, Receipt>("id", invoices[..999], 1));
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
