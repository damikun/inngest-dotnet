import { defineRouteMiddleware } from '@astrojs/starlight/route-data';
import { REPO_URL, SITE_DESCRIPTION, SITE_NAME } from './consts';
import { faq, plainText } from './data/faq';
import { CARD_SIZE, cardPath, sectionOf } from './lib/sections';

// The home page's <title>, worded for what people search for; other pages use "<title> | inngest-dotnet".
const HOME_TITLE = 'inngest-dotnet: Inngest SDK for .NET durable functions';

// Adds what Starlight does not: the social card, Twitter tags and structured data for each page.
export const onRequest = defineRouteMiddleware((context) => {
  const route = context.locals.starlightRoute;
  const { entry, head } = route;
  // The 404 page answers for any missing URL, so it gets no canonical URL, social card or structured data.
  if (entry.id === '404') {
    route.head = head.filter((tag) => tag.attrs?.rel !== 'canonical' && tag.attrs?.property !== 'og:url');
    return;
  }
  const site = new URL(import.meta.env.BASE_URL, context.site);
  const page = new URL(context.url.pathname, context.site);
  const isHome = entry.id === 'index' || entry.id === '';
  const title = entry.data.title;
  const description = entry.data.description ?? SITE_DESCRIPTION;
  const card = new URL(cardPath(entry.id), site);
  const alt = isHome ? `${SITE_NAME}: ${entry.data.hero?.title ?? title}` : `${title}, ${SITE_NAME}`;

  const ogType = head.find((tag) => tag.tag === 'meta' && tag.attrs?.property === 'og:type');
  if (ogType?.attrs && isHome) ogType.attrs.content = 'website';
  const titleTag = head.find((tag) => tag.tag === 'title');
  if (titleTag && isHome) titleTag.content = HOME_TITLE;

  head.push(
    { tag: 'meta', attrs: { property: 'og:image', content: card.href } },
    { tag: 'meta', attrs: { property: 'og:image:type', content: 'image/png' } },
    { tag: 'meta', attrs: { property: 'og:image:width', content: String(CARD_SIZE.width) } },
    { tag: 'meta', attrs: { property: 'og:image:height', content: String(CARD_SIZE.height) } },
    { tag: 'meta', attrs: { property: 'og:image:alt', content: alt } },
    { tag: 'meta', attrs: { name: 'twitter:title', content: title } },
    { tag: 'meta', attrs: { name: 'twitter:description', content: description } },
    { tag: 'meta', attrs: { name: 'twitter:image', content: card.href } },
    { tag: 'meta', attrs: { name: 'twitter:image:alt', content: alt } },
    { tag: 'script', attrs: { type: 'application/ld+json' }, content: jsonLd(isHome, site, page, title, description, entry.id) },
  );
});

function jsonLd(isHome: boolean, site: URL, page: URL, title: string, description: string, id: string): string {
  const software = {
    '@type': 'SoftwareSourceCode',
    '@id': `${site.href}#software`,
    name: SITE_NAME,
    description:
      'A .NET port of the Inngest TypeScript SDK: durable functions made of steps that retry on their own, sleep, wait for events and run in parallel, served from ASP.NET Core or a Connect worker.',
    url: site.href,
    codeRepository: REPO_URL,
    programmingLanguage: 'C#',
    runtimePlatform: '.NET 10',
    license: 'https://www.apache.org/licenses/LICENSE-2.0',
    isBasedOn: 'https://github.com/inngest/inngest-js',
    author: { '@type': 'Organization', name: 'inngest-dotnet contributors', url: REPO_URL },
    keywords: ['Inngest', '.NET', 'C#', 'ASP.NET Core', 'durable execution', 'workflows', 'background jobs'],
  };
  const website = {
    '@type': 'WebSite',
    '@id': `${site.href}#website`,
    name: SITE_NAME,
    url: site.href,
    description: SITE_DESCRIPTION,
    inLanguage: 'en',
  };
  if (isHome) {
    const questions = {
      '@type': 'FAQPage',
      mainEntity: faq.map(({ question, answer }) => ({
        '@type': 'Question',
        name: question,
        acceptedAnswer: { '@type': 'Answer', text: plainText(answer) },
      })),
    };
    return serialize([website, software, questions]);
  }
  const breadcrumbs = {
    '@type': 'BreadcrumbList',
    itemListElement: [
      { '@type': 'ListItem', position: 1, name: SITE_NAME, item: site.href },
      { '@type': 'ListItem', position: 2, name: title, item: page.href },
    ],
  };
  const graph = isHome
    ? [website, software]
    : [
        {
          '@type': 'TechArticle',
          headline: title,
          description,
          url: page.href,
          inLanguage: 'en',
          articleSection: sectionOf(id),
          isPartOf: { '@id': website['@id'] },
          about: { '@id': software['@id'] },
        },
        breadcrumbs,
        website,
        software,
      ];
  return serialize(graph);
}

// Escaped so that the JSON can never close the script element it sits in.
function serialize(graph: object[]): string {
  return JSON.stringify({ '@context': 'https://schema.org', '@graph': graph }).replace(/</g, '\\u003c');
}
