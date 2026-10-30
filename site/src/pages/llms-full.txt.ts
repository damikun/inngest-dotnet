import type { APIRoute } from 'astro';
import { llmsFullTxt, loadSite } from '../lib/llms';

// Every page of the docs as Markdown in one file, in the order of llms.txt, for tools that take a single file.
export const GET: APIRoute = async ({ site }) =>
  new Response(llmsFullTxt(await loadSite(site)), { headers: { 'Content-Type': 'text/plain; charset=utf-8' } });
