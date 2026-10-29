import type { AstroIntegration } from 'astro';
import { existsSync, mkdirSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { transformRepoDoc } from '../lib/repo-docs';

export interface RepoDocsOptions {
  // The repository's docs folder, relative to the site's root.
  docsDir: string;
  // Where the pages are written, relative to the site's root. Everything in it is generated.
  outDir: string;
  repoUrl: string;
  branch: string;
}

// Copies every docs/*.md of the repository into the docs collection before Astro loads content, so reports that
// land in docs/ show up on the site without any change to the site.
export function syncRepoDocs(root: string, base: string, options: RepoDocsOptions): string[] {
  const source = resolve(root, options.docsDir);
  const target = resolve(root, options.outDir);
  const repoRoot = resolve(source, '..');
  const files = existsSync(source)
    ? readdirSync(source)
        .filter((file) => file.toLowerCase().endsWith('.md'))
        .sort()
    : [];

  const docs = new Set(files);
  const isDirectory = (path: string) => {
    try {
      return statSync(join(repoRoot, path)).isDirectory();
    } catch {
      return false;
    }
  };

  const wanted = new Map<string, string>();
  for (const file of files) {
    const doc = transformRepoDoc(file, readFileSync(join(source, file), 'utf8'), {
      base,
      repoUrl: options.repoUrl,
      branch: options.branch,
      docs,
      isDirectory,
    });
    wanted.set(`${doc.slug}.md`, doc.content);
  }

  mkdirSync(target, { recursive: true });
  for (const existing of readdirSync(target)) {
    if (!wanted.has(existing)) rmSync(join(target, existing), { recursive: true, force: true });
  }
  for (const [name, content] of wanted) {
    const path = join(target, name);
    if (!existsSync(path) || readFileSync(path, 'utf8') !== content) writeFileSync(path, content);
  }
  return files;
}

export default function repoDocs(options: RepoDocsOptions): AstroIntegration {
  let root = '';
  let base = '/';
  return {
    name: 'repo-docs',
    hooks: {
      'astro:config:setup': ({ config, logger }) => {
        root = fileURLToPath(config.root);
        base = config.base;
        const files = syncRepoDocs(root, base, options);
        logger.info(`${files.length} documents from ${options.docsDir}: ${files.join(', ')}`);
      },
      'astro:server:setup': ({ server, logger }) => {
        const source = resolve(root, options.docsDir);
        server.watcher.add(source);
        server.watcher.on('all', (_, path) => {
          if (path.startsWith(source) && path.toLowerCase().endsWith('.md')) {
            syncRepoDocs(root, base, options);
            logger.info(`Updated pages from ${options.docsDir}`);
          }
        });
      },
    },
  };
}
