namespace Inngest.Sample;

public interface IEmailSender
{
    /// <summary>Sends an email and returns the provider's message id.</summary>
    Task<string> SendAsync(string to, string subject, CancellationToken cancellationToken);
}

internal sealed partial class ConsoleEmailSender(ILogger<ConsoleEmailSender> logger) : IEmailSender
{
    public Task<string> SendAsync(string to, string subject, CancellationToken cancellationToken)
    {
        var messageId = Guid.NewGuid().ToString("N");
        LogSent(to, subject, messageId);
        return Task.FromResult(messageId);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sent \"{Subject}\" to {To} as {MessageId}")]
    private partial void LogSent(string to, string subject, string messageId);
}
