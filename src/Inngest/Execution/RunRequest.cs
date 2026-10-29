// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Inngest.Execution;

// The body of a call from Inngest, read from the request document without copying it. The elements stay valid
// until the document is disposed at the end of the invocation.
internal sealed class RunRequest
{
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement;

    private JsonElement _stack;
    private JsonElement _internalFunctionId;
    private JsonElement _queueItemId;

    public JsonElement Event { get; private set; } = EmptyObject;

    // Every event of a batch; empty when the request carries only Event.
    public IReadOnlyList<JsonElement> Events { get; private set; } = [];

    public Dictionary<string, MemoEntry> Memo { get; private set; } = new(StringComparer.Ordinal);

    // Hashed ids of memoized results in the order they completed, which is the order they are handed back in.
    public List<string> Order { get; } = [];

    public string RunId { get; private init; } = string.Empty;

    public int Attempt { get; private init; }

    public int? MaxAttempts { get; private init; }

    public bool DisableImmediateExecution { get; private init; }

    // Inngest left the state out because it was too large; it has to be loaded from the API.
    public bool UseApi { get; private init; }

    // Inngest's own id of the function (ctx.fn_id) and the queue item being run (ctx.qi_id), which a checkpoint
    // names. Null when the server sent none. Read only when checkpointing applies, so other calls do not pay for them.
    public string? InternalFunctionId => NonEmptyString(_internalFunctionId);

    public string? QueueItemId => NonEmptyString(_queueItemId);

    public string EventName =>
        Event.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString()! : string.Empty;

    public static RunRequest Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The request body is not a JSON object.");
        }

        var context = Get(root, "ctx", JsonValueKind.Object);
        var request = new RunRequest
        {
            RunId = Get(context, "run_id", JsonValueKind.String) is { ValueKind: JsonValueKind.String } runId ? runId.GetString()! : string.Empty,
            Attempt = Int(context, "attempt") ?? 0,
            MaxAttempts = Int(context, "max_attempts"),
            DisableImmediateExecution = IsTrue(context, "disable_immediate_execution"),
            UseApi = IsTrue(context, "use_api") || IsTrue(root, "use_api"),
            _internalFunctionId = Get(context, "fn_id", JsonValueKind.String),
            _queueItemId = Get(context, "qi_id", JsonValueKind.String),
            _stack = Get(Get(context, "stack", JsonValueKind.Object), "stack", JsonValueKind.Array),
        };

        if (Get(root, "event", JsonValueKind.Object) is { ValueKind: JsonValueKind.Object } @event)
        {
            request.Event = @event;
        }

        request.LoadEvents(Get(root, "events", JsonValueKind.Array));
        request.LoadSteps(Get(root, "steps", JsonValueKind.Object));
        return request;
    }

    // Takes the events from an array; the first one becomes Event.
    public void LoadEvents(JsonElement events)
    {
        if (events.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var list = new JsonElement[events.GetArrayLength()];
        var count = 0;
        foreach (var item in events.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                list[count++] = item;
            }
        }

        Events = count == list.Length ? list : list[..count];
        if (count > 0)
        {
            Event = list[0];
        }
    }

    public void LoadSteps(JsonElement steps)
    {
        var isObject = steps.ValueKind == JsonValueKind.Object;
        Memo = new Dictionary<string, MemoEntry>(isObject ? steps.GetPropertyCount() : 0, StringComparer.Ordinal);
        if (isObject)
        {
            foreach (var step in steps.EnumerateObject())
            {
                Memo[step.Name] = MemoEntry.Parse(step.Value);
            }
        }

        Order.Clear();
        if (_stack.ValueKind == JsonValueKind.Array)
        {
            foreach (var id in _stack.EnumerateArray())
            {
                if (id.ValueKind == JsonValueKind.String)
                {
                    AddToOrder(id.GetString()!);
                }
            }
        }

        // Inngest lists every result in the stack. Results it left out are still handed back, after the others,
        // in the order of the keys, which Inngest sorts.
        foreach (var id in Memo.Keys)
        {
            AddToOrder(id);
        }
    }

    private void AddToOrder(string id)
    {
        ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(Memo, id);
        if (!Unsafe.IsNullRef(ref entry) && entry.IsResult && !entry.Ordered)
        {
            entry.Ordered = true;
            Order.Add(id);
        }
    }

    private static JsonElement Get(JsonElement element, string name, JsonValueKind kind) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == kind
            ? value
            : default;

    private static string? NonEmptyString(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private static bool IsTrue(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.True;

    private static int? Int(JsonElement element, string name) =>
        Get(element, name, JsonValueKind.Number) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number)
            ? number
            : null;
}
