// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

#if !NET9_0_OR_GREATER
namespace System;

// ReadOnlySpan<char>.Split(char) from .NET 9: enumerates the Range of every piece, empty ones included, like string.Split.
internal static class MemoryExtensionsPolyfills
{
    extension(ReadOnlySpan<char> source)
    {
        public SpanSplitEnumerator Split(char separator) => new(source, separator);
    }

    internal ref struct SpanSplitEnumerator
    {
        private readonly ReadOnlySpan<char> _source;
        private readonly char _separator;
        private int _start;
        private int _end;
        private int _next;

        public SpanSplitEnumerator(ReadOnlySpan<char> source, char separator)
        {
            _source = source;
            _separator = separator;
        }

        public readonly Range Current => new(_start, _end);

        public readonly SpanSplitEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            if (_next > _source.Length)
            {
                return false;
            }

            _start = _next;
            var length = _source[_start..].IndexOf(_separator);
            _end = length < 0 ? _source.Length : _start + length;
            _next = _end + 1;
            return true;
        }
    }
}
#endif
