import { devServerTabs } from '../../data/dev-server';
import { faq } from '../../data/faq';
import { calls } from '../../data/hero';
import replay from '../../data/replay.json';
import type { Api, Handler, JsxElement, MdNode } from './convert';

// What each component of the docs is, written as Markdown. The components of Starlight become what they say in
// words: tabs are sections one after the other and asides are blockquotes. The figures of the site become their
// captions, which say in words what the drawing shows, and the replay visualizer becomes the run it shows. The other
// interactive tools have no text of their own, so each becomes a line that says where to use it; the page around them
// says what they do. A component that is not here stops the build, so that a page never loses one without anyone
// noticing.

const text = (value: string): MdNode => ({ type: 'text', value });
const paragraph = (...children: MdNode[]): MdNode => ({ type: 'paragraph', children });
const strong = (value: string): MdNode => ({ type: 'strong', children: [text(value)] });
const heading = (depth: number, value: string): MdNode => ({ type: 'heading', depth: Math.min(depth, 6), children: [text(value)] });
const listItem = (...children: MdNode[]): MdNode => ({ type: 'listItem', spread: false, children });
const list = (ordered: boolean, items: MdNode[], data?: MdNode['data']): MdNode => ({
  type: 'list',
  ordered,
  start: ordered ? 1 : null,
  spread: false,
  data,
  children: items,
});

const children: Handler = (node, api) => api.nodes(node.children!);
const omit: Handler = () => [];

// An interactive tool has nothing to write out, so the copy says where it is.
const interactive: Handler = (_, api) => [
  paragraph(text('An interactive version is on '), { type: 'link', url: api.pageUrl, children: [text('the web page')] }, text('.')),
];

// A titled part of a page: a heading one level below the section it sits in, or a bold line inside a list, where a
// heading would not read as one.
function titled(api: Api, title: string, body: () => MdNode[]): MdNode[] {
  const depth = api.depth;
  const label = api.inList ? paragraph(strong(title)) : heading(depth + 1, title);
  const content = body();
  // The next part starts at the same level, whatever headings this one held.
  api.depth = depth;
  return [label, ...content];
}

// The recorded run that the replay visualizer steps through, as the source of the function and a table of its calls.
interface ReplayCall {
  request: { url: string; body: { steps?: Record<string, unknown> } };
  response: { status: number; body: unknown };
}

function replayTable(api: Api): MdNode {
  const names = replay.stepNames as Record<string, string>;
  const name = (id: string) => `\`${names[id] ?? id}\``;
  const rows = (replay.calls as ReplayCall[]).map((call, index) => {
    const saved = Object.keys(call.request.body.steps ?? {}).map(name);
    const target = new URL(call.request.url, 'http://localhost').searchParams.get('stepId');
    const sent = [
      saved.length > 0 ? `the saved results of ${saved.join(', ')}` : 'no saved results',
      ...(target && target !== 'step' ? [`a request to run ${name(target)}`] : []),
    ].join(' and ');
    const { status, body } = call.response;
    const answered = Array.isArray(body)
      ? body.map((op: { op: string; displayName: string }) => `a \`${op.op}\` op for \`${op.displayName}\``).join(' and ')
      : `the output \`${JSON.stringify(body)}\``;
    return `| ${index + 1} | ${sent} | \`${status}\` with ${answered} |`;
  });
  return api.raw(['| Call | Inngest sends | The SDK answers |', '| - | - | - |', ...rows].join('\n'));
}

const asides: Record<string, string> = { note: 'Note', tip: 'Tip', caution: 'Caution', danger: 'Danger' };

function required(node: JsxElement, api: Api, name: string): string {
  return api.attr(node, name) ?? api.fail(`<${node.name}> needs a ${name} written as plain text`, node);
}

// The captions of the figures are in the components themselves; StepsReplay takes its own as a property.
const figureSources = import.meta.glob<string>('../../components/figures/*.astro', { query: '?raw', import: 'default', eager: true });

function figure(name: string): Handler {
  return (node, api) => {
    const source = figureSources[`../../components/figures/${name}.astro`];
    const given = api.attr(node, 'caption');
    const slot = source && /<Fragment slot="caption">([\s\S]*?)<\/Fragment>/.exec(source);
    const fallback = source && /\bcaption = '((?:[^'\\]|\\.)*)'/.exec(source);
    const caption = given ?? slot?.[1] ?? fallback?.[1].replace(/\\(.)/g, '$1');
    if (!caption) return api.fail(`the figure ${name} has no caption to write out`, node);
    return [api.fragment(`Figure: ${caption}`)];
  };
}

export const components: Record<string, Handler> = {
  // Starlight
  Aside: (node, api) => {
    const type = api.attr(node, 'type') ?? 'note';
    const title = api.attr(node, 'title') ?? asides[type] ?? 'Note';
    return [{ type: 'blockquote', children: [paragraph(strong(title)), ...api.nodes(node.children!)] }];
  },
  Badge: (node, api) => [text(required(node, api, 'text'))],
  Card: (node, api) => [paragraph(strong(required(node, api, 'title'))), ...api.nodes(node.children!)],
  CardGrid: children,
  FileTree: children,
  LinkCard: (node, api) => {
    const title = required(node, api, 'title');
    const description = api.attr(node, 'description');
    const link: MdNode = { type: 'link', url: api.link(required(node, api, 'href')), children: [text(title)] };
    return [list(false, [listItem(paragraph(link, ...(description ? [text(`: ${description}`)] : [])))], { cards: true })];
  },
  Steps: children,
  Tabs: children,
  TabItem: (node, api) => titled(api, required(node, api, 'label'), () => api.nodes(node.children!)),

  // The landing page
  Section: (node, api) => {
    const lead = api.attr(node, 'lead');
    const title = required(node, api, 'title');
    api.depth = 2;
    return [heading(2, title), ...(lead ? [paragraph(text(lead))] : []), ...api.nodes(node.children!)];
  },
  Faq: (_, api) => faq.flatMap(({ question, answer }) => [heading(api.depth + 1, question), api.fragment(answer)]),
  Proof: omit,

  // How it works
  RunLoop: (_, api) => [
    paragraph(text('One run of a checkout function, call by call:')),
    list(true, calls.map((call) => listItem(api.fragment(call.story)))),
  ],
  Architecture: omit,
  ReplayVisualizer: (node, api) => [
    { type: 'code', lang: 'csharp', value: replay.source },
    replayTable(api),
    ...interactive(node, api),
  ],

  // The guides
  DevServer: (node, api) =>
    devServerTabs(api.flag(node, 'connect')).flatMap((tab) =>
      titled(api, tab.label, () => [
        ...tab.blocks.map((block): MdNode => ({
          type: 'code',
          lang: block.lang,
          meta: block.title ? `title="${block.title}"` : null,
          value: block.code,
        })),
        ...(tab.note ? [api.fragment(tab.note)] : []),
      ]),
    ),
  ConnectVsServe: figure('ConnectVsServe'),
  DeployTopology: figure('DeployTopology'),
  LocalSetup: figure('LocalSetup'),
  ParallelFan: figure('ParallelFan'),
  SleepWait: figure('SleepWait'),
  StepsReplay: figure('StepsReplay'),

  // The interactive explainers
  FlowTimeline: interactive,
  OptionsBuilder: interactive,
};

function textOf(node: MdNode): string {
  return node.type === 'text' || node.type === 'inlineCode' ? node.value! : (node.children ?? []).map(textOf).join('');
}

// The HTML elements that can appear in a page of components: wrappers around Markdown, and a few inline ones.
export const tags: Record<string, Handler> = {
  div: children,
  section: children,
  span: children,
  p: (node, api) => [paragraph(...api.nodes(node.children!))],
  br: () => [{ type: 'break' }],
  a: (node, api) => [{ type: 'link', url: api.link(required(node, api, 'href')), children: api.nodes(node.children!) }],
  code: (node) => [{ type: 'inlineCode', value: textOf(node) }],
  strong: (node, api) => [{ type: 'strong', children: api.nodes(node.children!) }],
  em: (node, api) => [{ type: 'emphasis', children: api.nodes(node.children!) }],
};
