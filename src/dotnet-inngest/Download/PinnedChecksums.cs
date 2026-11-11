// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

namespace Inngest.Tool.Download;

// The SHA-256 of every file of the pinned release, copied from its checksums.txt. A download of that version is checked
// against these and never against the release itself, so replacing a file on GitHub, together with the checksums next to
// it, is caught.
internal static class PinnedChecksums
{
    public const string Text = """
        b0bae95aed285063280b24bf0252d3cbd03da261716817bedc35d11a7d62d32e  inngest_1.52.0_darwin_amd64.tar.gz
        ba2a454c1f606ee1dae24d3f5d27336bfc0ce78e6009aababdda431bce69800e  inngest_1.52.0_darwin_arm64.tar.gz
        9a4e96eb9b509da2840e9b1c7db35f651860dae45d0d757c324d59aa1415f3d1  inngest_1.52.0_linux_amd64.tar.gz
        f04b94caa0102280f231067a2fd94f2a99462496f3ef3a399a05d8097212ae2c  inngest_1.52.0_linux_arm64.tar.gz
        92c67bfcbad2fd142f7d7248884c9f4dc06127527208c0dccfdb43e0b84a343b  inngest_1.52.0_windows_amd64.zip
        ca18cf8fc02b26ea010f5146e1c5755798b12d0130b88ad7081b50bb21ddaae4  inngest_1.52.0_windows_arm64.zip
        """;

    public static ChecksumFile Checksums { get; } = ChecksumFile.Parse(Text);
}
