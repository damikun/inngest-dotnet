using Inngest.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

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

    [Fact]
    public async Task A_declared_length_past_the_host_limit_still_reads_the_body_that_arrives()
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(new HostBodyLimit(4));
        context.Request.ContentLength = 32L * 1024 * 1024;
        context.Request.Body = new MemoryStream("{}"u8.ToArray());

        using var body = await InngestEndpoint.ReadBodyAsync(context, TestContext.Current.CancellationToken);

        body.ShouldNotBeNull().WrittenSpan.SequenceEqual("{}"u8).ShouldBeTrue();
        (body.WrittenCount + body.GetSpan().Length).ShouldBeLessThanOrEqualTo(64 * 1024);
    }

    [Fact]
    public async Task A_body_past_the_host_limit_is_refused_without_naming_a_size()
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(new HostBodyLimit(8));
        context.Request.Body = new MemoryStream(new byte[9]);

        var error = await Should.ThrowAsync<RequestBodyTooLargeException>(() =>
            InngestEndpoint.ReadBodyAsync(context, TestContext.Current.CancellationToken));

        error.Message.ShouldBe("The request body is too large.");
    }

    [Fact]
    public async Task A_host_limit_is_what_decides_and_a_body_within_it_arrives()
    {
        var context = new DefaultHttpContext();
        var payload = new byte[32];
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(new HostBodyLimit(payload.Length));
        context.Request.Body = new MemoryStream(payload);

        using var body = await InngestEndpoint.ReadBodyAsync(context, TestContext.Current.CancellationToken);

        body.ShouldNotBeNull().WrittenCount.ShouldBe(payload.Length);
    }

    [Fact]
    public async Task A_raised_host_limit_accepts_a_body_the_old_ceiling_would_have_refused()
    {
        var length = 9 * 1024 * 1024;
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(new HostBodyLimit(length));
        context.Request.Body = new MemoryStream(new byte[length]);

        using var body = await InngestEndpoint.ReadBodyAsync(context, TestContext.Current.CancellationToken);

        body.ShouldNotBeNull().WrittenCount.ShouldBe(length);
    }

    [Fact]
    public async Task Without_a_host_limit_the_backstop_still_refuses_without_naming_a_size()
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(new HostBodyLimit(null));
        context.Request.Body = new MemoryStream(new byte[9 * 1024 * 1024]);

        var error = await Should.ThrowAsync<RequestBodyTooLargeException>(() =>
            InngestEndpoint.ReadBodyAsync(context, TestContext.Current.CancellationToken));

        error.Message.ShouldBe("The request body is too large.");
    }

    private sealed class HostBodyLimit(long? size) : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => true;

        public long? MaxRequestBodySize { get; set; } = size;
    }
}
