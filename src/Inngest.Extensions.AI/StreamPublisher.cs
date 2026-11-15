// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Inngest.Extensions.AI;

// Publishes the text of a streaming model call to a realtime topic while the model produces it, in one request, with
// IInngestRealtime.PublishStreamAsync, which gathers the updates of the model into the pieces Inngest relays. The text goes
// through a channel so that the model call never waits for the publish and never fails with it: a progress display is not
// worth a second call to a paid model. The model call is what the step memoizes, so a replay publishes nothing.
internal sealed class StreamPublisher
{
    private readonly Channel<string> _updates = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly string _stepId;
    private readonly RealtimeTopic _topic;
    private readonly ILogger _logger;
    private readonly Task _publishing;

    public StreamPublisher(IInngestRealtime realtime, RealtimeTopic topic, string stepId, ILogger logger, CancellationToken cancellationToken)
    {
        _topic = topic;
        _stepId = stepId;
        _logger = logger;
        _publishing = PublishAsync(realtime, cancellationToken);
    }

    // Called by the model's loop, one update after the other. Once the publish has ended or failed, the text is dropped
    // instead of piling up.
    public void Write(string text)
    {
        if (text.Length > 0)
        {
            _updates.Writer.TryWrite(text);
        }
    }

    // Ends the stream and waits until Inngest has taken it. Never throws. A stream whose model failed is ended the same way:
    // Inngest ends a request that stops early like one that finished, so aborting it would tell subscribers no more.
    public Task CompleteAsync()
    {
        _updates.Writer.TryComplete();
        return _publishing;
    }

    private async Task PublishAsync(IInngestRealtime realtime, CancellationToken cancellationToken)
    {
        try
        {
            await realtime.PublishStreamAsync(_topic, _updates.Reader.ReadAllAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The call is being abandoned, and its own cancellation says so.
            _updates.Writer.TryComplete();
        }
        catch (Exception exception)
        {
            _updates.Writer.TryComplete();
            Log.StreamNotPublished(_logger, _stepId, _topic.Name, _topic.Channel, exception.Message);
        }
    }
}
