using Microsoft.Azure.Functions.Worker;

namespace Inngest.Tests.AzureFunctions.Fakes;

// What a function reads from its context when it serves Inngest. The members the adapter has no business with fail loudly,
// so that a change that starts using one of them shows up in the tests. Named in full, since Inngest.FunctionContext is
// found first from a namespace inside Inngest.
internal sealed class FakeFunctionContext(IServiceProvider services, CancellationToken cancellationToken = default)
    : Microsoft.Azure.Functions.Worker.FunctionContext
{
    public override string InvocationId => "invocation-1";

    public override string FunctionId => "function-1";

    public override TraceContext TraceContext => throw new NotSupportedException();

    public override BindingContext BindingContext => throw new NotSupportedException();

    public override RetryContext RetryContext => throw new NotSupportedException();

    public override IServiceProvider InstanceServices { get; set; } = services;

    public override FunctionDefinition FunctionDefinition => throw new NotSupportedException();

    // Where the ASP.NET Core integration of the worker puts the HttpContext of the request, under "HttpRequestContext".
    public override IDictionary<object, object> Items { get; set; } = new Dictionary<object, object>();

    public override IInvocationFeatures Features => throw new NotSupportedException();

    public override CancellationToken CancellationToken { get; } = cancellationToken;
}
