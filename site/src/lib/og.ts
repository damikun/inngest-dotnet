import { createRequire } from 'node:module';
import * as fontkit from 'fontkit';
import sharp from 'sharp';
import { returnLine, steps, stillFrame, tokenize } from '../data/hero.ts';
import { CARD_SIZE } from './sections.ts';

// Social cards are drawn as SVG paths from the site's own fonts and rasterised with sharp, so they look the same
// on every machine and need no system fonts. The colors are the dark theme's. The landing page's card shows the
// hero illustration; every other page shows its title and the replay motif.

const WIDTH = CARD_SIZE.width;
const HEIGHT = CARD_SIZE.height;
const MARGIN = 80;

const colors = {
  background: '#1b1a19',
  surface: '#232120',
  border: '#3a3735',
  borderStrong: '#7d7671',
  rule: '#2c2a28',
  title: '#f6f6f6',
  text: '#d6d2ce',
  muted: '#a8a29d',
  accent: '#f65c4f',
  accentText: '#fd8a72',
  planned: '#fcc43f',
  plannedBg: '#3f2915',
  done: '#66bd8b',
  doneBg: '#0e3422',
  wait: '#ad9ffc',
  waitBg: '#2f1e64',
  codeBg: '#141312',
  code: { plain: '#e8e5e2', keyword: '#ad9ffc', string: '#66bd8b', type: '#9cd2ff', method: '#fd8a72' },
};


const require = createRequire(import.meta.url);
const load = (file: string) => fontkit.openSync(require.resolve(file));

const fonts = {
  display: load('@fontsource/geist/files/geist-latin-700-normal.woff2'),
  strong: load('@fontsource/figtree/files/figtree-latin-700-normal.woff2'),
  text: load('@fontsource/figtree/files/figtree-latin-500-normal.woff2'),
  mono: load('@fontsource/jetbrains-mono/files/jetbrains-mono-latin-600-normal.woff2'),
  code: load('@fontsource/jetbrains-mono/files/jetbrains-mono-latin-400-normal.woff2'),
};

interface TextRun {
  d: string;
  width: number;
}

// Code is set without ligatures, so "=>" stays two characters as it does on the site.
const plainCode = { calt: false, liga: false };

function shape(font: fontkit.Font, text: string, size: number, x: number, y: number, tracking = 0): TextRun {
  const scale = size / font.unitsPerEm;
  const run = font.layout(text, font === fonts.code || font === fonts.mono ? plainCode : undefined);
  let cursor = x;
  const parts: string[] = [];
  run.glyphs.forEach((glyph, i) => {
    const position = run.positions[i];
    parts.push(
      glyph.path
        .scale(scale, -scale)
        .translate(cursor + position.xOffset * scale, y - position.yOffset * scale)
        .toSVG(),
    );
    cursor += position.xAdvance * scale + tracking;
  });
  return { d: parts.join(''), width: cursor - x - (run.glyphs.length > 0 ? tracking : 0) };
}

function measure(font: fontkit.Font, text: string, size: number): number {
  return shape(font, text, size, 0, 0).width;
}

// Greedy word wrap. Returns null when the text needs more lines than allowed.
export function wrap(text: string, maxWidth: number, maxLines: number, width: (line: string) => number): string[] | null {
  const lines: string[] = [];
  let line = '';
  for (const word of text.split(/\s+/).filter(Boolean)) {
    const candidate = line ? `${line} ${word}` : word;
    if (line && width(candidate) > maxWidth) {
      lines.push(line);
      line = word;
    } else {
      line = candidate;
    }
  }
  if (line) lines.push(line);
  return lines.length <= maxLines ? lines : null;
}

// Fits the text in the given lines, shrinking it down to minSize, and cuts the last line short if it still does
// not fit.
function fit(font: fontkit.Font, text: string, maxWidth: number, maxLines: number, sizes: number[]) {
  for (const size of sizes) {
    const lines = wrap(text, maxWidth, maxLines, (line) => measure(font, line, size));
    if (lines) return { size, lines };
  }
  const size = sizes[sizes.length - 1];
  const words = text.split(/\s+/);
  while (words.length > 1) {
    words.pop();
    const lines = wrap(`${words.join(' ')}...`, maxWidth, maxLines, (line) => measure(font, line, size));
    if (lines) return { size, lines };
  }
  return { size, lines: [text] };
}

export interface CardContent {
  title: string;
  description?: string;
  section?: string;
  // The landing page: the title beside the hero illustration.
  home?: boolean;
}

export function renderCardSvg(content: CardContent): string {
  return content.home ? renderHomeSvg(content) : renderPageSvg(content);
}

function svg(shapes: string[], paths: string[]): string {
  return [
    `<svg xmlns="http://www.w3.org/2000/svg" width="${WIDTH}" height="${HEIGHT}" viewBox="0 0 ${WIDTH} ${HEIGHT}">`,
    '<defs><radialGradient id="glow" cx="0.3" cy="0" r="0.75">',
    `<stop offset="0" stop-color="${colors.accent}" stop-opacity="0.22"/><stop offset="1" stop-color="${colors.accent}" stop-opacity="0"/>`,
    '</radialGradient></defs>',
    `<rect width="${WIDTH}" height="${HEIGHT}" fill="${colors.background}"/>`,
    `<rect width="${WIDTH}" height="${HEIGHT}" fill="url(#glow)"/>`,
    `<rect x="0" y="0" width="${WIDTH}" height="6" fill="${colors.accent}"/>`,
    ...shapes,
    ...paths,
    '</svg>',
  ].join('');
}

function brand(path: (d: string, fill: string) => void, size: number, y: number) {
  const name = shape(fonts.mono, 'inngest', size, MARGIN, y);
  path(name.d, colors.title);
  path(shape(fonts.mono, '-dotnet', size, MARGIN + name.width, y).d, colors.accentText);
}

// The landing page's card: the title on the left, and on the right the hero picture as it shows without motion: the
// checkout function with the charge completed and send-receipt running its retry after a failure.
function renderHomeSvg({ title }: CardContent): string {
  const paths: string[] = [];
  const shapes: string[] = [];
  const path = (d: string, fill: string) => paths.push(`<path d="${d}" fill="${fill}"/>`);
  const left = 72;

  brand(path, 28, 104);
  // Three short lines, as on the page; another title falls back to wrapping.
  const lines = /^Durable functions for /.test(title)
    ? ['Durable', 'functions', title.replace(/^Durable functions /, '')]
    : fit(fonts.display, title, 500, 3, [80, 70, 60]).lines;
  let y = 236;
  for (const line of lines) {
    path(shape(fonts.display, line, 80, left, y, -2.8).d, colors.title);
    y += 82;
  }
  for (const line of ['Write background jobs and workflows', 'as plain C# methods.']) {
    path(shape(fonts.text, line, 25, left, y + 8).d, colors.text);
    y += 35;
  }
  path(shape(fonts.mono, 'Community .NET SDK for Inngest', 17, left, HEIGHT - 52).d, colors.muted);

  const x = 628;
  const codeSize = 13.5;
  const line = 21;
  const codeX = x + 22;
  const drawCode = (text: string, baseline: number, color?: string) => {
    let cursor = codeX;
    for (const run of tokenize(text)) {
      const shaped = shape(fonts.code, run.text, codeSize, cursor, baseline);
      path(shaped.d, color ?? (run.kind ? colors.code[run.kind] : colors.code.plain));
      cursor += shaped.width;
    }
  };
  // A chip: an outlined pill, or a solid one, with upper-case text.
  const chip = (text: string, cx: number, top: number, fg: string, solid?: string) => {
    const label = text.toUpperCase();
    const shaped = shape(fonts.mono, label, 10.5, 0, 0, 0.6);
    const dot = !solid && fg === colors.accentText;
    const width = shaped.width + 18 + (dot ? 11 : 0);
    shapes.push(
      solid
        ? `<rect x="${cx}" y="${top}" width="${width}" height="19" rx="9.5" fill="${solid}"/>`
        : `<rect x="${cx + 0.5}" y="${top + 0.5}" width="${width - 1}" height="18" rx="9" fill="none" stroke="${fg}"/>`,
    );
    if (dot) shapes.push(`<circle cx="${cx + 11}" cy="${top + 9.5}" r="3" fill="${fg}"/>`);
    path(shape(fonts.mono, label, 10.5, cx + 9 + (dot ? 11 : 0), top + 13.5, 0.6).d, solid ? '#ffffff' : fg);
    return width;
  };

  let cy = 132;
  drawCode('// CheckoutFunction.ExecuteAsync', cy + 15, colors.muted);
  cy += line + 10;
  steps.forEach((step, i) => {
    const [state, label] = stillFrame.steps[i];
    const color = state === 'completed' ? colors.done : state === 'idle' ? colors.borderStrong : colors.accentText;
    // The chips above the block.
    let chipX = codeX;
    if (state === 'retrying') chipX += chip('Failed', chipX, cy, '#ffffff', '#c62828') + 6;
    if (label) chip(label, chipX, cy, color);
    const top = cy + 24;
    const height = line * step.lines.length + 8;
    if (state === 'running' || state === 'retrying') {
      shapes.push(`<rect x="${x + 8}" y="${top}" width="${WIDTH - 60 - x - 8}" height="${height}" rx="5" fill="${colors.accent}" fill-opacity="0.13"/>`);
    }
    const edge = state === 'idle' ? `${colors.borderStrong}" stroke-opacity="0.55` : state === 'completed' ? colors.done : colors.accent;
    shapes.push(`<path d="M${x + 14} ${top + 1}H${x + 9}V${top + height - 1}H${x + 14}" fill="none" stroke="${edge}" stroke-width="2" stroke-linejoin="round"/>`);
    step.lines.forEach((text, n) => drawCode(text, top + 18 + n * line));
    cy = top + height + 12;
  });
  drawCode(returnLine, cy + 12);

  return svg(shapes, paths);
}

function renderPageSvg({ title, description, section }: CardContent): string {
  const paths: string[] = [];
  const path = (d: string, fill: string) => paths.push(`<path d="${d}" fill="${fill}"/>`);

  brand(path, 30, 112);

  if (section) {
    const label = section.toUpperCase();
    const width = shape(fonts.mono, label, 20, 0, 0, 2.4).width;
    path(shape(fonts.mono, label, 20, WIDTH - MARGIN - width, 108, 2.4).d, colors.muted);
  }

  const textWidth = WIDTH - 2 * MARGIN;
  const heading = fit(fonts.display, title, textWidth, 3, [68, 60, 52, 46]);
  let y = 230 + heading.size * 0.4;
  for (const line of heading.lines) {
    path(shape(fonts.display, line, heading.size, MARGIN, y, heading.size * -0.02).d, colors.title);
    y += heading.size * 1.12;
  }

  if (description) {
    const body = fit(fonts.text, description, textWidth, heading.lines.length > 2 ? 2 : 3, [28]);
    y += 14;
    for (const line of body.lines) {
      path(shape(fonts.text, line, body.size, MARGIN, y).d, colors.text);
      y += body.size * 1.4;
    }
  }

  // The replay motif: three partial answers and a final one, as Inngest calls a function again and again.
  const chips: [string, string, string][] = [
    ['206', colors.planned, colors.plannedBg],
    ['206', colors.planned, colors.plannedBg],
    ['206', colors.planned, colors.plannedBg],
    ['200', colors.done, colors.doneBg],
  ];
  const chipY = HEIGHT - 76;
  const shapes: string[] = [];
  shapes.push(`<rect x="${MARGIN}" y="${chipY + 17}" width="${4 * 96 - 24}" height="2" fill="${colors.rule}"/>`);
  chips.forEach(([label, fg, bg], i) => {
    const x = MARGIN + i * 96;
    shapes.push(`<rect x="${x}" y="${chipY}" width="72" height="36" rx="8" fill="${bg}" stroke="${fg}" stroke-opacity="0.5"/>`);
    const text = shape(fonts.mono, label, 18, 0, 0);
    path(shape(fonts.mono, label, 18, x + (72 - text.width) / 2, chipY + 24).d, fg);
  });

  const tagline = 'Community .NET SDK for Inngest';
  const taglineWidth = shape(fonts.mono, tagline, 18, 0, 0).width;
  path(shape(fonts.mono, tagline, 18, WIDTH - MARGIN - taglineWidth, chipY + 24).d, colors.muted);

  return svg(shapes, paths);
}

export async function renderCardPng(content: CardContent): Promise<Uint8Array<ArrayBuffer>> {
  const png = await sharp(Buffer.from(renderCardSvg(content))).png({ compressionLevel: 9 }).toBuffer();
  return new Uint8Array(png);
}
