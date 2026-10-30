// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Net;

namespace Inngest.Testing.Containers;

// How a server in a container reaches an app on the machine the test runs on. Inside the container localhost is the
// container itself, so a loopback URL becomes the name Docker maps to the host. Docker Desktop resolves it on its own;
// on Linux the builder maps it to the host gateway.
internal static class HostAccess
{
    public const string ContainerHostName = "host.docker.internal";

    public static Uri ForContainer(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri || url.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException($"The app URL must be an absolute http or https URL, not '{url}'.", nameof(url));
        }

        return IsHost(url) ? new UriBuilder(url) { Host = ContainerHostName }.Uri : url;
    }

    // An app that listens on all interfaces is reached on any of its addresses, so 0.0.0.0 and [::] stand for the host too.
    private static bool IsHost(Uri url) =>
        url.IsLoopback || (IPAddress.TryParse(url.DnsSafeHost, out var address) && (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)));
}
