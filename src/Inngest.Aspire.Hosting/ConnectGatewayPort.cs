// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Net;
using System.Net.Sockets;

namespace Aspire.Hosting;

// The port of the Connect gateway. The server tells a worker to dial the host it called the API on and the port the
// gateway listens on, so the port must be the same on the host and in the container. Aspire only picks the host's port
// of a container endpoint (the container's port has to be known when the model is validated), so this picks both, from
// the range that Aspire allocates from when an endpoint has no port. Its top is below the ephemeral ports of Linux, which
// the kernel hands out as source ports to the connections that the server opens before it binds the gateway.
internal static class ConnectGatewayPort
{
    // The port the server listens on when it is not told otherwise, which is what a deployment gets.
    internal const int Default = 8289;

    private const int First = 10_000;
    private const int Last = 32_767;

    private static readonly HashSet<int> Picked = [];

    internal static int Pick()
    {
        lock (Picked)
        {
            // A free port is found in the first few tries unless the machine has thousands of listeners.
            for (var attempt = 0; attempt < 500; attempt++)
            {
                var port = Random.Shared.Next(First, Last + 1);
                if (!Picked.Contains(port) && IsFree(port))
                {
                    Picked.Add(port);
                    return port;
                }
            }
        }

        throw new InvalidOperationException($"No port between {First} and {Last} is free for the Connect gateway of the Inngest server. Pass connectPort to choose one.");
    }

    private static bool IsFree(int port)
    {
        try
        {
            // Docker publishes on the loopback address of the host, and a listener on any address blocks it as well.
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
