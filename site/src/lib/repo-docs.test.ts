import { describe, expect, it } from 'vitest';
import { type RepoDocOptions, resolveTarget, rewriteLinks, slugOf, transformRepoDoc } from './repo-docs.ts';

const options: RepoDocOptions = {
  base: '/inngest-dotnet/',
  repoUrl: 'https://github.com/owner/repo',
  branch: 'main',
  docs: new Set(['design.md', 'benchmarks.md']),
  isDirectory: (path) => path === 'samples/Inngest.Sample',
};

describe('resolveTarget', () => {
  it.each([
    ['benchmarks.md', '/inngest-dotnet/internals/benchmarks/'],
    ['./benchmarks.md#results', '/inngest-dotnet/internals/benchmarks/#results'],
    ['../docs/design.md', '/inngest-dotnet/internals/design/'],
    ['missing.md', 'https://github.com/owner/repo/blob/main/docs/missing.md'],
    ['../README.md', 'https://github.com/owner/repo/blob/main/README.md'],
    ['../src/Inngest/InngestOptions.cs#L10', 'https://github.com/owner/repo/blob/main/src/Inngest/InngestOptions.cs#L10'],
    ['../samples/Inngest.Sample', 'https://github.com/owner/repo/tree/main/samples/Inngest.Sample'],
    ['/NOTICE', 'https://github.com/owner/repo/blob/main/NOTICE'],
    ['#spec-deviations', '#spec-deviations'],
    ['https://www.inngest.com/docs', 'https://www.inngest.com/docs'],
    ['mailto:someone@example.com', 'mailto:someone@example.com'],
    ['../../outside.md', '../../outside.md'],
  ])('%s becomes %s', (target, expected) => {
    expect(resolveTarget(target, options)).toBe(expected);
  });
});

describe('rewriteLinks', () => {
  it('rewrites inline links, images and reference definitions but not code', () => {
    const source = [
      'See [the benchmarks](benchmarks.md) and ![chart](img/chart.png "Chart").',
      'Inline `[not a link](benchmarks.md)` stays.',
      '```md',
      '[inside a fence](benchmarks.md)',
      '```',
      '[ref]: ../README.md',
    ].join('\n');
    expect(rewriteLinks(source, options)).toBe(
      [
        'See [the benchmarks](/inngest-dotnet/internals/benchmarks/) and ![chart](https://github.com/owner/repo/blob/main/docs/img/chart.png "Chart").',
        'Inline `[not a link](benchmarks.md)` stays.',
        '```md',
        '[inside a fence](benchmarks.md)',
        '```',
        '[ref]: https://github.com/owner/repo/blob/main/README.md',
      ].join('\n'),
    );
  });

  it('rewrites links whose text is inline code', () => {
    expect(rewriteLinks('Raw data: [`load-average.txt`](results/load-average.txt), and `[x](y.md)`.', options)).toBe(
      'Raw data: [`load-average.txt`](https://github.com/owner/repo/blob/main/docs/results/load-average.txt), and `[x](y.md)`.',
    );
  });
});

describe('transformRepoDoc', () => {
  it('takes the title from the first heading and the description from the first paragraph', () => {
    const doc = transformRepoDoc(
      'design.md',
      '# Design\n\nThis document describes how the SDK is put together. It is written for `people` working on the SDK.\n\n## Protocol\n\nText.\n',
      options,
    );
    expect(doc.slug).toBe('design');
    expect(doc.content).toContain('title: "Design"');
    expect(doc.content).toContain(
      'description: "This document describes how the SDK is put together. It is written for people working on the SDK."',
    );
    expect(doc.content).toContain('editUrl: "https://github.com/owner/repo/edit/main/docs/design.md"');
    expect(doc.content).toContain('  order: 1');
    expect(doc.content).not.toContain('# Design');
    expect(doc.content).toContain('## Protocol');
  });

  it('cuts a long description at a sentence end', () => {
    const sentence = 'This sentence is exactly long enough to matter here. ';
    const doc = transformRepoDoc('benchmarks.md', `# Benchmarks\n\n${sentence.repeat(5)}\n`, options);
    const description = /description: "(.*)"/.exec(doc.content)?.[1] ?? '';
    expect(description.length).toBeLessThanOrEqual(160);
    expect(description.endsWith('matter here.')).toBe(true);
  });

  it('falls back to the file name for a document without a heading, and orders unknown documents last', () => {
    const doc = transformRepoDoc('release-notes.md', 'Just text.\n', options);
    expect(doc.content).toContain('title: "Release notes"');
    expect(doc.content).toContain('  order: 100');
    expect(slugOf('Release_Notes.md')).toBe('release-notes');
  });
});
