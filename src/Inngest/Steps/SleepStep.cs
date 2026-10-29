// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using Inngest.Execution;
using Inngest.Internal;

namespace Inngest.Steps;

// Inngest memoizes null once the sleep is over. The duration goes in both opts.duration, which the SDK spec
// asks for, and name, which older servers read.
internal sealed class SleepStep : FoundStep<object?>
{
    private static readonly JsonEncodedText DurationProperty = JsonEncodedText.Encode("duration");

    private readonly TimeSpan _duration;
    private readonly DateTimeOffset? _until;

    public SleepStep(string id, TimeSpan duration)
        : base(id)
    {
        _duration = duration;
    }

    public SleepStep(string id, DateTimeOffset until)
        : base(id)
    {
        _until = until;
    }

    public override StepType Type => StepType.Sleep;

    public override JsonEncodedText Op => Ops.Sleep;

    public override string? GetName() => _until is { } until ? TimeString.Format(until) : TimeString.Format(_duration);

    protected override void WriteOptions(Utf8JsonWriter writer, string? name, InngestSettings settings) =>
        writer.WriteString(DurationProperty, name);

    protected override object? Read(JsonElement value, JsonSerializerOptions options) => null;
}
