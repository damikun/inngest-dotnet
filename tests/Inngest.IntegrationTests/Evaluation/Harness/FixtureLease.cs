using Inngest.IntegrationTests.AI;
using Inngest.IntegrationTests.Checkpointing;
using Inngest.IntegrationTests.Core;
using Inngest.IntegrationTests.Execution;
using Inngest.IntegrationTests.Features;
using Inngest.IntegrationTests.Gateway;
using Inngest.IntegrationTests.Outbox;
using Inngest.IntegrationTests.Rest;
using Inngest.IntegrationTests.Steps;
using Inngest.IntegrationTests.Webhooks;

namespace Inngest.IntegrationTests.Evaluation.Harness;

// One server for the classes in a collection. It starts before they run and the container is removed when they finish,
// so a finished server is not one more client of Docker Desktop's host gateway.
public sealed class FixtureLease<TFixture> : IAsyncLifetime
    where TFixture : class, IEvaluationFixture, new()
{
    private IEvaluationFixture? _fixture;

    public async ValueTask InitializeAsync()
    {
        var fixture = new TFixture();
        await fixture.InitializeAsync();
        _fixture = fixture;
        EvaluationEnvironment.Hold(fixture);
    }

    public async ValueTask DisposeAsync()
    {
        if (_fixture is not null)
        {
            await EvaluationEnvironment.Release(_fixture);
        }
    }
}

public static class EvaluationCollections
{
    public const string Dev = "Evaluation dev";

    public const string Features = "Evaluation features";

    public const string Steps = "Evaluation steps";

    public const string Runs = "Evaluation runs";

    public const string Cloud = "Evaluation cloud";

    public const string Rotation = "Evaluation rotation";

    public const string Chat = "Evaluation chat";

    public const string Checkpointing = "Evaluation checkpointing";

    public const string Execution = "Evaluation execution";

    public const string EncryptedExecution = "Evaluation encrypted execution";

    public const string Gateway = "Evaluation gateway";

    public const string Middleware = "Evaluation middleware";

    public const string Outbox = "Evaluation outbox";

    public const string Core = "Evaluation core";

    public const string Model = "Evaluation model";

    public const string Webhooks = "Evaluation webhooks";

    public const string Connect = "Evaluation connect";

    public const string CloudConnect = "Evaluation cloud connect";
}

[CollectionDefinition(EvaluationCollections.Dev)]
public sealed class EvaluationDevCollection : ICollectionFixture<FixtureLease<EvaluationDevFixture>>
{
}

[CollectionDefinition(EvaluationCollections.Features)]
public sealed class FeaturesCollection : ICollectionFixture<FixtureLease<FeaturesFixture>>
{
}

[CollectionDefinition(EvaluationCollections.Steps)]
public sealed class StepsCollection : ICollectionFixture<FixtureLease<StepsFixture>>
{
}

[CollectionDefinition(EvaluationCollections.Runs)]
public sealed class RunsCollection : ICollectionFixture<FixtureLease<RunsFixture>>
{
}

[CollectionDefinition(EvaluationCollections.Cloud)]
public sealed class CloudCollection : ICollectionFixture<FixtureLease<CloudFixture>>
{
}

[CollectionDefinition(EvaluationCollections.Rotation)]
public sealed class RotationCollection : ICollectionFixture<FixtureLease<RotationFixture>>
{
}

[CollectionDefinition(EvaluationCollections.Chat)]
public sealed class ChatCollection : ICollectionFixture<FixtureLease<ChatFixture>>
{
}

[CollectionDefinition(EvaluationCollections.Checkpointing)]
public sealed class CheckpointingCollection : ICollectionFixture<FixtureLease<CheckpointingFixture>>
{
}

[CollectionDefinition(EvaluationCollections.Execution)]
public sealed class ExecutionCollection : ICollectionFixture<FixtureLease<ExecutionFixture>>
{
}

[CollectionDefinition(EvaluationCollections.EncryptedExecution)]
public sealed class EncryptedExecutionCollection : ICollectionFixture<FixtureLease<EncryptedExecutionFixture>>
{
}

[CollectionDefinition(EvaluationCollections.Gateway)]
public sealed class GatewayCollection : ICollectionFixture<FixtureLease<GatewayFixture>>
{
}

[CollectionDefinition(EvaluationCollections.Middleware)]
public sealed class MiddlewareCollection : ICollectionFixture<FixtureLease<MiddlewareFixture>>
{
}

[CollectionDefinition(EvaluationCollections.Outbox)]
public sealed class OutboxCollection : ICollectionFixture<FixtureLease<OutboxFixture>>
{
}

[CollectionDefinition(EvaluationCollections.Core)]
public sealed class CoreCollection : ICollectionFixture<FixtureLease<CoreFixture>>
{
}

[CollectionDefinition(EvaluationCollections.Model)]
public sealed class ModelCollection : ICollectionFixture<FixtureLease<ModelFixture>>
{
}

[CollectionDefinition(EvaluationCollections.Webhooks)]
public sealed class WebhooksCollection : ICollectionFixture<FixtureLease<WebhooksFixture>>
{
}

[CollectionDefinition(EvaluationCollections.Connect)]
public sealed class ConnectEvaluationCollection : ICollectionFixture<FixtureLease<EvaluationConnectFixture>>
{
}

[CollectionDefinition(EvaluationCollections.CloudConnect)]
public sealed class CloudConnectCollection : ICollectionFixture<FixtureLease<CloudConnectFixture>>
{
}
