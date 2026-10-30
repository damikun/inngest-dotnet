// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Inngest.SourceGeneration.Compat;

// The generator's models hold arrays, and the incremental pipeline decides whether a step changed by comparing the
// models. ImmutableArray<T> compares by reference, so a model that held one would count as changed on every edit and
// the output would be emitted again each time. This compares the elements.
internal readonly struct EquatableArray<T>(ImmutableArray<T> items) : IEquatable<EquatableArray<T>>, IEnumerable<T>
{
    private readonly ImmutableArray<T> _items = items;

    public int Length => _items.IsDefault ? 0 : _items.Length;

    public T this[int index] => _items[index];

    public bool Equals(EquatableArray<T> other)
    {
        if (Length != other.Length)
        {
            return false;
        }

        var comparer = EqualityComparer<T>.Default;
        for (var i = 0; i < Length; i++)
        {
            if (!comparer.Equals(_items[i], other._items[i]))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        var comparer = EqualityComparer<T>.Default;
        var hash = 17;
        for (var i = 0; i < Length; i++)
        {
            hash = unchecked((hash * 31) + (_items[i] is { } item ? comparer.GetHashCode(item) : 0));
        }

        return hash;
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(_items.IsDefault ? ImmutableArray<T>.Empty : _items)).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public static bool operator ==(EquatableArray<T> left, EquatableArray<T> right) => left.Equals(right);

    public static bool operator !=(EquatableArray<T> left, EquatableArray<T> right) => !left.Equals(right);
}
