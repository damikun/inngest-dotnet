import { getCollection, type CollectionEntry } from 'astro:content';
import config from 'virtual:starlight/user-config';
import { SITE_NAME } from '../consts';
import { convert } from './markdown/convert';
import { HOME, htmlUrl, markdownUrl, pageId, siteLinks, type SiteLinks } from './markdown/links';

// The pages of the docs as Markdown, for the copies next to the HTML pages, for llms.txt, which lists them, and for
// llms-full.txt, which holds them all. The pages are listed in the order of the sidebar.

export interface Page {
  id: string;
  title: string;
  // One line, from the page's frontmatter.
  description: string | undefined;
  url: URL;
  markdownUrl: URL;
  // The whole copy: the title, the description and the address of the web page, then the page.
  markdown: string;
}

export interface Section {
  title: string;
  pages: Page[];
}

export interface Site {
  // The site's address with its base path: "https://damikun.github.io/inngest-dotnet/".
  root: URL;
  pages: Page[];
  // The sidebar's groups, in order, with the pages the llms.txt marks optional gathered at the end.
  sections: Section[];
}

// Groups of the sidebar that a reader can skip when short of room: tools to try, the state of the project and the
// notes for contributors.
const OPTIONAL = new Set(['Interactive', 'Project', 'Internals']);

const SUMMARY =
  'inngest-dotnet is a community .NET port of the Inngest TypeScript SDK. It runs durable functions and background workflows written in C#: steps that run once and retry on their own, sleeps and waits for events that last days, parallel steps, and flow control such as concurrency, throttling and rate limits. Functions are served from an ASP.NET Core endpoint, from Azure Functions or AWS Lambda, or from a Connect worker. Durable endpoints, in beta, let an ASP.NET Core endpoint run steps and answer its caller directly. It is not affiliated with or endorsed by Inngest Inc.';

const cache = new Map<string, Promise<Site>>();

// Converted once per build, however many endpoints ask.
export function loadSite(site: URL | undefined): Promise<Site> {
  if (!site) throw new Error('The Markdown copies need the site option of astro.config.mjs, for their absolute links.');
  const key = site.href;
  let loaded = cache.get(key);
  if (!loaded) {
    loaded = load(site);
    cache.set(key, loaded);
  }
  return loaded;
}

async function load(site: URL): Promise<Site> {
  const entries = await getCollection('docs', ({ data }) => !data.draft);
  const links = siteLinks(site, import.meta.env.BASE_URL, entries.map((entry) => entry.id));
  const pages = new Map<string, Page>();
  for (const entry of entries) pages.set(pageId(entry.id), page(entry, links));
  return { root: links.root, pages: [...pages.values()], sections: sections(pages) };
}

function page(entry: CollectionEntry<'docs'>, links: SiteLinks): Page {
  const id = pageId(entry.id);
  const url = htmlUrl(links, id);
  const { title, hero } = entry.data;
  const description = entry.data.description?.replace(/\s+/g, ' ').trim() || undefined;
  const body = convert(entry.body ?? '', { name: id, url, links, mdx: entry.filePath?.endsWith('.mdx') ?? false });
  // The pages generated from docs/ take their description from their first paragraph, which is in the body too.
  const stated = description && !saysItself(body, description);
  const markdown = [`# ${title}`, stated && `> ${description}`, hero?.tagline, `Web page: ${url.href}`, body]
    .filter(Boolean)
    .join('\n\n');
  return { id, title, description, url, markdownUrl: markdownUrl(links, id), markdown: `${markdown.trim()}\n` };
}

// Whether the start of a page already says its description, with or without the Markdown and the ellipsis a long
// description ends with.
function saysItself(markdown: string, description: string): boolean {
  const plain = (text: string) =>
    text
      .replace(/\[([^\]]*)\]\([^)]*\)/g, '$1')
      .replace(/[`*_]/g, '')
      .replace(/\s+/g, ' ')
      .trim();
  return plain(markdown).slice(0, 1000).includes(plain(description).replace(/\.{3}$/, ''));
}

type SidebarItem = NonNullable<typeof config.sidebar>[number];

// The sidebar's groups with the pages in them; a group inside a group adds its pages to the one around it. The home
// page leads the first section, and a page that is in no group goes into a last one of its own.
function sections(pages: Map<string, Page>): Section[] {
  const placed = new Set<string>();
  const place = (id: string): Page[] => {
    const found = pages.get(pageId(id));
    if (!found || placed.has(found.id)) return [];
    placed.add(found.id);
    return [found];
  };
  const collect = (items: readonly SidebarItem[]): Page[] =>
    items.flatMap((item) => {
      if ('items' in item) return collect(item.items);
      if ('slug' in item) return place(item.slug);
      if ('autogenerate' in item) {
        const directory = `${item.autogenerate.directory}/`;
        return [...pages.values()]
          .filter((candidate) => candidate.id.startsWith(directory))
          .sort((a, b) => a.id.localeCompare(b.id))
          .flatMap((candidate) => place(candidate.id));
      }
      return [];
    });

  const groups: Section[] = [];
  let loose: SidebarItem[] = [];
  for (const item of config.sidebar ?? []) {
    if ('items' in item) groups.push({ title: item.label, pages: collect(item.items) });
    else loose.push(item);
  }
  if (loose.length > 0) groups.unshift({ title: 'Pages', pages: collect(loose) });

  const home = place(HOME);
  const [first, ...rest] = groups.filter((group) => !OPTIONAL.has(group.title));
  const regular = first ? [{ ...first, pages: [...home, ...first.pages] }, ...rest] : [{ title: 'Pages', pages: home }];
  const optional = groups.filter((group) => OPTIONAL.has(group.title)).flatMap((group) => group.pages);
  const unplaced = [...pages.values()].filter((candidate) => !placed.has(candidate.id));
  return [
    ...regular,
    ...(unplaced.length > 0 ? [{ title: 'More', pages: unplaced }] : []),
    ...(optional.length > 0 ? [{ title: 'Optional', pages: optional }] : []),
  ].filter((section) => section.pages.length > 0);
}

// What the two files say before the pages: the name, the summary and how the files are laid out.
function introduction(site: Site, full: boolean): string[] {
  const assistants = site.pages.find((candidate) => candidate.id === 'guides/ai-assistants');
  const layout = full
    ? `This file holds the Markdown of every page of the documentation, in the order of [llms.txt](${new URL('llms.txt', site.root).href}). Each page starts with its title and the address of its web page.`
    : `Every page is also a Markdown file next to its web page, at the same address with \`.md\` in place of the trailing slash. [llms-full.txt](${new URL('llms-full.txt', site.root).href}) holds all of them in one file.`;
  return [
    `# ${SITE_NAME}`,
    `> ${SUMMARY}`,
    [
      "The API is not the TypeScript SDK's. Steps are methods on `context.Step`, such as `RunAsync` and `SleepAsync`, a function is a class or a delegate, and an event's data is a record.",
      assistants && `Read [${assistants.title}](${assistants.markdownUrl.href}) before writing code with it.`,
      layout,
    ]
      .filter(Boolean)
      .join(' '),
  ];
}

export function llmsTxt(site: Site): string {
  const parts = introduction(site, false);
  for (const section of site.sections) {
    parts.push(
      `## ${section.title}`,
      section.pages.map((p) => `- [${p.title}](${p.markdownUrl.href})${p.description ? `: ${p.description}` : ''}`).join('\n'),
    );
  }
  return `${parts.join('\n\n')}\n`;
}

export function llmsFullTxt(site: Site): string {
  const pages = site.sections.flatMap((section) => section.pages).map((p) => p.markdown.trim());
  return `${[introduction(site, true).join('\n\n'), ...pages].join('\n\n---\n\n')}\n`;
}
