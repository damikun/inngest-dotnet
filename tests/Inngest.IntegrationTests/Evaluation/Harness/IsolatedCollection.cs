namespace Inngest.IntegrationTests.Evaluation.Harness;

// Every Inngest server reaches its app through Docker Desktop's host networking, and opens a new connection for every
// call. Bursts of hundreds of calls, and a server dialing an app that is down, stall new connections for every container
// for seconds, and a call that cannot be delivered still uses up an attempt. Scenarios that cause either run in this
// collection: xUnit runs it after the parallel collections have finished, one scenario at a time.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class IsolatedCollection
{
    public const string Name = "Evaluation, isolated";
}
