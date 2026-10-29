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
    private readonly FrozenDictionary<Type, string[]> _idsByType;

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
        var idsByType = new Dictionary<Type, List<string>>();
        for (var i = 0; i < functions.Length; i++)
        {
            var function = RegisteredFunction.Create(functions[i], settings.AppId);
            FunctionOptionsValidator.Validate(function.Options);

            if (!byCompositeId.TryAdd(function.CompositeId, functions[i]))
            {
                throw new InvalidOperationException(
                    $"Function \"{function.Options.Id}\" is registered more than once. Function ids must be unique within an app.");
            }

            // A class may be registered under several ids; only invoking it by its class then fails.
            if (functions[i].FunctionType is { } type)
            {
                if (!idsByType.TryGetValue(type, out var ids))
                {
                    idsByType.Add(type, ids = []);
                }

                ids.Add(function.Options.Id);
            }

            _registered[i] = function;
        }

        _byCompositeId = byCompositeId.ToFrozenDictionary(StringComparer.Ordinal);
        _idsByType = idsByType.ToFrozenDictionary(static pair => pair.Key, static pair => pair.Value.ToArray());
        Functions = Array.AsReadOnly(functions);
    }

    public IReadOnlyList<FunctionDefinition> Functions { get; }

    public bool TryGet(string compositeId, out FunctionDefinition function) =>
        _byCompositeId.TryGetValue(compositeId, out function!);

    public string FunctionIdOf(Type functionType)
    {
        ArgumentNullException.ThrowIfNull(functionType);

        if (!_idsByType.TryGetValue(functionType, out var ids))
        {
            throw new InvalidOperationException(
                $"No function is registered as {functionType.Name}. Register it with AddFunction<{functionType.Name}>().");
        }

        if (ids.Length > 1)
        {
            throw new InvalidOperationException(
                $"{functionType.Name} is registered as {string.Join(" and ", ids.Select(static id => $"\"{id}\""))}, so its class does not say which function to use. Use the function's id instead.");
        }

        return ids[0];
    }

    public void WriteRegistration(PooledBufferWriter writer, Uri serveUrl, string framework, string? deployType = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(serveUrl);
        ArgumentNullException.ThrowIfNull(framework);

        RegistrationWriter.Write(writer, _settings, _registered, serveUrl, framework, deployType);
    }
}
