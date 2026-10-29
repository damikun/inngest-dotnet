import { posix } from 'node:path';

// Turns a Markdown file from the repository's docs/ folder into a Starlight page: the first heading becomes the
// title, the first paragraph the description, and relative links point at the site's own copy of a document or
// at the file on GitHub.

export interface RepoDoc {
  file: string;
  slug: string;
  content: string;
}

export interface RepoDocOptions {
  // The base path the site is served from, such as "/inngest-dotnet/".
  base: string;
  // The repository on GitHub, such as "https://github.com/owner/repo".
  repoUrl: string;
  branch: string;
  // File names in docs/ that become pages, such as "design.md".
  docs: ReadonlySet<string>;
  // Whether a repository path (relative to the root) is a directory, to link it as a tree instead of a blob.
  isDirectory?: (path: string) => boolean;
}

// Pages are listed in this order; anything else follows in alphabetical order.
const ORDER = ['design.md', 'readiness.md', 'benchmarks.md'];

export function slugOf(file: string): string {
  return file
    .replace(/\.md$/i, '')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-|-$/g, '');
}

export function transformRepoDoc(file: string, source: string, options: RepoDocOptions): RepoDoc {
  const text = source.replace(/\r\n?/g, '\n').replace(/^﻿/, '');
  const { title, body } = extractTitle(text, file);
  const rewritten = rewriteLinks(body, options);
  const description = extractDescription(rewritten);
  const order = ORDER.indexOf(file);

  const frontmatter = [
    '---',
    `title: ${JSON.stringify(title)}`,
    ...(description ? [`description: ${JSON.stringify(description)}`] : []),
    `editUrl: ${JSON.stringify(`${options.repoUrl}/edit/${options.branch}/docs/${file}`)}`,
    'sidebar:',
    `  order: ${order < 0 ? 100 : order + 1}`,
    '---',
  ].join('\n');

  const sourceUrl = `${options.repoUrl}/blob/${options.branch}/docs/${file}`;
  const notice = `<p class="repo-doc-source">Rendered from <a href="${sourceUrl}"><code>docs/${file}</code></a> in the repository when the site was built.</p>`;

  return { file, slug: slugOf(file), content: `${frontmatter}\n\n${notice}\n\n${rewritten.trim()}\n` };
}

function extractTitle(text: string, file: string): { title: string; body: string } {
  const lines = text.split('\n');
  let inFence = false;
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (/^\s*(```|~~~)/.test(line)) inFence = !inFence;
    if (inFence) continue;
    const match = /^#\s+(.+?)\s*#*\s*$/.exec(line);
    if (match) {
      lines.splice(i, 1);
      return { title: plainText(match[1]), body: lines.join('\n') };
    }
    if (line.trim() !== '' && !line.startsWith('<!--')) break;
  }
  const name = file.replace(/\.md$/i, '').replace(/[-_]+/g, ' ');
  return { title: name.charAt(0).toUpperCase() + name.slice(1), body: text };
}

// The first paragraph of prose, without Markdown, cut at a sentence end where possible.
function extractDescription(body: string): string | undefined {
  const blocks = splitOutsideFences(body);
  const paragraph = blocks.find((block) => {
    const first = block.trimStart();
    return first !== '' && !/^(#|[-*+]\s|\d+\.\s|>|\||<|```|~~~|!\[)/.test(first);
  });
  if (!paragraph) return undefined;
  const text = plainText(paragraph);
  if (text.length <= 160) return text;
  const sentences = text.match(/[^.!?]+[.!?]+(\s|$)/g) ?? [];
  let description = '';
  for (const sentence of sentences) {
    if ((description + sentence).trim().length > 160) break;
    description += sentence;
  }
  if (description.trim()) return description.trim();
  return `${text.slice(0, 157).replace(/\s+\S*$/, '')}...`;
}

function splitOutsideFences(body: string): string[] {
  const blocks: string[] = [];
  let current: string[] = [];
  let inFence = false;
  for (const line of body.split('\n')) {
    if (/^\s*(```|~~~)/.test(line)) inFence = !inFence;
    if (!inFence && line.trim() === '') {
      if (current.length) blocks.push(current.join('\n'));
      current = [];
    } else {
      current.push(line);
    }
  }
  if (current.length) blocks.push(current.join('\n'));
  return blocks;
}

function plainText(markdown: string): string {
  return markdown
    .replace(/!\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/\[([^\]]*)\]\[[^\]]*\]/g, '$1')
    .replace(/`([^`]*)`/g, '$1')
    .replace(/(\*\*|__)(.*?)\1/g, '$2')
    .replace(/(\*|_)(.*?)\1/g, '$2')
    .replace(/<[^>]+>/g, '')
    .replace(/\s+/g, ' ')
    .trim();
}

// Rewrites the targets of inline links, images and reference definitions outside code.
export function rewriteLinks(body: string, options: RepoDocOptions): string {
  const out: string[] = [];
  let inFence = false;
  for (const line of body.split('\n')) {
    if (/^\s*(```|~~~)/.test(line)) {
      inFence = !inFence;
      out.push(line);
      continue;
    }
    if (inFence) {
      out.push(line);
      continue;
    }
    const definition = /^(\s{0,3}\[[^\]]+\]:\s*)(\S+)(.*)$/.exec(line);
    if (definition) {
      out.push(definition[1] + resolveTarget(definition[2], options) + definition[3]);
      continue;
    }
    // Inline code spans are masked so links inside them stay as written, while a link whose text is a code span,
    // such as [`load-average.txt`](results/load-average.txt), is still rewritten.
    const spans: string[] = [];
    const masked = line.replace(/(`+)[^`]*?\1/g, (span) => `\u0000${spans.push(span) - 1}\u0000`);
    const rewrittenLine = masked.replace(
      /(!?\[(?:[^\]\\]|\\.)*\]\()(\s*<?)([^)\s>]+)(>?(?:\s+"[^"]*")?\s*\))/g,
      (_, open, lead, target, close) => open + lead + resolveTarget(target, options) + close,
    );
    out.push(rewrittenLine.replace(/\u0000(\d+)\u0000/g, (_, index) => spans[Number(index)]));
  }
  return out.join('\n');
}

export function resolveTarget(target: string, options: RepoDocOptions): string {
  if (/^[a-z][a-z0-9+.-]*:/i.test(target) || target.startsWith('#') || target.startsWith('//')) return target;

  const hashIndex = target.indexOf('#');
  const path = hashIndex < 0 ? target : target.slice(0, hashIndex);
  const hash = hashIndex < 0 ? '' : target.slice(hashIndex);
  if (path === '') return target;

  const base = options.base.endsWith('/') ? options.base : `${options.base}/`;
  const resolved = path.startsWith('/') ? posix.normalize(path.slice(1)) : posix.normalize(posix.join('docs', path));
  if (resolved.startsWith('..')) return target;

  const inDocs = /^docs\/([^/]+\.md)$/i.exec(resolved);
  if (inDocs && options.docs.has(inDocs[1])) {
    return `${base}internals/${slugOf(inDocs[1])}/${hash}`;
  }

  const kind = options.isDirectory?.(resolved.replace(/\/$/, '')) ? 'tree' : 'blob';
  return `${options.repoUrl}/${kind}/${options.branch}/${resolved.replace(/\/$/, '')}${hash}`;
}
