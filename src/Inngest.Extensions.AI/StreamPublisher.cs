// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Inngest.Extensions.AI;

// Publishes the text of a streaming model call to a realtime topic while the model produces it, in one request, as
// IInngestRealtime.PublishStreamAsync does. The text goes through a channel so that the model call never waits for the
// publish and never fails with it: a progress display is not worth a second call to a paid model. The model call is
// what the step memoizes, so a replay publishes nothing.
internal sealed class StreamPublisher
{
    private readonly Channel<string> _chunks = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly Task _publishing;

    public StreamPublisher(IInngestRealtime realtime, RealtimeTopic topic, string stepId, ILogger logger, CancellationToken cancellationToken) =>
        _publishing = PublishAsync(realtime, topic, stepId, logger, cancellationToken);

    public void Write(string text)
    {
        if (text.Length > 0)
        {
            _chunks.Writer.TryWrite(text);
        }
    }

    // Ends the stream and waits until Inngest has taken it. Never throws.
    public Task CompleteAsync()
    {
        _chunks.Writer.TryComplete();
        return _publishing;
    }

    private async Task PublishAsync(IInngestRealtime realtime, RealtimeTopic topic, string stepId, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            await realtime.PublishStreamAsync(topic, _chunks.Reader.ReadAllAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The call is being abandoned, and its own cancellation says so.
            _chunks.Writer.TryComplete();
        }
        catch (Exception exception)
        {
            // Writes after a failure are refused instead of piling up.
            _chunks.Writer.TryComplete(exception);
            Log.StreamNotPublished(logger, stepId, topic.Name, topic.Channel, exception.Message);
        }
    }
}
