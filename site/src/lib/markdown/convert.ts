import type { Root } from 'mdast';
import remarkGfm from 'remark-gfm';
import remarkMdx from 'remark-mdx';
import remarkParse from 'remark-parse';
import remarkStringify from 'remark-stringify';
import { unified } from 'unified';
import { components, tags } from './components';
import { htmlToMarkdown } from './html';
import { absoluteLink, type SiteLinks } from './links';

// Turns the body of a docs page into plain Markdown: no imports, no JSX comments, components written out as the text
// they show, code blocks kept with their language, and every link absolute. The pages are MDX, except the ones the
// repo-docs integration generates from docs/, which are Markdown with a little raw HTML.

// The mdast nodes, loosely typed: the MDX nodes are not part of mdast's own types.
export interface MdNode {
  type: string;
  children?: MdNode[];
  value?: string;
  [key: string]: any;
}

export interface JsxElement extends MdNode {
  name: string | null;
  attributes: { type: string; name?: string; value?: string | { value: string } | null }[];
}

// What a component's handler can ask of the converter.
export interface Api {
  // The heading depth of the section being written, so that a component can add headings below it.
  depth: number;
  // Whether the node is inside a list, where a heading is not welcome.
  readonly inList: boolean;
  // The children of an element, converted.
  nodes(children: MdNode[]): MdNode[];
  // The value of a string attribute, or undefined when it is missing or computed.
  attr(node: JsxElement, name: string): string | undefined;
  // Whether a boolean attribute is set: <DevServer connect />.
  flag(node: JsxElement, name: string): boolean;
  // Markdown that is printed as it is.
  raw(markdown: string): MdNode;
  // HTML turned into a paragraph of Markdown, with its links made absolute.
  fragment(html: string): MdNode;
  // A link made absolute.
  link(href: string): string;
  // The web page that is being converted.
  readonly pageUrl: string;
  fail(message: string, node?: MdNode): never;
}

export type Handler = (node: JsxElement, api: Api) => MdNode[];

export interface ConvertOptions {
  // What the source is called in error messages.
  name: string;
  // The page's own URL, which relative links are resolved against.
  url: URL;
  links: SiteLinks;
  // MDX, or plain Markdown, which may hold raw HTML but no components.
  mdx: boolean;
}

const markdown = unified().use(remarkParse).use(remarkGfm);
const mdx = unified().use(remarkParse).use(remarkMdx).use(remarkGfm);
// Tables are not padded into columns: the padding would cost a reader of the text more than it shows.
const printer = unified()
  .use(remarkGfm, { tablePipeAlign: false })
  .use(remarkStringify, { bullet: '-', emphasis: '*', strong: '*', fences: true, rule: '-', listItemIndent: 'one' });

const phrasing = new Set(['paragraph', 'heading', 'emphasis', 'strong', 'delete', 'link', 'linkReference', 'tableCell']);

export function convert(source: string, options: ConvertOptions): string {
  const root = (options.mdx ? mdx : markdown).parse(source) as Root;
  const converter = new Converter(options);
  root.children = converter.nodes(root.children as MdNode[]) as Root['children'];
  return `${String(printer.stringify(root)).trim()}\n`;
}

class Converter implements Api {
  depth = 1;
  private lists = 0;

  constructor(private readonly options: ConvertOptions) {}

  get inList(): boolean {
    return this.lists > 0;
  }

  nodes(children: MdNode[], inline = false): MdNode[] {
    const out: MdNode[] = [];
    for (const child of children) {
      for (const next of this.node(child, inline)) {
        const last = out.at(-1);
        // Link cards that follow each other make one list; two lists in a row would be printed with a comment between.
        if (next.type === 'list' && next.data?.cards && last?.type === 'list' && last.data?.cards) {
          last.children!.push(...next.children!);
        } else {
          out.push(next);
        }
      }
    }
    return out;
  }

  private node(node: MdNode, inline: boolean): MdNode[] {
    switch (node.type) {
      case 'mdxjsEsm':
        return [];
      case 'mdxFlowExpression':
      case 'mdxTextExpression':
        return this.expression(node);
      case 'mdxJsxFlowElement':
      case 'mdxJsxTextElement':
        return this.element(node as JsxElement);
      case 'html':
        return this.html(node, inline);
      case 'heading':
        this.depth = node.depth;
        node.children = this.nodes(node.children!, true);
        return [node];
      case 'link':
        node.url = this.link(node.url);
        node.children = this.nodes(node.children!, true);
        return [node];
      case 'image':
      case 'definition':
        node.url = this.link(node.url);
        return [node];
      case 'code':
        node.meta = codeTitle(node.meta);
        return [node];
      case 'list':
        this.lists++;
        node.children = this.nodes(node.children!);
        this.lists--;
        return [node];
      case 'paragraph':
        node.children = this.nodes(node.children!, true);
        // A paragraph that held only what was removed, such as a comment, is not a paragraph any more.
        return node.children.every((child) => child.type === 'text' && child.value!.trim() === '') ? [] : [node];
      default:
        if (node.children) node.children = this.nodes(node.children, phrasing.has(node.type));
        return [node];
    }
  }

  // The only expressions in the docs are comments, such as the snippet checker's {/* check: body OrderPlaced */}.
  private expression(node: MdNode): MdNode[] {
    const value = node.value!.trim();
    if (/^(\/\*[\s\S]*?\*\/\s*)+$/.test(value)) return [];
    const text = /^(['"`])([^\\]*)\1$/.exec(value);
    if (text && !text[2].includes('${')) return [{ type: 'text', value: text[2] }];
    return this.fail(`the expression {${value}} has no Markdown form`, node);
  }

  private element(node: JsxElement): MdNode[] {
    if (node.name === null) return this.nodes(node.children!);
    const handler = /^[a-z]/.test(node.name) ? tags[node.name] : components[node.name];
    if (!handler) {
      const kind = /^[a-z]/.test(node.name) ? 'HTML element' : 'component';
      return this.fail(`the ${kind} <${node.name}> has no Markdown form; describe it in src/lib/markdown/components.ts`, node);
    }
    return handler(node, this);
  }

  private html(node: MdNode, inline: boolean): MdNode[] {
    if (inline) return /^<br\s*\/?>$/i.test(node.value!.trim()) ? [{ type: 'break' }] : [];
    const converted = htmlToMarkdown(node.value!, (href) => this.link(href));
    return converted ? [this.raw(converted)] : [];
  }

  attr(node: JsxElement, name: string): string | undefined {
    const attribute = node.attributes.find((candidate) => candidate.name === name);
    const value = attribute?.value;
    if (typeof value === 'string') return value;
    // {'text'} is a string too.
    const literal = value && /^\s*(['"])([^\\]*)\1\s*$/.exec(value.value);
    return literal ? literal[2] : undefined;
  }

  flag(node: JsxElement, name: string): boolean {
    const attribute = node.attributes.find((candidate) => candidate.name === name);
    if (!attribute) return false;
    const value = attribute.value;
    return value === null || value === undefined || value === 'true' || (typeof value === 'object' && value.value.trim() === 'true');
  }

  raw(value: string): MdNode {
    return { type: 'html', value };
  }

  fragment(html: string): MdNode {
    return { type: 'paragraph', children: [this.raw(htmlToMarkdown(html, (href) => this.link(href)))] };
  }

  link(href: string): string {
    return absoluteLink(href, this.options.url, this.options.links);
  }

  get pageUrl(): string {
    return this.options.url.href;
  }

  fail(message: string, node?: MdNode): never {
    const line = node?.position?.start?.line;
    throw new Error(`${this.options.name}${line ? `:${line}` : ''}: ${message}`);
  }
}

// Expressive Code's meta string holds frame and marker options; only the title says anything to a reader of the text.
function codeTitle(meta: string | null | undefined): string | null {
  const title = meta && /\btitle=(?:"([^"]*)"|'([^']*)')/.exec(meta);
  return title ? `title="${title[1] ?? title[2]}"` : null;
}
