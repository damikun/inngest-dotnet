import { Parser } from 'htmlparser2';

// Turns the HTML that the docs keep in data files and in a few Markdown pages into Markdown: links, code, emphasis,
// paragraphs, lists, headings and images. Anything else loses its tags and keeps its text.

const blocks = new Set(['p', 'div', 'section', 'details', 'summary', 'blockquote', 'ul', 'ol', 'figure', 'figcaption']);
const headings = new Set(['h1', 'h2', 'h3', 'h4', 'h5', 'h6']);

export function htmlToMarkdown(html: string, link: (href: string) => string = (href) => href): string {
  let out = '';
  let pre = false;
  const anchors: { at: number; href: string | undefined }[] = [];
  const lists: { ordered: boolean; count: number }[] = [];

  const parser = new Parser({
    onopentag(tag, attributes) {
      if (blocks.has(tag)) {
        out += '\n\n';
        if (tag === 'ul' || tag === 'ol') lists.push({ ordered: tag === 'ol', count: 0 });
        if (tag === 'summary') out += '**';
      } else if (headings.has(tag)) {
        out += `\n\n${'#'.repeat(Number(tag[1]))} `;
      } else if (tag === 'li') {
        const list = lists.at(-1);
        if (list) list.count++;
        out += `\n${list?.ordered ? `${list.count}. ` : '- '}`;
      } else if (tag === 'a') {
        anchors.push({ at: out.length, href: attributes.href });
        out += '[';
      } else if (tag === 'code' && !pre) {
        out += '`';
      } else if (tag === 'pre') {
        pre = true;
        out += '\n\n```\n';
      } else if (tag === 'strong' || tag === 'b') {
        out += '**';
      } else if (tag === 'em' || tag === 'i') {
        out += '*';
      } else if (tag === 'br') {
        out += '\n';
      } else if (tag === 'img') {
        out += `![${attributes.alt ?? ''}](${link(attributes.src ?? '')})`;
      }
    },
    ontext(text) {
      out += pre ? text : text.replace(/\s+/g, ' ');
    },
    onclosetag(tag) {
      if (blocks.has(tag)) {
        if (tag === 'summary') out += '**';
        if (tag === 'ul' || tag === 'ol') lists.pop();
        out += '\n\n';
      } else if (headings.has(tag)) {
        out += '\n\n';
      } else if (tag === 'a') {
        const anchor = anchors.pop();
        if (anchor?.href === undefined) out = anchor ? out.slice(0, anchor.at) + out.slice(anchor.at + 1) : out;
        else out += `](${link(anchor.href)})`;
      } else if (tag === 'code' && !pre) {
        out += '`';
      } else if (tag === 'pre') {
        pre = false;
        out += '\n```\n\n';
      } else if (tag === 'strong' || tag === 'b') {
        out += '**';
      } else if (tag === 'em' || tag === 'i') {
        out += '*';
      }
    },
  });
  parser.write(html);
  parser.end();

  return out
    .replace(/[ \t]*\n[ \t]*/g, '\n')
    .replace(/\n{3,}/g, '\n\n')
    .trim();
}
