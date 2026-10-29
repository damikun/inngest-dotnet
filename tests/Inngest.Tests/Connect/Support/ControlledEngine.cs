using System.Text;
using System.Threading.Channels;
using Inngest.Execution;
using Inngest.Internal;

namespace Inngest.Tests.Connect.Support;

// An execution engine whose executions the test finishes, so that it decides when a reply is due and can look at
// what the worker does in the meantime.
internal sealed class ControlledEngine : IExecutionEngine
{
    private readonly Channel<ControlledExecution> _started = Channel.CreateUnbounded<ControlledExecution>();

    public async Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken)
    {
        var execution = new ControlledExecution(request, cancellationToken);
        _started.Writer.TryWrite(execution);
        return await execution.Result.WaitAsync(cancellationToken);
    }

    public async Task<ControlledExecution> NextAsync()
    {
        try
        {
            return await _started.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Waits.Timeout);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException("The worker did not start an execution.", exception);
        }
    }

    public bool TryTake(out ControlledExecution execution) => _started.Reader.TryRead(out execution!);
}

internal sealed class ControlledExecution(ExecutionRequest request, CancellationToken cancellationToken)
{
    private readonly TaskCompletionSource<ExecutionResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ExecutionRequest Request { get; } = request;

    // The body is only valid while the execution runs, so it is read up front.
    public string Body { get; } = Encoding.UTF8.GetString(request.Body.Span);

    public CancellationToken CancellationToken { get; } = cancellationToken;

    public Task<ExecutionResult> Result => _result.Task;

    public void Complete(int statusCode, string body, bool? noRetry = null, string? retryAfter = null)
    {
        var buffer = new PooledBufferWriter();
        buffer.Write(Encoding.UTF8.GetBytes(body));
        _result.TrySetResult(new ExecutionResult(statusCode, buffer) { NoRetry = noRetry, RetryAfter = retryAfter });
    }

    public void Fail(Exception exception) => _result.TrySetException(exception);
}
