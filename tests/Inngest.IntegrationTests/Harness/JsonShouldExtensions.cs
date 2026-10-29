using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Inngest.IntegrationTests.Harness;

public static class JsonShouldExtensions
{
    // Passes when every property in expected is present in actual with an equal value, recursively. Properties only
    // actual has are ignored, since the server adds its own (ids, slugs, hashes); arrays must have the same length.
    public static void ShouldContainJson(this JsonElement actual, [StringSyntax(StringSyntaxAttribute.Json)] string expected)
    {
        using var document = JsonDocument.Parse(expected);
        if (FindMismatch(actual, document.RootElement, "$") is { } mismatch)
        {
            throw new ShouldAssertException($"{mismatch}\nActual JSON: {actual.GetRawText()}");
        }
    }

    private static string? FindMismatch(JsonElement actual, JsonElement expected, string path)
    {
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object when actual.ValueKind != JsonValueKind.Object:
                return $"{path} should be an object but is {actual.GetRawText()}";

            case JsonValueKind.Object:
                foreach (var property in expected.EnumerateObject())
                {
                    if (!actual.TryGetProperty(property.Name, out var value))
                    {
                        return $"{path}.{property.Name} is missing";
                    }

                    if (FindMismatch(value, property.Value, $"{path}.{property.Name}") is { } mismatch)
                    {
                        return mismatch;
                    }
                }

                return null;

            case JsonValueKind.Array when actual.ValueKind != JsonValueKind.Array || actual.GetArrayLength() != expected.GetArrayLength():
                return $"{path} should be an array of {expected.GetArrayLength()} items but is {actual.GetRawText()}";

            case JsonValueKind.Array:
                for (var i = 0; i < expected.GetArrayLength(); i++)
                {
                    if (FindMismatch(actual[i], expected[i], $"{path}[{i}]") is { } mismatch)
                    {
                        return mismatch;
                    }
                }

                return null;

            default:
                return JsonElement.DeepEquals(actual, expected)
                    ? null
                    : $"{path} should be {expected.GetRawText()} but is {actual.GetRawText()}";
        }
    }
}
