using System.Net;
using System.Net.Sockets;

namespace Inngest.IntegrationTests.Harness;

// Ports are picked before anything starts, so the app and the Inngest server can be given each other's URLs up front.
public static class FreePorts
{
    // Fixed ports that other local stacks on developer machines listen on.
    private static readonly HashSet<int> Reserved = [5080, 5173, 8288, 15180];

    public static int[] Pick(int count)
    {
        var ports = new HashSet<int>();
        while (ports.Count < count)
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
            if (!Reserved.Contains(port))
            {
                ports.Add(port);
            }
        }

        return [.. ports];
    }
}
