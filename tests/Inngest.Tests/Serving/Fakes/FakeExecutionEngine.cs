using System.Text;
using Inngest.Execution;
using Inngest.Functions;
using Inngest.Internal;

namespace Inngest.Tests.Serving.Fakes;

internal sealed class FakeExecutionEngine : IExecutionEngine
{
    public List<Call> Calls { get; } = [];

    // Answers 200 "done" unless a test sets its own behaviour.
    public Func<ExecutionRequest, CancellationToken, Task<ExecutionResult>> Handler { get; set; } =
        (_, _) => Task.FromResult(Result(200, "\"done\""));

    public ExecutionResult? LastResult { get; private set; }

    public static ExecutionResult Result(int statusCode, string body, bool? noRetry = null, string? retryAfter = null)
    {
        var buffer = new PooledBufferWriter();
        buffer.Write(Encoding.UTF8.GetBytes(body));
        return new ExecutionResult(statusCode, buffer) { NoRetry = noRetry, RetryAfter = retryAfter };
    }

    public async Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken)
    {
        // The body lives in a buffer the host reuses after the request, so keep a copy.
        Calls.Add(new Call(request.Function, request.Body.ToArray(), request.StepId, request.Services, cancellationToken)
        {
            RequestId = request.RequestId,
            JobId = request.JobId,
            GenerationId = request.GenerationId,
        });
        LastResult = await Handler(request, cancellationToken);
        return LastResult;
    }

    public sealed record Call(
        FunctionDefinition Function,
        byte[] Body,
        string? StepId,
        IServiceProvider Services,
        CancellationToken CancellationToken)
    {
        public string? RequestId { get; init; }

        public string? JobId { get; init; }

        public int? GenerationId { get; init; }

        public string BodyText => Encoding.UTF8.GetString(Body);
    }
}
