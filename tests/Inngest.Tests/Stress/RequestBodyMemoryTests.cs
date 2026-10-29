using Inngest.AspNetCore;
using Microsoft.AspNetCore.Http;

namespace Inngest.Tests.Stress;

// The request body is read before its signature can be checked, so what a caller declares must not decide how much
// memory the app sets aside for it.
public sealed class RequestBodyMemoryTests
{
    [Fact]
    public async Task A_declared_length_reserves_at_most_64_KB_before_the_body_arrives()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentLength = 4 * 1024 * 1024;
        context.Request.Body = new MemoryStream("{}"u8.ToArray());

        using var body = await InngestEndpoint.ReadBodyAsync(context, TestContext.Current.CancellationToken);

        body.ShouldNotBeNull().WrittenCount.ShouldBe(2);
        (body.WrittenCount + body.GetSpan().Length).ShouldBeLessThanOrEqualTo(64 * 1024);
    }

    [Fact]
    public async Task A_body_larger_than_the_first_buffer_arrives_intact()
    {
        var payload = new byte[3 * 1024 * 1024];
        new Random(3).NextBytes(payload);
        var context = new DefaultHttpContext();
        context.Request.ContentLength = payload.Length;
        context.Request.Body = new MemoryStream(payload);

        using var body = await InngestEndpoint.ReadBodyAsync(context, TestContext.Current.CancellationToken);

        body.ShouldNotBeNull().WrittenSpan.SequenceEqual(payload).ShouldBeTrue();
    }
}
