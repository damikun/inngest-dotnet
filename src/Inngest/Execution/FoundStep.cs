// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using Inngest.Errors;
using Inngest.Internal;
using Inngest.Middleware;
using Inngest.Steps;

namespace Inngest.Execution;

// A step the function asked for during this invocation. The engine reads and writes it only from its own logical
// thread, so nothing here is synchronized.
internal abstract class FoundStep(string id)
{
    private static readonly JsonEncodedText IdProperty = JsonEncodedText.Encode("id");
    private static readonly JsonEncodedText OpProperty = JsonEncodedText.Encode("op");
    private static readonly JsonEncodedText NameProperty = JsonEncodedText.Encode("name");
    private static readonly JsonEncodedText DisplayNameProperty = JsonEncodedText.Encode("displayName");
    private static readonly JsonEncodedText OptsProperty = JsonEncodedText.Encode("opts");
    private static readonly JsonEncodedText UserlandProperty = JsonEncodedText.Encode("userland");
    private static readonly JsonEncodedText IndexProperty = JsonEncodedText.Encode("index");
    private static readonly JsonEncodedText ParallelModeProperty = JsonEncodedText.Encode("parallelMode");
    private static readonly JsonEncodedText RaceMode = JsonEncodedText.Encode("race");

    // The display name the code gave the step, if any.
    private string? _name;

    // The id the code passed.
    public string Id { get; } = id;

    // What the Inngest UI shows for the step.
    public string DisplayName => _name ?? Id;

    // Read where the step is created, so that a step created inside a ParallelAsync callback gets its mode.
    public ParallelMode ParallelMode { get; private set; } = ParallelScope.Mode;

    // Id with the ":<n>" suffix of a repeated id, which is what gets hashed.
    public string FinalId { get; private set; } = id;

    public int? Index { get; private set; }

    public string HashedId { get; private set; } = string.Empty;

    public MemoEntry? Memo { get; private set; }

    public bool HasState => Memo.HasValue;

    public bool Fulfilled => Memo is { IsResult: true };

    // Found without a result, and not answered by middleware that skipped next(): Inngest has to carry it out.
    public bool IsNew => !HasState && Chain is not { IsCompleted: true };

    // The memoized result has been handed back, or is on its way.
    public bool Handled { get; set; }

    // Which decision window the step was reported in; -1 until it is reported.
    public int Window { get; set; } = -1;

    public Invocation Owner { get; set; } = null!;

    // Middleware whose WrapStepAsync was running where the step was created; the step skips them.
    public ActiveWrap? Guard { get; set; }

    // The WrapStepAsync chain whose result becomes the step's result.
    public Task<object?>? Chain { get; set; }

    public abstract StepType Type { get; }

    // The op reported when the step is found but not executed.
    public abstract JsonEncodedText Op { get; }

    // Run-type steps execute in the SDK; the others are carried out by Inngest.
    public virtual bool IsRunnable => false;

    public void Attach(string finalId, int? index, string hashedId, MemoEntry? memo)
    {
        FinalId = finalId;
        Index = index;
        HashedId = hashedId;
        Memo = memo;
    }

    // The options of a StepOptions overload. A mode given there wins over the mode of the callback.
    public void Apply(StepOptions options)
    {
        _name = string.IsNullOrEmpty(options.Name) ? null : options.Name;
        ParallelMode = options.ParallelMode ?? ParallelMode;
    }

    public StepInfo CreateInfo(bool memoized) => new(FinalId, HashedId, Type, memoized) { Name = _name };

    // The legacy "name" of the op: the id for run steps, the duration of a sleep, the event to wait for.
    public abstract string? GetName();

    // Writes the properties of the op's opts object; the base class writes the object around them.
    protected abstract void WriteOptions(Utf8JsonWriter writer, string? name, InngestSettings settings);

    // Writes the op up to and including "userland"; the caller adds the result and closes the object.
    public void WriteOpStart(Utf8JsonWriter writer, JsonEncodedText op, InngestSettings settings)
    {
        var name = GetName();
        writer.WriteStartObject();
        writer.WriteString(IdProperty, HashedId);
        writer.WriteString(OpProperty, op);
        if (name is not null)
        {
            writer.WriteString(NameProperty, name);
        }

        writer.WriteString(DisplayNameProperty, DisplayName);
        writer.WriteStartObject(OptsProperty);
        WriteOptions(writer, name, settings);

        // Inngest calls the function again after each step of a race finishes, not once all have.
        if (ParallelMode == ParallelMode.Race)
        {
            writer.WriteString(ParallelModeProperty, RaceMode);
        }

        writer.WriteEndObject();
        writer.WriteStartObject(UserlandProperty);
        writer.WriteString(IdProperty, Id);
        if (Index is { } index)
        {
            writer.WriteNumber(IndexProperty, index);
        }

        writer.WriteEndObject();
    }

    public virtual Task<object?> ExecuteAsync(FunctionContext context, IServiceProvider services, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"Step \"{Id}\" is carried out by Inngest, not by the SDK.");

    public virtual void WriteOutput(Utf8JsonWriter writer, object? output, JsonSerializerOptions options)
    {
        if (output is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            JsonSerializer.Serialize(writer, output, options.GetTypeInfo(output.GetType()));
        }
    }

    // Writes the output as Inngest stores it: as WriteOutput writes it, then rewritten by the payload transforms of
    // the call's middleware when it has any. Everything that sends a step's output to Inngest writes it through this.
    public void WriteData(Utf8JsonWriter writer, object? output, JsonSerializerOptions options)
    {
        if (Owner.Transforms is { } transforms)
        {
            transforms.WriteStepOutput(writer, this, output);
        }
        else
        {
            WriteOutput(writer, output, options);
        }
    }

    // Hands the memoized result to the function directly.
    public abstract void Resolve(JsonSerializerOptions options);

    // The memoized result as WrapStepAsync middleware sees it.
    public abstract Task<object?> ReadMemoAsync(JsonSerializerOptions options);

    // Hands the result of the WrapStepAsync chain to the function.
    public abstract void CompleteFromChain();

    protected StepFailedException CreateFailure(JsonElement error) => new(FinalId, ErrorSerializer.Read(error));
}

// The task returned to the function completes only when the step's memoized result is handed back. In an
// invocation that discovers or executes the step it never completes, and the function is abandoned there.
internal abstract class FoundStep<T>(string id) : FoundStep(id)
{
    // Continuations run inline, on the scheduler, because the engine completes this only from there.
    private readonly TaskCompletionSource<T> _completion = new();

    public Task<T> Completion => _completion.Task;

    public sealed override void Resolve(JsonSerializerOptions options)
    {
        var memo = Memo.GetValueOrDefault();
        if (memo.Kind == MemoKind.Error)
        {
            _completion.TrySetException(CreateFailure(memo.Value));
            return;
        }

        T value;
        try
        {
            value = ReadData(memo.Value, options);
        }
        catch (Exception exception)
        {
            _completion.TrySetException(exception);
            return;
        }

        _completion.TrySetResult(value);
    }

    public sealed override Task<object?> ReadMemoAsync(JsonSerializerOptions options)
    {
        var memo = Memo.GetValueOrDefault();
        if (memo.Kind == MemoKind.Error)
        {
            return Task.FromException<object?>(CreateFailure(memo.Value));
        }

        try
        {
            return Task.FromResult<object?>(ReadData(memo.Value, options));
        }
        catch (Exception exception)
        {
            return Task.FromException<object?>(exception);
        }
    }

    public sealed override void CompleteFromChain()
    {
        var chain = Chain!;
        if (chain.IsCompletedSuccessfully)
        {
            switch (chain.Result)
            {
                case T value:
                    _completion.TrySetResult(value);
                    break;
                case null when default(T) is null:
                    _completion.TrySetResult(default!);
                    break;
                case var other:
                    _completion.TrySetException(new InvalidCastException(
                        $"Middleware returned {other?.GetType().Name ?? "null"} for step \"{FinalId}\", which returns {typeof(T).Name}."));
                    break;
            }
        }
        else if (chain.IsFaulted)
        {
            _completion.TrySetException(chain.Exception!.InnerExceptions);
        }
        else
        {
            _completion.TrySetCanceled();
        }
    }

    protected abstract T Read(JsonElement value, JsonSerializerOptions options);

    // Reads the memoized result once the payload transforms of the call's middleware, when it has any, have
    // rewritten it. Read copies what it needs, so the rewritten JSON is released right away.
    private T ReadData(JsonElement value, JsonSerializerOptions options)
    {
        if (Owner.Transforms is not { } transforms)
        {
            return Read(value, options);
        }

        using var transformed = transforms.TransformStepResult(CreateInfo(memoized: true), value);
        return Read(transformed.Value, options);
    }
}
