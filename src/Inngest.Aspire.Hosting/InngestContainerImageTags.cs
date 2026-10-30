// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

namespace Aspire.Hosting;

// The image of the Inngest server. The tag is the release of the Inngest CLI that the integration tests of this
// repository run against; WithImageTag on the resource builder replaces it.
internal static class InngestContainerImageTags
{
    public const string Registry = "docker.io";

    public const string Image = "inngest/inngest";

    public const string Tag = "v1.49.0";
}
