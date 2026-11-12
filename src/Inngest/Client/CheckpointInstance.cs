// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Inngest.Configuration;

namespace Inngest.Client;

// The instance id that checkpoint requests send in X-Inngest-Instance-Id, as inngest-js does: the id of the app's
// Connect worker once it has started, or else INNGEST_CONNECT_INSTANCE_ID. Null sends no header.
internal sealed class CheckpointInstance(InngestSettings settings)
{
    private volatile string? _connectInstanceId;

    public string? Id => _connectInstanceId ?? settings.ConnectInstanceId;

    // The Connect worker sets its id as it starts connecting, with the option, the variable and the host name applied.
    public void UseConnectInstanceId(string instanceId) => _connectInstanceId = instanceId;
}
