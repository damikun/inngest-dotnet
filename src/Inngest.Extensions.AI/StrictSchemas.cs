// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Inngest.Extensions.AI;

// The JSON schemas that Microsoft.Extensions.AI produces describe what a .NET type can hold. OpenAI's strict mode takes a
// narrower dialect: no additional properties, every property required, and only some keywords. This changes a schema into
// it the way Microsoft's adapter for the OpenAI client does when strict is asked for, with the transformations of
// Microsoft.Extensions.AI, and with the keywords OpenAI does not support moved into the description, where the model can still
// read them. A schema is transformed once per function or format.
internal static class StrictSchemas
{
    // The keywords that the schemas of Microsoft.Extensions.AI can hold and that OpenAI's documentation of structured outputs
    // lists as unsupported, or as unsupported by some models, or by Azure OpenAI.
    private static readonly string[] Unsupported =
    [
        "contentEncoding", "contentMediaType", "not",
        "minLength", "maxLength", "pattern", "format", "minimum", "maximum", "multipleOf", "patternProperties", "minItems", "maxItems",
        "unevaluatedProperties", "propertyNames", "minProperties", "maxProperties",
        "unevaluatedItems", "contains", "minContains", "maxContains", "uniqueItems",
    ];

    private static readonly AIJsonSchemaTransformCache Cache = new(new AIJsonSchemaTransformOptions
    {
        DisallowAdditionalProperties = true,
        ConvertBooleanSchemas = true,
        MoveDefaultKeywordToDescription = true,
        RequireAllProperties = true,
        TransformSchemaNode = static (_, node) => MoveUnsupportedToDescription(node),
    });

    public static JsonElement Of(AIFunctionDeclaration function) => Cache.GetOrCreateTransformedSchema(function);

    public static JsonElement? Of(ChatResponseFormatJson format) => Cache.GetOrCreateTransformedSchema(format);

    private static JsonNode MoveUnsupportedToDescription(JsonNode node)
    {
        if (node is not JsonObject schema)
        {
            return node;
        }

        StringBuilder? moved = null;
        foreach (var keyword in Unsupported)
        {
            if (schema[keyword] is { } value)
            {
                schema.Remove(keyword);
                (moved ??= new StringBuilder()).Append(moved.Length > 0 ? "\n" : string.Empty).Append(keyword).Append(": ").Append(value.ToString());
            }
        }

        if (moved is not null)
        {
            schema["description"] = schema["description"] is { } description && description.GetValueKind() == JsonValueKind.String
                ? $"{description.GetValue<string>()}\n{moved}"
                : moved.ToString();
        }

        return schema;
    }
}
