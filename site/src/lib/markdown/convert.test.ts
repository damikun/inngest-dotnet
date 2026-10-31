import { describe, expect, it } from 'vitest';
import { convert } from './convert.ts';
import { htmlToMarkdown } from './html.ts';
import { absoluteLink, htmlUrl, markdownUrl, siteLinks } from './links.ts';

const links = siteLinks('https://example.com', '/docs', ['index', 'guides/steps', 'guides/connect', 'how-it-works']);
const url = htmlUrl(links, 'guides/steps');

function mdx(source: string): string {
  return convert(source, { name: 'guides/steps', url, links, mdx: true });
}

describe('links', () => {
  it('puts the Markdown copy next to the HTML page', () => {
    expect(htmlUrl(links, 'guides/steps').href).toBe('https://example.com/docs/guides/steps/');
    expect(markdownUrl(links, 'guides/steps').href).toBe('https://example.com/docs/guides/steps.md');
    expect(htmlUrl(links, '').href).toBe('https://example.com/docs/');
    expect(markdownUrl(links, '').href).toBe('https://example.com/docs/index.md');
  });

  it.each([
    ['/docs/guides/connect/', 'https://example.com/docs/guides/connect.md'],
    ['/docs/guides/connect/#workers', 'https://example.com/docs/guides/connect.md#workers'],
    ['../connect/', 'https://example.com/docs/guides/connect.md'],
    ['/docs/', 'https://example.com/docs/index.md'],
    ['#step-ids', 'https://example.com/docs/guides/steps.md#step-ids'],
    ['/docs/og/index.png', 'https://example.com/docs/og/index.png'],
    ['https://example.com/docs/how-it-works/', 'https://example.com/docs/how-it-works.md'],
    ['https://www.inngest.com/docs', 'https://www.inngest.com/docs'],
    ['mailto:someone@example.com', 'mailto:someone@example.com'],
    ['/elsewhere/', 'https://example.com/elsewhere/'],
  ])('%s becomes %s', (href, expected) => {
    expect(absoluteLink(href, url, links)).toBe(expected);
  });
});

describe('convert', () => {
  it('removes imports and JSX comments and keeps code with its language and title', () => {
    const markdown = mdx(
      [
        "import { Steps } from '@astrojs/starlight/components';",
        '',
        'A step runs once.',
        '',
        '{/* check: body OrderPlaced */}',
        '```csharp title="Charge.cs" wrap {1}',
        'await context.Step.RunAsync("charge", ct => Task.CompletedTask);',
        '```',
      ].join('\n'),
    );
    expect(markdown).toBe(
      ['A step runs once.', '', '```csharp title="Charge.cs"', 'await context.Step.RunAsync("charge", ct => Task.CompletedTask);', '```', ''].join('\n'),
    );
  });

  it('writes tabs as sections one level below the heading they sit under', () => {
    const markdown = mdx(
      [
        '## In code',
        '',
        '<Tabs syncKey="style">',
        '<TabItem label="Attributes">',
        '',
        '```csharp',
        '[Concurrency(2)]',
        '```',
        '',
        '</TabItem>',
        '<TabItem label="Code first">',
        '',
        'Pass `FunctionOptions`.',
        '',
        '</TabItem>',
        '</Tabs>',
      ].join('\n'),
    );
    expect(markdown).toBe(
      ['## In code', '', '### Attributes', '', '```csharp', '[Concurrency(2)]', '```', '', '### Code first', '', 'Pass `FunctionOptions`.', ''].join('\n'),
    );
  });

  it('writes tabs inside a list as bold labels, since a heading does not belong there', () => {
    const markdown = mdx(
      ['## Run it', '', '1. Start the app:', '', '   <Tabs>', '   <TabItem label="bash">', '', '   ```bash', '   dotnet run', '   ```', '', '   </TabItem>', '   </Tabs>'].join('\n'),
    );
    expect(markdown).toBe(['## Run it', '', '1. Start the app:', '', '   **bash**', '', '   ```bash', '   dotnet run', '   ```', ''].join('\n'));
  });

  it('writes an aside as a blockquote with its title', () => {
    expect(mdx('<Aside type="caution" title="Careful">\n\nIds must not change.\n\n</Aside>')).toBe(
      ['> **Careful**', '>', '> Ids must not change.', ''].join('\n'),
    );
    expect(mdx('<Aside type="tip">\n\nUse a record.\n\n</Aside>')).toBe(['> **Tip**', '>', '> Use a record.', ''].join('\n'));
  });

  it('writes link cards as one list of links to the Markdown copies', () => {
    const markdown = mdx(
      [
        '<CardGrid>',
        '  <LinkCard title="Connect" description="Workers without an endpoint." href="/docs/guides/connect/" />',
        '  <LinkCard title="Inngest" href="https://www.inngest.com/docs" />',
        '</CardGrid>',
      ].join('\n'),
    );
    expect(markdown).toBe(
      ['- [Connect](https://example.com/docs/guides/connect.md): Workers without an endpoint.', '- [Inngest](https://www.inngest.com/docs)', ''].join('\n'),
    );
  });

  it('unwraps HTML wrappers and keeps the Markdown inside them', () => {
    expect(mdx('<div class="card">\n\n### Title\n\nText with [a link](/docs/how-it-works/).\n\n</div>')).toBe(
      ['### Title', '', 'Text with [a link](https://example.com/docs/how-it-works.md).', ''].join('\n'),
    );
  });

  it('leaves out an interactive tool and says where it is', () => {
    expect(mdx('<FlowTimeline client:visible />')).toBe('An interactive version is on [the web page](https://example.com/docs/guides/steps/).\n');
  });

  it('writes the recorded run of the replay visualizer as the function and a table of its calls', () => {
    const markdown = mdx('<ReplayVisualizer client:visible />');
    expect(markdown).toContain('public sealed class FulfillOrder');
    expect(markdown).toContain('| 1 | no saved results | `206` with a `StepRun` op for `reserve-stock` |');
    expect(markdown).toMatch(/\| 4 \| the saved results of `reserve-stock`, `charge-card`, `cooling-off` \| `206` with a `StepPlanned` op for `send-receipt` and a `StepPlanned` op for `ship-order` \|/);
    expect(markdown).toContain('and a request to run `send-receipt` | `206` with a `StepRun` op for `send-receipt` |');
    expect(markdown).toMatch(/\| 7 \|.*\| `200` with the output `\{"orderId":"ord_1042","paymentId":"pay_31d9"\}` \|/);
    expect(markdown).toContain('An interactive version is on [the web page]');
  });

  it('writes a figure as its caption', () => {
    expect(mdx('<StepsReplay caption="Every call runs <code>ExecuteAsync</code> from the top." />')).toBe(
      'Figure: Every call runs `ExecuteAsync` from the top.\n',
    );
    expect(mdx('<ParallelFan />')).toMatch(/^Figure: When a call reaches two new steps at once/);
  });

  it('writes the dev server commands as sections, for HTTP and for Connect', () => {
    const http = mdx('## Run it\n\n<DevServer />');
    expect(http).toContain('### Install script');
    expect(http).toContain('inngest dev -u http://localhost:5280/api/inngest');
    expect(http).toContain('The container reaches the app at `host.docker.internal`');
    const connect = mdx('<DevServer connect />');
    expect(connect).toContain('inngest dev --no-discovery');
    expect(connect).not.toContain('5280');
  });

  it('stops at a component it has no Markdown for, and says where', () => {
    expect(() => mdx('Text.\n\n<Chart data={[1, 2]} />')).toThrow('guides/steps:3: the component <Chart> has no Markdown form');
    expect(() => mdx('<marquee>Hi</marquee>')).toThrow('the HTML element <marquee>');
  });

  it('stops at an expression that is not a comment', () => {
    expect(() => mdx('Total: {order.Total}')).toThrow('the expression {order.Total}');
  });

  it('keeps tables compact', () => {
    expect(mdx('| Method | Does |\n|---|---|\n| `RunAsync` | Runs code once, saves its result. |')).toBe(
      ['| Method | Does |', '| - | - |', '| `RunAsync` | Runs code once, saves its result. |', ''].join('\n'),
    );
  });

  it('converts Markdown pages with raw HTML, such as the ones generated from docs/', () => {
    const markdown = convert(
      [
        '<p class="repo-doc-source">Rendered from <a href="https://github.com/o/r/blob/main/docs/design.md"><code>docs/design.md</code></a> in the repository.</p>',
        '',
        'See [the guide](/docs/guides/steps/) and the <!-- note -->text.',
        '',
        '```json',
        '{ "a": "<uuid>" }',
        '```',
      ].join('\n'),
      { name: 'internals/design', url: htmlUrl(links, 'internals/design'), links, mdx: false },
    );
    expect(markdown).toBe(
      [
        'Rendered from [`docs/design.md`](https://github.com/o/r/blob/main/docs/design.md) in the repository.',
        '',
        'See [the guide](https://example.com/docs/guides/steps.md) and the text.',
        '',
        '```json',
        '{ "a": "<uuid>" }',
        '```',
        '',
      ].join('\n'),
    );
  });
});

describe('htmlToMarkdown', () => {
  it('writes links, code and emphasis', () => {
    expect(htmlToMarkdown('Use <code>MapInngest</code>, or <a href="/docs/guides/connect/">Connect</a>. <strong>Not</strong> <em>both</em>.')).toBe(
      'Use `MapInngest`, or [Connect](/docs/guides/connect/). **Not** *both*.',
    );
  });

  it('writes paragraphs, lists and headings', () => {
    expect(htmlToMarkdown('<h2>Why</h2><p>One.</p><ul><li>A</li><li>B</li></ul><ol><li>First</li><li>Second</li></ol>')).toBe(
      ['## Why', '', 'One.', '', '- A', '- B', '', '1. First', '2. Second'].join('\n'),
    );
  });

  it('decodes entities and drops comments and other tags', () => {
    expect(htmlToMarkdown('<span class="x">a &amp; b</span><!-- hidden --> c')).toBe('a & b c');
  });
});
