using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using Inngest.AspNetCore;
using Inngest.Tests.Serving;
using Inngest.Tests.Serving.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Tests.AspNetCore;

// The endpoint on its own, with a response whose caller can be made to hang up.
public sealed class InngestEndpointTests
{
    // Padded, so that the pooled buffer of the request body is one of a size that little else in a test process rents.
    private static readonly byte[] CallBody = Encoding.UTF8.GetBytes(
        """{"ctx":{"attempt":0,"run_id":"01J0000000000000000000000"},"event":{"name":"user/signed.up"},"steps":{},"version":2}"""
        + new string(' ', 3000));

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_streamed_call_whose_caller_hangs_up_goes_on_with_its_request_body_until_its_function_ends()
    {
        var harness = new CommHandlerHarness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ReadOnlyMemory<byte> held = default;
        byte[] original = [];
        harness.Engine.Handler = async (request, _) =>
        {
            // The real engine parses this memory in place and reads the memoized steps out of it as the function reaches them.
            held = request.Body;
            original = request.Body.ToArray();

            // A step that does not honour its cancellation token.
            await release.Task;
            return FakeExecutionEngine.Result(200, "\"done\"");
        };
        var handler = harness.CreateHandler(CommHandlerHarness.DevSettings with { Streaming = true });
        var writeFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = CallContext(harness, new HungUpStream(writeFailed.SetResult));

        var handling = InngestEndpoint.HandleAsync(context, handler);
        harness.Engine.Calls.Count.ShouldBe(1);

        // Three seconds pass, the endpoint answers 201 and writes a space, which fails because the caller has gone.
        harness.Time.Advance(TimeSpan.FromSeconds(3));
        await writeFailed.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);

        // The function is still running on the request body, so the endpoint must not end, and the body must stay as it is.
        var winner = await Task.WhenAny(handling, Task.Delay(TimeSpan.FromMilliseconds(500), CancellationToken));
        winner.ShouldNotBeSameAs(handling, "the endpoint ended, and returned the request body to the pool, while the function was still running");
        MemoryMarshal.TryGetArray(held, out var segment).ShouldBeTrue();
        var next = ArrayPool<byte>.Shared.Rent(segment.Array!.Length);
        next.AsSpan().Fill((byte)'x');
        held.Span.SequenceEqual(original).ShouldBeTrue("another renter of the pool changed the request body the function reads");
        ArrayPool<byte>.Shared.Return(next);

        // Once the function has ended, the endpoint reports the hang-up, and what the function answered goes back to the pool.
        release.SetResult();
        await Should.ThrowAsync<IOException>(handling);
        harness.Engine.LastResult.ShouldNotBeNull().Body.WrittenCount.ShouldBe(0);
    }

    [Fact]
    public async Task The_hang_up_is_what_the_endpoint_reports_when_the_function_ends_by_the_cancellation_it_causes()
    {
        using var abort = new CancellationTokenSource();
        var harness = new CommHandlerHarness();
        harness.Engine.Handler = async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return FakeExecutionEngine.Result(200, "null");
        };
        var handler = harness.CreateHandler(CommHandlerHarness.DevSettings with { Streaming = true });

        // Kestrel aborts the request when a write finds the caller gone.
        var context = CallContext(harness, new HungUpStream(abort.Cancel));
        context.RequestAborted = abort.Token;

        var handling = InngestEndpoint.HandleAsync(context, handler);
        harness.Time.Advance(TimeSpan.FromSeconds(3));

        // The cancelled function ends with an OperationCanceledException, which is not what went wrong.
        await Should.ThrowAsync<IOException>(handling);
    }

    private static DefaultHttpContext CallContext(CommHandlerHarness harness, Stream response)
    {
        var services = new ServiceCollection().AddSingleton<TimeProvider>(harness.Time).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = "POST";
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("shop.example.com");
        context.Request.Path = "/api/inngest";
        context.Request.QueryString = new QueryString("?fnId=shop-send-email&stepId=step");
        context.Request.Body = new MemoryStream(CallBody);
        context.Request.ContentLength = CallBody.Length;
        context.Response.Body = response;
        return context;
    }

    // A response whose caller has gone: every write fails.
    private sealed class HungUpStream(Action onWrite) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => HangUp();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => HangUp();

        private ValueTask HangUp()
        {
            onWrite();
            throw new IOException("The caller hung up.");
        }
    }
}
