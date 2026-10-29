namespace Inngest.Tests.Steps;

// A fake written against the six original members, like the one in the testing guide, keeps compiling and covers
// the StepOptions overloads and ParallelAsync through the interface's default members.
public sealed class StepToolsFakeTests
{
    private static readonly StepOptions Named = new("step-id") { Name = "A name", ParallelMode = ParallelMode.Race };

    [Fact]
    public async Task The_step_options_overloads_fall_back_to_the_id_overloads()
    {
        var fake = new InlineStepTools();
        IStepTools step = fake;

        (await step.RunAsync(Named, _ => Task.FromResult(1))).ShouldBe(1);
        await step.SleepAsync(Named, TimeSpan.FromMinutes(1));
        await step.SleepUntilAsync(Named, DateTimeOffset.UnixEpoch);
        (await step.WaitForEventAsync<string>(Named, new WaitForEventOptions("app/approved", TimeSpan.FromMinutes(1)))).ShouldBeNull();
        (await step.InvokeAsync<int>(Named, new InvokeOptions("billing"))).ShouldBe(0);
        (await step.SendEventAsync(Named, [new InngestEvent("app/shipped", new { })])).Ids.ShouldBe(["step-id-0"]);

        fake.Calls.ShouldBe(["run step-id", "sleep step-id", "sleep-until step-id", "wait step-id", "invoke step-id", "send step-id"]);
    }

    [Fact]
    public async Task The_extension_overloads_with_step_options_reach_the_fake_too()
    {
        var fake = new InlineStepTools();
        IStepTools step = fake;

        (await step.RunAsync(Named, () => 2)).ShouldBe(2);
        (await step.RunAsync(Named, () => Task.FromResult(3))).ShouldBe(3);
        await step.RunAsync(Named, () => { });
        await step.RunAsync(Named, () => Task.CompletedTask);
        await step.RunAsync(Named, (CancellationToken _) => Task.CompletedTask);
        (await step.WaitForEventAsync(Named, new WaitForEventOptions("app/approved", TimeSpan.FromMinutes(1)))).ShouldBeNull();
        await step.SendEventAsync(Named, new InngestEvent("app/shipped", new { }));

        fake.Calls.ShouldBe(["run step-id", "run step-id", "run step-id", "run step-id", "run step-id", "wait step-id", "send step-id"]);
    }

    [Fact]
    public async Task ParallelAsync_runs_the_callback_on_a_fake()
    {
        IStepTools step = new InlineStepTools();

        var first = await await step.ParallelAsync(ParallelMode.Race, () => Task.WhenAny(step.RunAsync("a", () => "a"), step.RunAsync("b", () => "b")));
        await step.ParallelAsync(ParallelMode.Race, () => step.SleepAsync("nap", TimeSpan.FromSeconds(1)));

        first.ShouldBe("a");
    }

    [Fact]
    public async Task A_handler_returning_a_ValueTask_is_rejected_with_step_options_too()
    {
        IStepTools step = new InlineStepTools();

        var exception = await Should.ThrowAsync<ArgumentException>(() => step.RunAsync(Named, () => ValueTask.CompletedTask));

        exception.Message.ShouldContain("\"step-id\"");
        exception.ParamName.ShouldBe("handler");
    }

    private sealed class InlineStepTools : IStepTools
    {
        public List<string> Calls { get; } = [];

        public async Task<T> RunAsync<T>(string id, Func<CancellationToken, Task<T>> handler)
        {
            Calls.Add($"run {id}");
            return await handler(CancellationToken.None);
        }

        public Task SleepAsync(string id, TimeSpan duration)
        {
            Calls.Add($"sleep {id}");
            return Task.CompletedTask;
        }

        public Task SleepUntilAsync(string id, DateTimeOffset until)
        {
            Calls.Add($"sleep-until {id}");
            return Task.CompletedTask;
        }

        public Task<InngestEvent<TData>?> WaitForEventAsync<TData>(string id, WaitForEventOptions options)
        {
            Calls.Add($"wait {id}");
            return Task.FromResult<InngestEvent<TData>?>(null);
        }

        public Task<TResult> InvokeAsync<TResult>(string id, InvokeOptions options)
        {
            Calls.Add($"invoke {id}");
            return Task.FromResult(default(TResult)!);
        }

        public Task<SendEventsResult> SendEventAsync(string id, IReadOnlyList<InngestEvent> events)
        {
            Calls.Add($"send {id}");
            return Task.FromResult(new SendEventsResult([.. events.Select((_, i) => $"{id}-{i}")]));
        }
    }
}
