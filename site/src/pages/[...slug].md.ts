import type { APIRoute, GetStaticPaths } from 'astro';
import { getCollection } from 'astro:content';
import { loadSite } from '../lib/llms';
import { pageId } from '../lib/markdown/links';

// The Markdown copy of every docs page, next to the page: guides/steps/ has guides/steps.md, and the home page has
// index.md. The pages the repo-docs integration generates from docs/ are in the collection too, so they have one.
export const getStaticPaths = (async () => {
  const entries = await getCollection('docs', ({ data }) => !data.draft);
  return entries.map((entry) => ({ params: { slug: pageId(entry.id) } }));
}) satisfies GetStaticPaths;

export const GET: APIRoute = async ({ params, site }) => {
  const page = (await loadSite(site)).pages.find((candidate) => candidate.id === params.slug);
  if (!page) return new Response('Not found', { status: 404 });
  return new Response(page.markdown, { headers: { 'Content-Type': 'text/markdown; charset=utf-8' } });
};
