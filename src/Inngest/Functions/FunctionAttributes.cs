// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Reflection;

namespace Inngest.Functions;

// Turns the attributes of a function class into FunctionOptions: [InngestFunction] gives the id, and each
// FunctionOptionsAttribute then applies itself. Whether Inngest accepts the values is checked at startup by
// FunctionOptionsValidator, as for options written in code.
internal static class FunctionAttributes
{
    public static FunctionOptions Read(Type functionType)
    {
        var function = functionType.GetCustomAttribute<InngestFunctionAttribute>(inherit: false)
            ?? throw new InvalidOperationException(
                $"{functionType.Name} has no [InngestFunction] attribute. Add one with the function's id, or pass FunctionOptions to AddFunction<{functionType.Name}>.");

        var options = new FunctionOptions
        {
            Id = function.Id,
            Name = function.Name,
            Triggers = [],
            Retries = function.RetriesValue,
            Idempotency = function.Idempotency,
        };

        var attributes = functionType.GetCustomAttributes(inherit: false).OfType<FunctionOptionsAttribute>();

        // OrderBy is stable, so attributes with the same Order apply in the order they are declared.
        foreach (var attribute in attributes.OrderBy(static attribute => attribute.Order))
        {
            options = attribute.Apply(options);
        }

        return options;
    }
}
