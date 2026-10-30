// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Globalization;

namespace Inngest.Steps;

// The ids of the steps a fan-out creates, "<id>[<index>]" for an item and "<id>[<first>-<last>]" for a chunk.
//
// The engine gives a repeated id a ":<n>" suffix, so an id like "<id>:<index>" could be taken by the second use of
// "<id>" in the same run. These ids end in "]", which that suffix never does, and a suffix only ever extends the id
// it repeats. Two ids of a fan-out are different strings for different indexes: the digits after the last "[" are
// the index, and what precedes them is the id the code passed. An item and a chunk differ in the "-". What is left is
// the code passing the same id to two fan-outs of a run, or a step id of its own that spells one of these, and the
// engine numbers those like any repeated id.
//
// A chunk's id has both ends in it so that a run that is replayed with another chunk size does not take the result
// of one chunk for another's.
internal static class FanOutIds
{
    public static string Item(string id, int index) => string.Create(CultureInfo.InvariantCulture, $"{id}[{index}]");

    public static string Chunk(string id, int first, int last) => string.Create(CultureInfo.InvariantCulture, $"{id}[{first}-{last}]");
}
