using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Tests.DependencyInjection;

public sealed class AddMiddlewareTests
{
    [Fact]
    public async Task One_middleware_instance_serves_a_whole_invocation_scope()
    {
        var services = new ServiceCollection();
        services.AddInngest(options => options.AppId = "shop").AddMiddleware<CountingMiddleware>();
        await using var provider = services.BuildServiceProvider();

        await using var invocation = provider.CreateAsyncScope();
        var forTheRun = invocation.ServiceProvider.GetServices<InngestMiddleware>().ShouldHaveSingleItem();
        var forAStepSend = invocation.ServiceProvider.GetServices<InngestMiddleware>().ShouldHaveSingleItem();
        await using var nextInvocation = provider.CreateAsyncScope();
        var forTheNextRun = nextInvocation.ServiceProvider.GetServices<InngestMiddleware>().ShouldHaveSingleItem();

        forAStepSend.ShouldBeSameAs(forTheRun);
        forTheNextRun.ShouldNotBeSameAs(forTheRun);
    }

    [Fact]
    public void Registering_the_same_middleware_twice_keeps_one_registration()
    {
        var services = new ServiceCollection();

        services.AddInngest(options => options.AppId = "shop").AddMiddleware<CountingMiddleware>().AddMiddleware<CountingMiddleware>();

        services.Count(descriptor => descriptor.ServiceType == typeof(InngestMiddleware)).ShouldBe(1);
    }

    private sealed class CountingMiddleware : InngestMiddleware;
}
