// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Collections.Frozen;
using Inngest.Functions;
using Inngest.Internal;

namespace Inngest.Registration;

// The app's functions, fixed at startup. Lookups on the request path read a frozen dictionary and take no locks.
// The sync body is written once per sync, so writing it favors clarity over speed.
internal sealed class FunctionRegistry : IFunctionRegistry
{
    private readonly InngestSettings _settings;
    private readonly RegisteredFunction[] _registered;
    private readonly FrozenDictionary<string, FunctionDefinition> _byCompositeId;

    // Throws InvalidOperationException, naming the function, for options Inngest would reject and for ids that are
    // empty or used twice.
    public FunctionRegistry(IEnumerable<FunctionDefinition> definitions, InngestSettings settings)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;

        var functions = definitions.ToArray();
        _registered = new RegisteredFunction[functions.Length];

        var byCompositeId = new Dictionary<string, FunctionDefinition>(functions.Length, StringComparer.Ordinal);
        for (var i = 0; i < functions.Length; i++)
        {
            var function = RegisteredFunction.Create(functions[i], settings.AppId);
            FunctionOptionsValidator.Validate(function.Options);

            if (!byCompositeId.TryAdd(function.CompositeId, functions[i]))
            {
                throw new InvalidOperationException(
                    $"Function \"{function.Options.Id}\" is registered more than once. Function ids must be unique within an app.");
            }

            _registered[i] = function;
        }

        _byCompositeId = byCompositeId.ToFrozenDictionary(StringComparer.Ordinal);
        Functions = Array.AsReadOnly(functions);
    }

    public IReadOnlyList<FunctionDefinition> Functions { get; }

    public bool TryGet(string compositeId, out FunctionDefinition function) =>
        _byCompositeId.TryGetValue(compositeId, out function!);

    public void WriteRegistration(PooledBufferWriter writer, Uri serveUrl, string framework, string? deployType = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(serveUrl);
        ArgumentNullException.ThrowIfNull(framework);

        RegistrationWriter.Write(writer, _settings, _registered, serveUrl, framework, deployType);
    }
}
