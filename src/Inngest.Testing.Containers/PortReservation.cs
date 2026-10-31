// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Net;
using System.Net.Sockets;

namespace Inngest.Testing.Containers;

// The Connect gateway is published on the same port inside the container and on the host, so that port is chosen before
// the container exists. Pulling the image can take a while before Docker publishes it, and a port that was free when it
// was picked may not be by then, so the port stays taken from the moment it is picked until the container starts.
internal sealed class PortReservation : IDisposable
{
    private TcpListener? _listener;

    private PortReservation(TcpListener listener)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    public static PortReservation Take()
    {
        while (true)
        {
            // All interfaces, because Docker publishes the port on all of them.
            var listener = new TcpListener(IPAddress.Any, 0);
            listener.Start();
            var reservation = new PortReservation(listener);
            if (!InngestServerBuilder.ServerPorts.Contains(reservation.Port))
            {
                return reservation;
            }

            reservation.Dispose();
        }
    }

    // Lets Docker have the port. Releasing it again does nothing.
    public void Release() => Interlocked.Exchange(ref _listener, null)?.Stop();

    public void Dispose() => Release();
}
