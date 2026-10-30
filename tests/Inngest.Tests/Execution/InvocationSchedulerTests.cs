using Inngest.Execution;

namespace Inngest.Tests.Execution;

public sealed class InvocationSchedulerTests
{
    [Fact]
    public void Code_waiting_on_a_task_that_never_completes_is_seen_as_blocked()
    {
        var scheduler = new InvocationScheduler();
        var step = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = false;

        var run = scheduler.Run(async () =>
        {
            await Task.Yield();
            reached = true;
            await step.Task;
            return 1;
        });
        scheduler.RunPending();

        reached.ShouldBeTrue();
        run.IsCompleted.ShouldBeFalse();
        scheduler.RunPending().ShouldBe(0);
    }

    [Fact]
    public async Task Completing_a_step_resumes_the_code_on_the_scheduler()
    {
        var scheduler = new InvocationScheduler();
        var step = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumedOnScheduler = false;

        var run = scheduler.Run(async () =>
        {
            var value = await step.Task;
            resumedOnScheduler = scheduler.IsCurrent;
            return value * 2;
        });
        scheduler.RunPending();
        step.SetResult(21);
        scheduler.RunPending();

        run.IsCompletedSuccessfully.ShouldBeTrue();
        (await run).ShouldBe(42);
        resumedOnScheduler.ShouldBeTrue();
    }

    [Fact]
    public void All_parallel_branches_run_before_the_code_is_seen_as_blocked()
    {
        var scheduler = new InvocationScheduler();
        var started = new List<string>();
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task Branch(string name)
        {
            await Task.Yield();
            await Task.Yield();
            started.Add(name);
            await never.Task;
        }

        scheduler.Run(() => Task.WhenAll(Branch("a"), Branch("b"), Branch("c")));
        scheduler.RunPending();

        started.ShouldBe(["a", "b", "c"], ignoreOrder: true);
    }

    [Fact]
    public async Task Real_io_posts_its_continuation_later()
    {
        var scheduler = new InvocationScheduler();
        var io = new TaskCompletionSource();
        var afterIo = false;

        var run = scheduler.Run(async () =>
        {
            await io.Task;
            afterIo = true;
        });
        scheduler.RunPending();
        afterIo.ShouldBeFalse();

        // Finished on a thread pool thread, as a socket or a timer would.
        await Task.Run(io.SetResult, TestContext.Current.CancellationToken);
        (await scheduler.WaitForWorkAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        scheduler.RunPending();

        afterIo.ShouldBeTrue();
        run.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public void Code_that_keeps_yielding_is_drained_in_bounded_chunks()
    {
        var scheduler = new InvocationScheduler();
        var iterations = 0;

        scheduler.Run(async () =>
        {
            while (true)
            {
                iterations++;
                await Task.Yield();
            }
        });

        scheduler.RunPending(limit: 10).ShouldBe(10);
        scheduler.RunPending(limit: 10).ShouldBe(10);
        iterations.ShouldBe(21);
    }

    [Fact]
    public async Task Work_posted_after_close_runs_on_the_thread_pool()
    {
        var scheduler = new InvocationScheduler();
        scheduler.Close();
        var ran = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        scheduler.Post(_ => ran.SetResult(Thread.CurrentThread.IsThreadPoolThread), null);

        scheduler.RunPending().ShouldBe(0);
        (await ran.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task Work_still_queued_at_close_runs_on_the_thread_pool()
    {
        var scheduler = new InvocationScheduler();
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.Post(_ => ran.SetResult(), null);

        scheduler.Close();

        await ran.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Late_work_that_throws_is_dropped()
    {
        var scheduler = new InvocationScheduler();
        scheduler.Close();
        var after = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // On the thread pool an exception from a work item would end the test process.
        scheduler.Post(_ => throw new InvalidOperationException("late"), null);
        scheduler.Post(_ => after.SetResult(), null);

        await after.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }
}
