import type { APIRoute, GetStaticPaths } from 'astro';
import { getCollection } from 'astro:content';
import { renderCardPng } from '../../lib/og';
import { sectionOf } from '../../lib/sections';

export const getStaticPaths = (async () => {
  const entries = await getCollection('docs');
  return entries.map((entry) => ({
    params: { slug: entry.id === '' ? 'index' : entry.id },
    props: {
      title: entry.data.hero?.title ?? entry.data.title,
      description: entry.data.description,
      section: sectionOf(entry.id),
      home: entry.id === '' || entry.id === 'index',
    },
  }));
}) satisfies GetStaticPaths;

export const GET: APIRoute = async ({ props }) => {
  const png = await renderCardPng(props as { title: string; description?: string; section?: string; home: boolean });
  return new Response(png, { headers: { 'Content-Type': 'image/png' } });
};
