using Inngest.IntegrationTests.Functions;
using Inngest.IntegrationTests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests;

public sealed class RunTests(DevServerFixture fixture) : IntegrationTest<DevServerFixture>(fixture)
{
    [Fact(Timeout = 30_000)]
    public async Task Event_starts_a_run_that_completes_with_the_function_output()
    {
        var run = await RunToEndAsync("itests/hello", new GreetEvent(TestId, "Ada"));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<Greeting>().ShouldBe(new Greeting("Hello, Ada!"));
    }

    // In dev mode the client needs no event key.
    [Fact(Timeout = 30_000)]
    public async Task Client_sends_an_event_that_starts_a_run()
    {
        var client = Fixture.App.Services.GetRequiredService<IInngestClient>();

        var sent = await client.SendAsync(new InngestEvent("itests/hello", new GreetEvent(TestId, "Grace")), Cancellation);
        var run = await Api.WaitForRunEndAsync(sent.Ids.ShouldHaveSingleItem(), Cancellation);

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<Greeting>().ShouldBe(new Greeting("Hello, Grace!"));
    }

    [Fact(Timeout = 30_000)]
    public async Task Client_sends_several_events_in_one_request_and_each_starts_a_run()
    {
        var client = Fixture.App.Services.GetRequiredService<IInngestClient>();

        var sent = await client.SendAsync(
            [
                new InngestEvent("itests/hello", new GreetEvent(TestId, "Alan")),
                new InngestEvent("itests/hello", new GreetEvent(TestId, "Barbara")),
            ],
            Cancellation);
        var runs = await Task.WhenAll(sent.Ids.Select(id => Api.WaitForRunEndAsync(id, Cancellation)));

        sent.Ids.Count.ShouldBe(2);
        runs.Select(run => run.OutputAs<Greeting>().Message).ShouldBe(["Hello, Alan!", "Hello, Barbara!"], ignoreOrder: true);
    }
}
