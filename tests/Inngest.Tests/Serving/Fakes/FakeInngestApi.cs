using System.Text;
using System.Text.Json;
using Inngest.Client;

namespace Inngest.Tests.Serving.Fakes;

internal sealed class FakeInngestApi : IInngestApi
{
    public List<Registration> Registrations { get; } = [];

    public RegisterResult Result { get; set; } = new(Success: true, StatusCode: 200, Modified: true, Error: null);

    public Exception? Failure { get; set; }

    // The token the latest registration was given.
    public CancellationToken RegisterToken { get; private set; }

    public Task<RegisterResult> RegisterAsync(ReadOnlyMemory<byte> payload, string? deployId, string? serverKind, CancellationToken cancellationToken)
    {
        RegisterToken = cancellationToken;
        Registrations.Add(new Registration(Encoding.UTF8.GetString(payload.Span), deployId, serverKind));
        return Failure is null ? Task.FromResult(Result) : Task.FromException<RegisterResult>(Failure);
    }

    public Task<JsonDocument> GetRunStepsAsync(string runId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<JsonDocument> GetRunEventsAsync(string runId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public sealed record Registration(string Payload, string? DeployId, string? ServerKind);
}
