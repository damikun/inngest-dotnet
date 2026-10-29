import { describe, expect, it } from 'vitest';
import { tokenizeCSharp, tokenizeJson } from './highlight.ts';

describe('tokenizer', () => {
  const csharp = `builder.Services
    .AddFunction<SyncAccount>(new FunctionOptions
    {
        Id = "sync-account", // a comment
        Triggers = [Trigger.Event("account/updated", @if: "event.data.plan == 'pro'")],
        Retries = 5,
    });`;
  const json = '{\n  "id": "shop-sync",\n  "retries": { "attempts": 5 },\n  "ok": true,\n  "n": -1.5e3\n}';

  it('keeps every character of C#', () => {
    expect(tokenizeCSharp(csharp).map((t) => t.text).join('')).toBe(csharp);
  });

  it('keeps every character of JSON', () => {
    expect(tokenizeJson(json).map((t) => t.text).join('')).toBe(json);
  });

  it('tells keys from string values', () => {
    const tokens = tokenizeJson('{"id": "a"}');
    expect(tokens.find((t) => t.text === '"id"')?.kind).toBe('property');
    expect(tokens.find((t) => t.text === '"a"')?.kind).toBe('string');
  });

  it('marks C# keywords, types, strings and comments', () => {
    const tokens = tokenizeCSharp('new Throttle(10, TimeSpan.FromMinutes(1)) // x\n"s"');
    expect(tokens.find((t) => t.text === 'new')?.kind).toBe('keyword');
    expect(tokens.find((t) => t.text === 'Throttle')?.kind).toBe('type');
    expect(tokens.find((t) => t.text === '10')?.kind).toBe('number');
    expect(tokens.find((t) => t.text === '// x')?.kind).toBe('comment');
    expect(tokens.find((t) => t.text === '"s"')?.kind).toBe('string');
  });
});
