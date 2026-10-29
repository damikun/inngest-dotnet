import type { APIRoute } from 'astro';

// Crawlers only read robots.txt at the root of a host, so this one takes effect when the site is served from the
// root of its own domain. It is generated so that the sitemap URL follows the configured site and base path.
export const GET: APIRoute = ({ site }) => {
  const sitemap = new URL(`${import.meta.env.BASE_URL.replace(/\/?$/, '/')}sitemap-index.xml`, site);
  return new Response(`User-agent: *\nAllow: /\n\nSitemap: ${sitemap.href}\n`, {
    headers: { 'Content-Type': 'text/plain; charset=utf-8' },
  });
};
