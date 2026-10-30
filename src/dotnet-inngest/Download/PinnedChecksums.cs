// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

namespace Inngest.Tool.Download;

// The SHA-256 of every file of the pinned release, copied from its checksums.txt. A download of that version is checked
// against these and never against the release itself, so replacing a file on GitHub, together with the checksums next to
// it, is caught.
internal static class PinnedChecksums
{
    public const string Text = """
        190f0a3c433a73546cd346c4638ff9027a94af77ef581357de90715c6d739c70  inngest_1.49.0_darwin_amd64.tar.gz
        f60d2a9200a5a037f3e24fc7fc19df45200551510d7a81d77eed03cfe87f3e94  inngest_1.49.0_darwin_arm64.tar.gz
        d62cb047b8475bf8d2e6ccca757506890fb6f3bb68e5d15e0188012c5bd2cd6f  inngest_1.49.0_linux_amd64.tar.gz
        51370d9043f4fe5ec9755717b59fe9587984b93da7fe2dde3c2b28b63002afb7  inngest_1.49.0_linux_arm64.tar.gz
        b1eaf0806166085fcfb1a60393d1c0ed7cd137ae01ccb5b0c076971446a24e08  inngest_1.49.0_windows_amd64.zip
        12c750ad0292398ba532576952cd8551b5b72067806b0bb15449eed0c71b523c  inngest_1.49.0_windows_arm64.zip
        """;

    public static ChecksumFile Checksums { get; } = ChecksumFile.Parse(Text);
}
