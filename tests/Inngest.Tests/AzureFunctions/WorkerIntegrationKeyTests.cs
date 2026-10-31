using Inngest.AzureFunctions;
using Inngest.Tests.AzureFunctions.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Tests.AzureFunctions;

// The adapter finds the ASP.NET Core integration by an entry in FunctionContext.Items that the worker does not document.
// These tests run against the package of the integration, so that a release that moves the entry fails here and not in a
// deployed app, where the adapter would go on answering through the built-in model and never stream.
public sealed class WorkerIntegrationKeyTests
{
    [Fact]
    public void The_key_the_adapter_looks_under_is_the_one_the_integration_reads_the_HttpContext_from()
    {
        var httpContext = new DefaultHttpContext();
        var context = new FakeFunctionContext(new ServiceCollection().BuildServiceProvider());
        context.Items[FunctionsEndpoint.HttpContextKey] = httpContext;

        // GetHttpContext() is what code that works with the integration calls to reach the context.
        Microsoft.Azure.Functions.Worker.FunctionContextExtensions.GetHttpContext(context).ShouldBeSameAs(httpContext);
    }

    [Fact]
    public void Without_the_entry_the_integration_has_no_HttpContext_to_give()
    {
        var context = new FakeFunctionContext(new ServiceCollection().BuildServiceProvider());

        Microsoft.Azure.Functions.Worker.FunctionContextExtensions.GetHttpContext(context).ShouldBeNull();
    }
}
