// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using DotNet.Testcontainers.Configurations;

namespace Inngest.Testing.Containers;

// The connection string of an Inngest server is the address of its API.
internal sealed class InngestServerConnectionStringProvider : ContainerConnectionStringProvider<InngestServerContainer, InngestServerConfiguration>
{
    protected override string GetHostConnectionString() => Container.GetBaseAddress().AbsoluteUri;
}
