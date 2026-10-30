import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { convert } from './convert.ts';
import { htmlUrl, siteLinks } from './links.ts';

// Every page of the docs, converted: none stops the conversion, and none keeps anything of MDX. The pages generated
// from docs/ are in the folder only after the site has been built or served once, so these are checked when they are.

const docs = fileURLToPath(new URL('../../content/docs', import.meta.url));

function files(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    return statSync(path).isDirectory() ? files(path) : [path];
  });
}

const pages = files(docs)
  .filter((file) => /\.mdx?$/.test(file))
  .map((file) => ({ file, id: relative(docs, file).replace(/\.mdx?$/, '').split(sep).join('/') }));
const links = siteLinks('https://example.com', '/docs', pages.map((page) => page.id));

// The text outside code, where MDX would show: C# generics such as `InvokeAsync<T>` are not JSX.
function prose(markdown: string): string {
  return markdown
    .replace(/^(\s*)(`{3,}|~{3,}).*\n[\s\S]*?\n\1\2[ \t]*$/gm, '')
    .replace(/(`+)[^`]*?\1/g, '');
}

// The body of every fenced code block, with the fence's indentation taken off.
function fences(text: string): string[] {
  const lines = text.split('\n');
  const found: string[] = [];
  for (let i = 0; i < lines.length; i++) {
    const open = /^(\s*)(`{3,}|~{3,})/.exec(lines[i]);
    if (!open) continue;
    const [, indent, fence] = open;
    const close = new RegExp(`^\\s*${fence[0] === '`' ? '`' : '~'}{${fence.length},}\\s*$`);
    const body: string[] = [];
    let j = i + 1;
    for (; j < lines.length && !close.test(lines[j]); j++) {
      body.push(lines[j].startsWith(indent) ? lines[j].slice(indent.length) : lines[j].trimStart());
    }
    found.push(body.join('\n').trimEnd());
    i = j;
  }
  return found;
}

describe.each(pages)('$id', ({ file, id }) => {
  const source = readFileSync(file, 'utf8').replace(/^---\n[\s\S]*?\n---\n/, '');
  const markdown = convert(source, { name: id, url: htmlUrl(links, id), links, mdx: file.endsWith('.mdx') });

  it('is plain Markdown', () => {
    const text = prose(markdown);
    expect(text).not.toMatch(/^\s*(import|export) .+ from /m);
    expect(text).not.toContain('{/*');
    expect(text).not.toMatch(/<\/?[A-Z][A-Za-z]*[\s/>]/);
    expect(text).not.toMatch(/\bclient:(load|visible|idle|only)\b/);
  });

  it('keeps every code block', () => {
    const kept = new Set(fences(markdown));
    expect(fences(source).filter((body) => !kept.has(body))).toEqual([]);
  });

  it('links only to absolute URLs', () => {
    for (const [, target] of prose(markdown).matchAll(/\]\(([^)\s]+)/g)) {
      expect(target).toMatch(/^(https?:\/\/|mailto:)/);
    }
  });
});
