// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Buffers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Inngest.UserContext;

// What a user looks like in an event, and the rules around it. The event's user object holds one reserved key,
//
//   "_inngest_dotnet_user": { "v": 1, "t": "<protected text>" }
//
// and leaves every other key to the app. The protected text is the protector's output for this JSON, so everything in
// it is read and checked only after it has been unprotected:
//
//   { "v": 1, "iat": <unix s>, "exp": <unix s>, "ev": "<event name>", "nt": "<name claim type>", "rt": "<role claim type>",
//     "c": { "<claim type>": ["<value>", ...] } }
//
// "ev" ties the text to the event it was made for, so a payload that leaked from one event cannot be attached to
// another one to run as the user; "nt" and "rt" are left out when they are the .NET defaults. "v" is outside as well,
// so that a reader can tell a format it does not know from a payload it cannot unprotect. The codec is shared by every
// call and keeps nothing for one.
internal sealed class UserContextCodec
{
    public const string Key = "_inngest_dotnet_user";
    public const int Version = 1;

    // The latest second a DateTimeOffset can hold; a payload naming a later one is not valid.
    private const long MaxUnixSeconds = 253402300799;

    // The JSON never reaches a page, so characters are written as they are. \uXXXX escapes would use up the size bound.
    private static readonly JsonWriterOptions PlainText = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonDocumentOptions Reading = new() { MaxDepth = 8 };

    private readonly IUserContextProtector _protector;
    private readonly TimeProvider _time;
    private readonly HashSet<string> _allowed;
    private readonly long _lifetimeSeconds;
    private readonly int _maxSize;
    private readonly int _maxProtectedLength;

    public UserContextCodec(IOptions<UserContextOptions> options, IUserContextProtector protector, TimeProvider time)
    {
        var settings = options.Value;
        if (UserContextOptionsValidator.Check(settings) is { Count: > 0 } problems)
        {
            throw new InvalidOperationException(string.Join(' ', problems));
        }

        _protector = protector;
        _time = time;
        _allowed = new HashSet<string>(settings.Claims, StringComparer.Ordinal);
        _lifetimeSeconds = (long)settings.Lifetime.TotalSeconds;
        _maxSize = settings.MaxSize;

        // Room for what a protector adds, whatever its format: Data Protection needs a third of the size and about a
        // hundred bytes more.
        _maxProtectedLength = (settings.MaxSize * 2) + 1024;
    }

    // The allowed claims of a user, or null when there is no authenticated user or none of its claims is allowed. The
    // claims of all authenticated identities are taken, and the name and role types of the first.
    public UserSnapshot? Capture(ClaimsPrincipal? principal)
    {
        if (principal is null)
        {
            return null;
        }

        Dictionary<string, List<string>>? claims = null;
        string? nameType = null;
        string? roleType = null;
        foreach (var identity in principal.Identities)
        {
            if (!identity.IsAuthenticated)
            {
                continue;
            }

            nameType ??= identity.NameClaimType;
            roleType ??= identity.RoleClaimType;
            foreach (var claim in identity.Claims)
            {
                if (claim.Value.Length == 0 || !_allowed.Contains(claim.Type))
                {
                    continue;
                }

                claims ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);
                if (!claims.TryGetValue(claim.Type, out var values))
                {
                    claims.Add(claim.Type, values = []);
                }

                if (!values.Contains(claim.Value))
                {
                    values.Add(claim.Value);
                }
            }
        }

        if (claims is null)
        {
            return null;
        }

        // Whole seconds, which is what the payload keeps, so that what is read back equals what was taken.
        var issued = DateTimeOffset.FromUnixTimeSeconds(_time.GetUtcNow().ToUnixTimeSeconds());
        return new UserSnapshot(
            issued,
            issued.AddSeconds(_lifetimeSeconds),
            nameType == ClaimTypes.Name ? null : nameType,
            roleType == ClaimTypes.Role ? null : roleType,
            [.. claims.Select(static pair => new KeyValuePair<string, string[]>(pair.Key, [.. pair.Value]))]);
    }

    // The protected text of a snapshot for an event of this name, or null when the claims are larger than MaxSize.
    public string? Protect(UserSnapshot snapshot, string eventName)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, PlainText))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v"u8, Version);
            writer.WriteNumber("iat"u8, snapshot.IssuedAt.ToUnixTimeSeconds());
            writer.WriteNumber("exp"u8, snapshot.ExpiresAt.ToUnixTimeSeconds());
            writer.WriteString("ev"u8, eventName);
            if (snapshot.NameClaimType is not null)
            {
                writer.WriteString("nt"u8, snapshot.NameClaimType);
            }

            if (snapshot.RoleClaimType is not null)
            {
                writer.WriteString("rt"u8, snapshot.RoleClaimType);
            }

            writer.WriteStartObject("c"u8);
            foreach (var (type, values) in snapshot.Claims)
            {
                writer.WriteStartArray(type);
                foreach (var value in values)
                {
                    writer.WriteStringValue(value);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.WrittenCount > _maxSize ? null : _protector.Protect(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    // Reads the envelope of an event's user. reference is the time to judge the expiry by. Nothing of the claims is
    // trusted before the protector has vouched for the text.
    public UserRead Read(JsonElement envelope, string eventName, DateTimeOffset reference)
    {
        if (envelope.ValueKind != JsonValueKind.Object ||
            !envelope.TryGetProperty("v", out var version) ||
            version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var number) ||
            !envelope.TryGetProperty("t", out var text) ||
            text.ValueKind != JsonValueKind.String)
        {
            return UserRead.Failed(UserReadProblem.Malformed);
        }

        if (number != Version)
        {
            return UserRead.Failed(UserReadProblem.Unsupported);
        }

        var protectedText = text.GetString()!;
        if (protectedText.Length == 0 || protectedText.Length > _maxProtectedLength)
        {
            return UserRead.Failed(UserReadProblem.Malformed);
        }

        return _protector.TryUnprotect(protectedText, out var plaintext)
            ? Open(plaintext, eventName, reference)
            : UserRead.Failed(UserReadProblem.Unreadable);
    }

    private static UserRead Open(string plaintext, string eventName, DateTimeOffset reference)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(plaintext, Reading);
        }
        catch (JsonException)
        {
            return UserRead.Failed(UserReadProblem.Malformed);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryReadSeconds(root, "iat", out var issued) ||
                !TryReadSeconds(root, "exp", out var expires) ||
                expires < issued ||
                !root.TryGetProperty("v", out var inner) || inner.ValueKind != JsonValueKind.Number || !inner.TryGetInt32(out var innerVersion) || innerVersion != Version ||
                !root.TryGetProperty("ev", out var name) || name.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("c", out var claims) || claims.ValueKind != JsonValueKind.Object)
            {
                return UserRead.Failed(UserReadProblem.Malformed);
            }

            if (!string.Equals(name.GetString(), eventName, StringComparison.Ordinal))
            {
                return UserRead.Failed(UserReadProblem.WrongEvent);
            }

            if (reference.ToUnixTimeSeconds() > expires)
            {
                return UserRead.Failed(UserReadProblem.Expired);
            }

            if (!TryReadClaims(claims, out var read) ||
                !TryReadText(root, "nt", out var nameType) ||
                !TryReadText(root, "rt", out var roleType))
            {
                return UserRead.Failed(UserReadProblem.Malformed);
            }

            return UserRead.Succeeded(new UserSnapshot(
                DateTimeOffset.FromUnixTimeSeconds(issued),
                DateTimeOffset.FromUnixTimeSeconds(expires),
                nameType,
                roleType,
                read));
        }
    }

    private static bool TryReadSeconds(JsonElement root, string name, out long seconds)
    {
        seconds = 0;
        return root.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out seconds) &&
            seconds is >= 0 and <= MaxUnixSeconds;
    }

    // Absent is fine, a value that is not text is not.
    private static bool TryReadText(JsonElement root, string name, out string? text)
    {
        text = null;
        if (!root.TryGetProperty(name, out var value))
        {
            return true;
        }

        text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return !string.IsNullOrEmpty(text);
    }

    private static bool TryReadClaims(JsonElement claims, out KeyValuePair<string, string[]>[] read)
    {
        var list = new List<KeyValuePair<string, string[]>>();
        foreach (var claim in claims.EnumerateObject())
        {
            if (claim.Value.ValueKind != JsonValueKind.Array || claim.Value.GetArrayLength() == 0)
            {
                read = [];
                return false;
            }

            var values = new string[claim.Value.GetArrayLength()];
            var index = 0;
            foreach (var value in claim.Value.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } text)
                {
                    read = [];
                    return false;
                }

                values[index++] = text;
            }

            list.Add(new KeyValuePair<string, string[]>(claim.Name, values));
        }

        read = [.. list];
        return read.Length > 0;
    }
}

// Why a user in an event cannot be used. The text names the reason in the warning of a run, without the payload.
internal enum UserReadProblem
{
    None,
    Malformed,
    Unsupported,
    Unreadable,
    WrongEvent,
    Expired,
}

internal readonly record struct UserRead(UserSnapshot? Snapshot, UserReadProblem Problem)
{
    public static UserRead Succeeded(UserSnapshot snapshot) => new(snapshot, UserReadProblem.None);

    public static UserRead Failed(UserReadProblem problem) => new(null, problem);
}
