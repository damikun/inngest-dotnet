// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace Inngest.Events;

// Sessions as inngest-js handles them. An event carries two layers under "meta": "sessions", which the code sets and
// which may hold null to drop an inherited session, and "propagated_sessions", which the SDK copies from the run
// that sends it. Inngest merges the two, the first winning per key, and records the result as the event's sessions.
internal static class EventSessions
{
    // The most sessions one event may carry, which Inngest enforces when it receives the event.
    private const int MaxPerEvent = 5;

    public static IReadOnlyDictionary<string, string> None { get; } = ReadOnlyDictionary<string, string>.Empty;

    // The sessions a run passes on: those every triggering event carries with the same id, the first five keys in
    // the UTF-8 byte order Inngest sorts them in.
    public static IReadOnlyDictionary<string, string> OfRun<TData>(IReadOnlyList<InngestEvent<TData>> events)
    {
        Dictionary<string, string>? shared = null;
        foreach (var @event in events)
        {
            var own = @event.Meta?.Sessions;
            if (shared is null)
            {
                shared = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (key, id) in own ?? ReadOnlyDictionary<string, string?>.Empty)
                {
                    if (key.Length > 0 && id is not null)
                    {
                        shared[key] = id;
                    }
                }
            }
            else
            {
                foreach (var key in shared.Keys.ToArray())
                {
                    if (own is null || !own.TryGetValue(key, out var id) || !string.Equals(id, shared[key], StringComparison.Ordinal))
                    {
                        shared.Remove(key);
                    }
                }
            }

            if (shared.Count == 0)
            {
                return None;
            }
        }

        if (shared is null)
        {
            return None;
        }

        var sessions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in shared.Keys.Order(Utf8Order.Instance).Take(MaxPerEvent))
        {
            sessions.Add(key, shared[key]);
        }

        return sessions.AsReadOnly();
    }

    // The run's sessions on each event that does not carry propagated sessions already; the events themselves are
    // left as they are.
    public static IReadOnlyList<InngestEvent> Propagate(IReadOnlyList<InngestEvent> events, IReadOnlyDictionary<string, string> sessions)
    {
        if (sessions.Count == 0)
        {
            return events;
        }

        InngestEvent[]? stamped = null;
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i] is not { } @event || @event.Meta is { InheritSessions: false } or { PropagatedSessions: not null })
            {
                continue;
            }

            stamped ??= [.. events];
            stamped[i] = @event with { Meta = Inherit(@event.Meta, sessions) };
        }

        return stamped ?? events;
    }

    // The meta of an event or invocation sent from a run: the run's sessions under any the code set itself.
    public static EventMeta? Inherit(EventMeta? meta, IReadOnlyDictionary<string, string> sessions) =>
        sessions.Count == 0 || meta is { InheritSessions: false } or { PropagatedSessions: not null }
            ? meta
            : (meta ?? new EventMeta()) with { PropagatedSessions = sessions };

    // Writes "meta" when there are sessions to send.
    public static void WriteMeta(Utf8JsonWriter writer, EventMeta? meta, string owner)
    {
        var sessions = meta?.Sessions is { Count: > 0 } own ? own : null;
        var propagated = meta is { InheritSessions: true, PropagatedSessions: { Count: > 0 } inherited } ? inherited : null;

        if (sessions is null && propagated is null)
        {
            return;
        }

        writer.WriteStartObject("meta"u8);
        if (sessions is not null)
        {
            writer.WriteStartObject("sessions"u8);
            foreach (var (key, id) in sessions)
            {
                CheckKey(key, owner);
                if (id is null)
                {
                    writer.WriteNull(key);
                }
                else
                {
                    CheckId(key, id, owner);
                    writer.WriteString(key, id);
                }
            }

            writer.WriteEndObject();
        }

        if (propagated is not null)
        {
            writer.WriteStartObject("propagated_sessions"u8);
            foreach (var (key, id) in propagated)
            {
                CheckKey(key, owner);
                CheckId(key, id, owner);
                writer.WriteString(key, id);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    // Reads the "meta" of a received event: the sessions Inngest recorded, ids that are numbers included.
    public static EventMeta? ReadMeta(JsonElement meta)
    {
        if (meta.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var sessions = meta.TryGetProperty("sessions", out var own) ? Read(own) : null;
        var propagated = meta.TryGetProperty("propagated_sessions", out var inherited) ? Read(inherited) : null;
        if (sessions is null && propagated is null)
        {
            return null;
        }

        return new EventMeta
        {
            Sessions = sessions,
            PropagatedSessions = propagated?.Where(static session => session.Value is not null)
                .ToDictionary(static session => session.Key, static session => session.Value!, StringComparer.Ordinal),
        };
    }

    private static Dictionary<string, string?>? Read(JsonElement sessions)
    {
        if (sessions.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var read = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var session in sessions.EnumerateObject())
        {
            read[session.Name] = session.Value.ValueKind switch
            {
                JsonValueKind.String => session.Value.GetString(),
                JsonValueKind.Number => session.Value.GetRawText(),
                _ => null,
            };
        }

        return read;
    }

    // Throws for what Inngest would refuse, before anything is sent. owner names the event or step in the message.
    public static void Check(EventMeta? meta, string owner)
    {
        foreach (var (key, id) in meta?.Sessions ?? ReadOnlyDictionary<string, string?>.Empty)
        {
            CheckKey(key, owner);
            if (id is not null)
            {
                CheckId(key, id, owner);
            }
        }

        foreach (var (key, id) in meta?.PropagatedSessions ?? None)
        {
            CheckKey(key, owner);
            CheckId(key, id, owner);
        }
    }

    // The sessions Inngest records for an event it receives: the code's own first, without those set to null, then
    // the inherited ones in UTF-8 byte order of their keys, five in all.
    public static IReadOnlyDictionary<string, string> Merge(EventMeta? meta)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        var own = meta?.Sessions ?? ReadOnlyDictionary<string, string?>.Empty;
        foreach (var (key, id) in own)
        {
            if (id is not null && merged.Count < MaxPerEvent)
            {
                merged[key] = id;
            }
        }

        if (meta is { InheritSessions: true, PropagatedSessions: { } inherited })
        {
            foreach (var key in inherited.Keys.Order(Utf8Order.Instance))
            {
                if (merged.Count < MaxPerEvent && !own.ContainsKey(key))
                {
                    merged[key] = inherited[key];
                }
            }
        }

        return merged.Count == 0 ? None : merged.AsReadOnly();
    }

    private static void CheckKey(string key, string owner)
    {
        if (string.IsNullOrEmpty(key))
        {
            throw new ArgumentException($"A session of {owner} has an empty key.");
        }
    }

    private static void CheckId(string key, string id, string owner)
    {
        if (id.Length == 0)
        {
            throw new ArgumentException($"Session \"{key}\" of {owner} has an empty id. Use null to drop an inherited session.");
        }
    }

    // Byte-wise over UTF-8, as Go compares strings; ordinal UTF-16 order differs outside the Basic Multilingual Plane.
    private sealed class Utf8Order : IComparer<string>
    {
        public static readonly Utf8Order Instance = new();

        public int Compare(string? x, string? y) =>
            Encoding.UTF8.GetBytes(x ?? string.Empty).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(y ?? string.Empty));
    }
}
