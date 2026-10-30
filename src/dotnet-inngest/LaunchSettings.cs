// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Text.Json;

namespace Inngest.Tool;

// The URL of the app's Inngest endpoint, and the profile of the launch settings file it was read from.
internal sealed record AppUrl(string Url, string Profile, string File);

// The address of a project is in its Properties/launchSettings.json, which dotnet run uses too. That is where the dev
// server finds the app when -u is left out.
internal static class LaunchSettings
{
    // The path MapInngest serves at unless the app names another.
    private const string EndpointPath = "/api/inngest";

    private static readonly JsonDocumentOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    // The first profile that runs the project and has an http address decides, as it does for dotnet run. Https is not
    // used: the dev server is a Go program that does not trust the development certificate on every system.
    public static AppUrl? Find(string project)
    {
        var directory = File.Exists(project) ? Path.GetDirectoryName(Path.GetFullPath(project))! : project;
        var file = Path.Combine(directory, "Properties", "launchSettings.json");
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(file);
            using var document = JsonDocument.Parse(stream, Options);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("profiles", out var profiles)
                || profiles.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var profile in profiles.EnumerateObject())
            {
                if (profile.Value.ValueKind == JsonValueKind.Object
                    && Text(profile.Value, "commandName") is { } command && command.Equals("Project", StringComparison.OrdinalIgnoreCase)
                    && Text(profile.Value, "applicationUrl") is { } urls)
                {
                    foreach (var candidate in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (HttpOrigin(candidate) is { } origin)
                        {
                            return new AppUrl(origin + EndpointPath, profile.Name, file);
                        }
                    }
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // A file the tool cannot read is no reason not to start the dev server. It finds the app on its own.
        }

        return null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // Kestrel listens on a wildcard address, which is not one to call. The dev server runs on the same machine unless it
    // is in a container, and then -u says what it needs.
    private static string? HttpOrigin(string address)
    {
        foreach (var wildcard in new[] { "://*", "://+", "://0.0.0.0", "://[::]" })
        {
            address = address.Replace(wildcard, "://localhost", StringComparison.Ordinal);
        }

        return Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp
            ? uri.GetLeftPart(UriPartial.Authority)
            : null;
    }
}
