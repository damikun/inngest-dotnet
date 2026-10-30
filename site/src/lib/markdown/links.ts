// Where the site's pages live, so that the Markdown copies can link to each other instead of to the HTML pages.

export interface SiteLinks {
  // The site's origin and base path, with a trailing slash: "https://damikun.github.io/inngest-dotnet/".
  root: URL;
  // The ids of the docs pages, such as "guides/steps"; "index" is the home page.
  pages: ReadonlySet<string>;
}

export const HOME = 'index';

export function siteLinks(site: URL | string, base: string, ids: Iterable<string>): SiteLinks {
  const root = new URL(`${base.replace(/\/?$/, '/')}`, site);
  return { root, pages: new Set([...ids].map(pageId)) };
}

// Starlight names the home page "" or "index", depending on the version and the file.
export function pageId(id: string): string {
  return id === '' ? HOME : id;
}

// The HTML page: the site's root for the home page, "guides/steps/" for the others.
export function htmlUrl(links: SiteLinks, id: string): URL {
  return new URL(pageId(id) === HOME ? '' : `${id}/`, links.root);
}

// The Markdown copy, next to the HTML page: "guides/steps.md", and "index.md" for the home page.
export function markdownUrl(links: SiteLinks, id: string): URL {
  return new URL(`${pageId(id)}.md`, links.root);
}

// Makes a link absolute. A link to a page of the site points at that page's Markdown copy, so that someone who reads
// the copies can follow every link without leaving them. Everything else keeps the target the author wrote.
export function absoluteLink(href: string, from: URL, links: SiteLinks): string {
  if (href === '') return href;
  const absolute = /^[a-z][a-z0-9+.-]*:/i.test(href);
  let url: URL;
  try {
    url = new URL(href, from);
  } catch {
    return href;
  }
  if (url.origin === links.root.origin) {
    const path = url.pathname.endsWith('/') ? url.pathname : `${url.pathname}/`;
    if (path.startsWith(links.root.pathname)) {
      const id = path.slice(links.root.pathname.length).replace(/\/$/, '') || HOME;
      if (links.pages.has(id)) return `${markdownUrl(links, id).href}${url.hash}`;
    }
  }
  return absolute ? href : url.href;
}
