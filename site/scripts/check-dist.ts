// Checks the built site in dist/: every same-site link, asset and fragment resolves, every page has the metadata
// the site promises, and every URL in the sitemap exists. starlight-links-validator checks the links written in
// Markdown; this also covers components, generated pages and head tags. It also checks code blocks: every fenced
// block in the docs renders as one block with all of its lines, and no shell command continues onto a second line,
// which breaks when pasted into PowerShell and in readers that turn the page into Markdown. And it checks the files
// for tools: every page has a Markdown copy with nothing of MDX left in it and with links that resolve, llms.txt
// lists every copy, and llms-full.txt holds them.
//
//   node scripts/check-dist.ts

import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { Parser } from 'htmlparser2';

const SITE = 'https://damikun.github.io';
const BASE = '/inngest-dotnet/';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const dist = join(root, 'dist');
const docs = join(root, 'src', 'content', 'docs');
const problems: string[] = [];

function files(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    return statSync(path).isDirectory() ? files(path) : [path];
  });
}

// The file a URL path of the site is served from, or undefined when there is none.
function target(pathname: string): string | undefined {
  if (!pathname.startsWith(BASE)) return undefined;
  const local = join(dist, decodeURIComponent(pathname.slice(BASE.length)));
  for (const candidate of [local, join(local, 'index.html')]) {
    if (existsSync(candidate) && statSync(candidate).isFile()) return candidate;
  }
  return undefined;
}

interface Page {
  ids: Set<string>;
  links: { url: string; attribute: string }[];
  meta: Map<string, string>;
  title: string;
  jsonLd: number;
}

const pages = new Map<string, Page>();
function parse(file: string): Page {
  const cached = pages.get(file);
  if (cached) return cached;
  const page: Page = { ids: new Set(), links: [], meta: new Map(), title: '', jsonLd: 0 };
  let inTitle = false;
  const parser = new Parser({
    onopentag(name, attributes) {
      if (attributes.id) page.ids.add(attributes.id);
      if (name === 'a' && attributes.name) page.ids.add(attributes.name);
      for (const attribute of ['href', 'src']) {
        if (attributes[attribute] !== undefined) page.links.push({ url: attributes[attribute], attribute });
      }
      if (name === 'meta') {
        const key = attributes.property ?? attributes.name;
        if (key && attributes.content !== undefined) page.meta.set(key, attributes.content);
      }
      if (name === 'link' && attributes.rel === 'canonical') page.meta.set('canonical', attributes.href ?? '');
      if (name === 'script' && attributes.type === 'application/ld+json') page.jsonLd++;
      inTitle = name === 'title';
    },
    ontext(text) {
      if (inTitle) page.title += text;
    },
    onclosetag() {
      inTitle = false;
    },
  });
  parser.write(readFileSync(file, 'utf8'));
  parser.end();
  pages.set(file, page);
  return page;
}

function checkUrl(url: string, from: string, what: string) {
  if (/^(mailto:|tel:|javascript:|data:)/.test(url)) return;
  let resolved: URL;
  try {
    resolved = new URL(url, `${SITE}${BASE}${relative(dist, from).split(sep).join('/')}`);
  } catch {
    problems.push(`${relative(dist, from)}: ${what} "${url}" is not a URL`);
    return;
  }
  if (resolved.origin !== SITE) return;
  const file = target(resolved.pathname);
  if (!file) {
    problems.push(`${relative(dist, from)}: ${what} "${url}" points at nothing`);
    return;
  }
  const fragment = decodeURIComponent(resolved.hash.slice(1));
  if (fragment && fragment !== '_top' && file.endsWith('.html') && !parse(file).ids.has(fragment)) {
    problems.push(`${relative(dist, from)}: ${what} "${url}" has no #${fragment} on its page`);
  }
}

const html = files(dist).filter((file) => file.endsWith('.html'));
for (const file of html) {
  const page = parse(file);
  for (const { url, attribute } of page.links) checkUrl(url, file, attribute);

  const name = relative(dist, file);
  if (name === '404.html') continue;
  if (!page.title.trim()) problems.push(`${name}: no <title>`);
  for (const key of ['description', 'canonical', 'og:title', 'og:description', 'og:image', 'twitter:card', 'twitter:image']) {
    const value = page.meta.get(key);
    if (!value) problems.push(`${name}: no ${key}`);
    else if (['canonical', 'og:image', 'twitter:image'].includes(key)) checkUrl(value, file, key);
  }
  if (page.jsonLd !== 1) problems.push(`${name}: ${page.jsonLd} JSON-LD blocks instead of 1`);
}

// The text of every <pre> on a page, one entry per block, with Expressive Code's line elements turned into lines.
function codeBlocks(file: string): { language: string; lines: string[] }[] {
  const blocks: { language: string; lines: string[] }[] = [];
  let current: { language: string; lines: string[] } | undefined;
  const parser = new Parser({
    onopentag(name, attributes) {
      if (name === 'pre') {
        current = { language: attributes['data-language'] ?? '', lines: [''] };
        blocks.push(current);
      } else if (current && name === 'div' && attributes.class?.split(' ').includes('ec-line')) {
        current.lines.push('');
      }
    },
    ontext(text) {
      if (current) current.lines[current.lines.length - 1] += text;
    },
    onclosetag(name) {
      if (name === 'pre') current = undefined;
    },
  });
  parser.write(readFileSync(file, 'utf8'));
  parser.end();
  // Expressive Code puts each line in an element of its own, and a newline inside an empty one.
  return blocks.map((block) => ({
    ...block,
    lines: tidy(block.lines.length > 1 ? block.lines.map((line) => line.replace(/\n/g, '')) : block.lines[0].split('\n')),
  }));
}

// Lines without trailing spaces and without empty lines at either end.
function tidy(lines: string[]): string[] {
  const trimmed = lines.map((line) => line.trimEnd());
  while (trimmed.length > 0 && trimmed[0] === '') trimmed.shift();
  while (trimmed.length > 0 && trimmed.at(-1) === '') trimmed.pop();
  return trimmed;
}

// The fenced code blocks of a Markdown or MDX file, with the fence's indentation taken off every line.
function fences(source: string): { line: number; lines: string[] }[] {
  const lines = source.split('\n');
  const found: { line: number; lines: string[] }[] = [];
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
    found.push({ line: i + 1, lines: tidy(body) });
    i = j;
  }
  return found;
}

const shells = new Set(['bash', 'sh', 'shell', 'zsh', 'console', 'powershell', 'ps1', 'pwsh']);
for (const source of files(docs).filter((file) => /\.mdx?$/.test(file))) {
  const slug = relative(docs, source).replace(/\.mdx?$/, '').split(sep).join('/');
  const page = join(dist, slug === 'index' ? '' : slug, 'index.html');
  if (!existsSync(page)) continue;
  const rendered = codeBlocks(page);
  const renderedText = new Set(rendered.map((block) => block.lines.join('\n')));
  const name = relative(root, source);
  for (const fence of fences(readFileSync(source, 'utf8'))) {
    if (!renderedText.has(fence.lines.join('\n'))) {
      problems.push(`${name}:${fence.line}: the code block does not render as one block with all of its lines`);
    }
  }
  // The internals pages mirror the repository's docs/ folder and keep its formatting.
  if (slug.startsWith('internals/')) continue;
  for (const block of rendered.filter((block) => shells.has(block.language))) {
    const split = block.lines.find((line) => /(\\|`)$/.test(line));
    if (split) problems.push(`${relative(dist, page)}: "${split}" continues onto the next line; write one line and let it wrap`);
  }
}

for (const sitemap of files(dist).filter((file) => /sitemap-\d+\.xml$/.test(file))) {
  for (const [, url] of readFileSync(sitemap, 'utf8').matchAll(/<loc>([^<]+)<\/loc>/g)) {
    checkUrl(url, sitemap, 'sitemap entry');
  }
}

// The text of a Markdown file outside code, where links and leftovers of MDX show. A fence's indentation is spaces and
// tabs only: with \s, a blank line before a fence would pair it with a later fence and leave code in the text.
function outsideCode(markdown: string): string {
  return markdown.replace(/^([ \t]*)(`{3,}|~{3,}).*\n[\s\S]*?\n\1\2[ \t]*$/gm, '').replace(/(`+)[^`]*?\1/g, '');
}

// The HTML page that a Markdown copy is next to: guides/steps.md is next to guides/steps/index.html.
function pageOf(copy: string): string {
  return join(dist, copy === 'index.md' ? 'index.html' : join(copy.replace(/\.md$/, ''), 'index.html'));
}

// Where the copy of an HTML page is: guides/steps/index.html has guides/steps.md, and the home page has index.md.
function copyOf(name: string): string {
  const folder = name.replace(/(^|[\\/])index\.html$/, '');
  return `${folder === '' ? 'index' : folder}.md`;
}

const copies = files(dist).filter((file) => file.endsWith('.md'));
for (const file of html) {
  const name = relative(dist, file);
  if (name !== '404.html' && !existsSync(join(dist, copyOf(name)))) problems.push(`${name}: no Markdown copy at ${copyOf(name)}`);
}

const llms = join(dist, 'llms.txt');
const full = join(dist, 'llms-full.txt');
const listed = new Map<string, number>();
if (!existsSync(llms)) problems.push('llms.txt: missing');
else {
  const text = readFileSync(llms, 'utf8');
  if (!/^# \S.*\n\n> \S/.test(text)) problems.push('llms.txt: does not start with a title and a summary');
  for (const [, url] of text.matchAll(/^- \[[^\]]+\]\(([^)]+)\)/gm)) {
    checkUrl(url, llms, 'link');
    listed.set(url, (listed.get(url) ?? 0) + 1);
  }
}
for (const copy of copies) {
  const url = `${SITE}${BASE}${relative(dist, copy).split(sep).join('/')}`;
  if (existsSync(llms) && listed.get(url) !== 1) problems.push(`llms.txt: lists ${url} ${listed.get(url) ?? 0} times instead of once`);
}
if (!existsSync(full)) problems.push('llms-full.txt: missing');
const everything = existsSync(full) ? readFileSync(full, 'utf8') : '';

for (const copy of copies) {
  const name = relative(dist, copy);
  const markdown = readFileSync(copy, 'utf8');
  if (!/^# \S/.test(markdown)) problems.push(`${name}: does not start with a title`);
  if (existsSync(full) && !everything.includes(markdown.trim())) problems.push(`${name}: is not in llms-full.txt as it is`);

  const prose = outsideCode(markdown);
  if (/^\s*(import|export) .+ from /m.test(prose)) problems.push(`${name}: an import of MDX is left`);
  if (prose.includes('{/*')) problems.push(`${name}: a JSX comment is left`);
  const component = /<\/?[A-Z][A-Za-z]*[\s/>]/.exec(prose);
  if (component) problems.push(`${name}: the component ${component[0].trim()} is left`);
  if (/\bclient:(load|visible|idle|only)\b/.test(prose)) problems.push(`${name}: a client directive is left`);

  for (const [, url] of prose.matchAll(/!?\[[^\]]*\]\(([^)\s]+)/g)) {
    if (!/^[a-z][a-z0-9+.-]*:/i.test(url)) {
      problems.push(`${name}: the link "${url}" is not absolute`);
      continue;
    }
    if (/^(mailto:|tel:)/.test(url)) continue;
    checkUrl(url, copy, 'link');
    // A link to a copy goes to a place on its page, which is where the fragment has to exist.
    const resolved = new URL(url);
    const fragment = decodeURIComponent(resolved.hash.slice(1));
    const file = resolved.origin === SITE ? target(resolved.pathname) : undefined;
    if (fragment && file?.endsWith('.md') && !parse(pageOf(relative(dist, file))).ids.has(fragment)) {
      problems.push(`${name}: the link "${url}" has no #${fragment} on its page`);
    }
  }
}

if (problems.length > 0) {
  console.error(problems.join('\n'));
  console.error(`\n${problems.length} problems in dist/.`);
  process.exit(1);
}
console.log(
  `dist/: ${html.length} pages and ${copies.length} Markdown copies, every link, asset, fragment, head tag and sitemap entry checks out.`,
);
