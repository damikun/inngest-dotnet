using System.Text.Json;
using Inngest.Testing;
using Inngest.UserContext;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Inngest.Tests.UserContext;

// Functions under the in-memory test host: with the middleware added they run as the user of their event, and the events
// they send, which the host records, carry it on.
public sealed class TestHostTests
{
    [Fact]
    public async Task A_function_under_the_test_host_runs_as_the_user_of_its_event_and_passes_it_on()
    {
        var keys = new EphemeralDataProtectionProvider();
        await using var sender = new UserContextApp(keys, options => options.Claims = ["sub", "name", "role"]);
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(UserContextApp.Now));
        services.AddSingleton<IDataProtectionProvider>(keys);
        services
            .AddInngest(options => options.AppId = "test-app")
            .AddUserContext(options => options.Claims = ["sub", "name", "role"])
            .AddFunction(UserContextApp.Options(), async (context, _) =>
            {
                var seen = UserContextApp.SeenBy(context);
                await context.Step.SendEventAsync("notify", [new InngestEvent("shop/notified", new { })]);
                return seen;
            });
        await using var host = new InngestTestHost(services);
        var user = JsonDocument.Parse(sender.Seal(UserContextApp.Ada())).RootElement.Clone();

        var run = await host.RunAsync("fn", new InngestEvent(UserContextApp.EventName, new { }) { User = user }, TestContext.Current.CancellationToken);

        run.Status.ShouldBe(TestRunStatus.Completed);
        var seen = run.GetOutput<UserContextApp.Seen>().ShouldNotBeNull();
        seen.IsAuthenticated.ShouldBeTrue();
        seen.Name.ShouldBe("Ada Lovelace");
        var sent = run.SentEvents.ShouldHaveSingleItem();
        var carried = ((JsonElement)sent.User!).GetProperty(UserContextCodec.Key);
        carried.GetProperty("v").GetInt32().ShouldBe(1);
        carried.GetProperty("t").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_function_under_the_test_host_runs_without_a_user_when_its_event_has_none()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(UserContextApp.Now));
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services
            .AddInngest(options => options.AppId = "test-app")
            .AddUserContext()
            .AddFunction(UserContextApp.Options(), (context, _) => Task.FromResult<object?>(UserContextApp.SeenBy(context)));
        await using var host = new InngestTestHost(services);

        var run = await host.RunAsync("fn", new InngestEvent(UserContextApp.EventName, new { }), TestContext.Current.CancellationToken);

        run.GetOutput<UserContextApp.Seen>().ShouldNotBeNull().IsAuthenticated.ShouldBeFalse();
    }
}
