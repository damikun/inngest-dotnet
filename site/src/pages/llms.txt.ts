import type { APIRoute } from 'astro';
import { llmsTxt, loadSite } from '../lib/llms';

// The index of the docs for language models, in the llms.txt format (llmstxt.org): the name, a summary and a link to
// the Markdown copy of every page, each with the page's description, in the sidebar's groups.
export const GET: APIRoute = async ({ site }) =>
  new Response(llmsTxt(await loadSite(site)), { headers: { 'Content-Type': 'text/plain; charset=utf-8' } });
