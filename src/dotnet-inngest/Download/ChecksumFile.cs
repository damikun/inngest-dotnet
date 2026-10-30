// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Diagnostics.CodeAnalysis;

namespace Inngest.Tool.Download;

// A checksums.txt as GoReleaser writes it, which is the output of sha256sum: one line per file with the SHA-256 in
// hexadecimal, a space and either a second space or an asterisk, and the file name.
internal sealed class ChecksumFile
{
    private const int HashLength = 64;

    private readonly Dictionary<string, string> _hashes;

    private ChecksumFile(Dictionary<string, string> hashes) => _hashes = hashes;

    // A line that is not of that form fails the whole file instead of being skipped, so that a page of HTML or a half
    // download is not mistaken for a list with one file less.
    public static ChecksumFile Parse(string text)
    {
        Dictionary<string, string> hashes = new(StringComparer.Ordinal);
        var number = 0;
        foreach (var raw in text.Split('\n'))
        {
            number++;
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var name = line.Length > HashLength ? line[HashLength..].TrimStart(' ', '\t').TrimStart('*') : "";
            if (name.Length == 0 || line[HashLength] is not (' ' or '\t') || !IsHex(line.AsSpan(0, HashLength)))
            {
                throw new ToolException($"Line {number} of the checksums is not a SHA-256 followed by a file name.");
            }

            var hash = line[..HashLength].ToLowerInvariant();
            if (hashes.TryGetValue(name, out var known) && known != hash)
            {
                throw new ToolException($"The checksums list {name} twice with different hashes.");
            }

            hashes[name] = hash;
        }

        return new ChecksumFile(hashes);
    }

    public bool TryGet(string fileName, [NotNullWhen(true)] out string? sha256) => _hashes.TryGetValue(fileName, out sha256);

    private static bool IsHex(ReadOnlySpan<char> text)
    {
        foreach (var character in text)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
