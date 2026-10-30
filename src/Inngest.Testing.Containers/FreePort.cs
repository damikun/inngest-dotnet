// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Net;
using System.Net.Sockets;

namespace Inngest.Testing.Containers;

// The Connect gateway is published on the same port inside the container and on the host, so that port is chosen before
// the container exists.
internal static class FreePort
{
    public static ushort Pick()
    {
        while (true)
        {
            // All interfaces, because Docker publishes the port on all of them.
            using var listener = new TcpListener(IPAddress.Any, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            if (!InngestBuilder.ServerPorts.Contains(port))
            {
                return (ushort)port;
            }
        }
    }
}
