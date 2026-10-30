// @ts-check
import react from '@astrojs/react';
import starlight from '@astrojs/starlight';
import tailwindcss from '@tailwindcss/vite';
import { defineConfig, fontProviders } from 'astro/config';
import starlightLinksValidator from 'starlight-links-validator';
import { darkCodeTheme, lightCodeTheme } from './src/code-themes.ts';
import { REPO_URL, SITE_DESCRIPTION } from './src/consts.ts';
import repoDocs from './src/integrations/repo-docs.ts';

export default defineConfig({
  site: 'https://damikun.github.io',
  base: '/inngest-dotnet',
  trailingSlash: 'always',
  integrations: [
    // Before Starlight, so that docs/*.md of the repository are in the docs collection when content loads.
    repoDocs({ docsDir: '../docs', outDir: 'src/content/docs/internals', repoUrl: REPO_URL, branch: 'main' }),
    starlight({
      title: 'inngest-dotnet',
      description: SITE_DESCRIPTION,
      favicon: '/favicon.svg',
      social: [{ icon: 'github', label: 'GitHub', href: REPO_URL }],
      editLink: { baseUrl: `${REPO_URL}/edit/main/site/` },
      customCss: ['./src/styles/global.css'],
      components: {
        Footer: './src/components/Footer.astro',
        Head: './src/components/Head.astro',
        Hero: './src/components/Hero.astro',
        SiteTitle: './src/components/SiteTitle.astro',
      },
      routeMiddleware: './src/route-data.ts',
      // src/pages/404.astro renders the page, so that the docs collection needs no 404 entry.
      disable404Route: true,
      expressiveCode: {
        themes: [darkCodeTheme, lightCodeTheme],
        useStarlightUiThemeColors: false,
        styleOverrides: {
          borderRadius: '0.5rem',
          borderColor: 'var(--idn-border)',
          codeFontFamily: 'var(--__sl-font-mono)',
          codeFontSize: '0.8125rem',
          codeLineHeight: '1.65',
          uiFontFamily: 'var(--__sl-font)',
          frames: {
            frameBoxShadowCssValue: 'none',
            editorActiveTabIndicatorTopColor: 'var(--sl-color-text-accent)',
            editorActiveTabIndicatorBottomColor: 'transparent',
            editorTabBarBorderBottomColor: 'var(--idn-border)',
            terminalTitlebarBorderBottomColor: 'var(--idn-border)',
            inlineButtonBorder: 'var(--idn-border-strong)',
          },
        },
      },
      sidebar: [
        {
          label: 'Start here',
          items: ['how-it-works', 'guides/getting-started'],
        },
        {
          label: 'Writing functions',
          items: [
            'guides/functions-and-triggers',
            'guides/steps',
            'guides/parallel-steps',
            'guides/sleep-and-wait-for-events',
            'guides/invoking-functions',
            'guides/sending-events',
            'guides/realtime',
          ],
        },
        {
          label: 'Reliability',
          items: ['guides/flow-control', 'guides/errors-and-retries', 'guides/middleware'],
        },
        {
          label: 'Running and shipping',
          items: ['guides/configuration', 'guides/connect', 'guides/testing', 'guides/native-aot', 'guides/deploying'],
        },
        {
          label: 'Examples',
          items: [
            'examples/order-fulfillment',
            'examples/user-onboarding',
            'examples/nightly-billing',
            'examples/fan-out-with-invoke',
            'examples/webhook-pipeline',
            'examples/scheduled-reminders',
            'examples/connect-worker',
            'examples/tracing-middleware',
          ],
        },
        {
          label: 'Interactive',
          items: ['explainers/function-options', 'explainers/flow-control'],
        },
        { label: 'Reference', items: ['reference/api'] },
        {
          label: 'Project',
          items: [
            'roadmap',
            { label: 'Readiness report', slug: 'internals/readiness' },
            { label: 'Benchmarks', slug: 'internals/benchmarks' },
          ],
        },
        // The contributor documents from the repository's docs/ folder, last and folded.
        {
          label: 'Internals',
          collapsed: true,
          items: [
            { label: 'Design', slug: 'internals/design' },
          ],
        },
      ],
      plugins: [starlightLinksValidator()],
    }),
    react(),
  ],
  // Self-hosted latin subsets, with fallback fonts sized to match so that swapping them in moves nothing.
  fonts: [
    {
      provider: fontProviders.local(),
      name: 'Geist',
      cssVariable: '--font-heading-face',
      fallbacks: ['sans-serif'],
      options: {
        variants: [{ src: ['@fontsource-variable/geist/files/geist-latin-wght-normal.woff2'], weight: '100 900', style: 'normal' }],
      },
    },
    {
      provider: fontProviders.local(),
      name: 'Figtree',
      cssVariable: '--font-body-face',
      fallbacks: ['sans-serif'],
      options: {
        variants: [{ src: ['@fontsource-variable/figtree/files/figtree-latin-wght-normal.woff2'], weight: '300 900', style: 'normal' }],
      },
    },
    {
      provider: fontProviders.local(),
      name: 'JetBrains Mono',
      cssVariable: '--font-code-face',
      fallbacks: ['monospace'],
      options: {
        variants: [
          { src: ['@fontsource-variable/jetbrains-mono/files/jetbrains-mono-latin-wght-normal.woff2'], weight: '100 800', style: 'normal' },
        ],
      },
    },
  ],
  vite: { plugins: [tailwindcss()] },
});
