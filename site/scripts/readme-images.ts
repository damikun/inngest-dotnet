// Draws the pictures of the repository's README in the site's dark and light colors, and the social preview GitHub
// shows for the repository. Text is set as paths from the site's own fonts, as on the social cards, so the pictures
// look the same everywhere and carry no fonts. Each glyph is defined once and reused.
//
//   node scripts/readme-images.ts
//
// Writes the banner and the animated run of the hero's steps, each as -dark.svg and -light.svg, and
// social-preview.png to ../.github/assets. The social preview is uploaded by hand, in the repository's settings.
// The architecture figure has a script of its own, architecture-animation.ts.

import { mkdirSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import * as fontkit from 'fontkit';
import sharp from 'sharp';
import { frames, returnLine, stillFrame, steps, tokenize, type StepState } from '../src/data/hero.ts';
import { renderCardSvg } from '../src/lib/og.ts';

const out = join(resolve(dirname(fileURLToPath(import.meta.url)), '..'), '..', '.github', 'assets');
const require = createRequire(import.meta.url);
const load = (file: string) => fontkit.openSync(require.resolve(file)) as fontkit.Font;

const fonts = {
  display: load('@fontsource/geist/files/geist-latin-700-normal.woff2'),
  name: load('@fontsource/figtree/files/figtree-latin-700-normal.woff2'),
  text: load('@fontsource/figtree/files/figtree-latin-500-normal.woff2'),
  mono: load('@fontsource/jetbrains-mono/files/jetbrains-mono-latin-500-normal.woff2'),
  wordmark: load('@fontsource/jetbrains-mono/files/jetbrains-mono-latin-600-normal.woff2'),
};
type Face = keyof typeof fonts;

// A piece of a line of text, in one face and color.
interface Run {
  text: string;
  face?: Face;
  fill?: string;
}

const round = (n: number) => Math.round(n * 100) / 100;

class Picture {
  private readonly glyphs = new Map<string, string>();
  private readonly defs: string[] = [];
  private readonly parts: string[] = [];
  private readonly width: number;
  private readonly height: number;
  private readonly title: string;

  constructor(width: number, height: number, title: string) {
    this.width = width;
    this.height = height;
    this.title = title;
  }

  add(markup: string) {
    this.parts.push(markup);
  }

  def(markup: string) {
    this.defs.push(markup);
  }

  // Where the next part goes, so that parts drawn from here on can be taken back out and placed elsewhere.
  mark(): number {
    return this.parts.length;
  }

  takeSince(mark: number): string {
    return this.parts.splice(mark).join('');
  }

  // The width of a line, without drawing it.
  measure(runs: Run[], size: number, tracking = 0): number {
    return runs.reduce((sum, run) => sum + this.layout(run, size, tracking).width, 0);
  }

  // Draws a line of runs from x, or centered on x, with its baseline at y. Returns its width.
  line(runs: Run[], size: number, x: number, y: number, fill: string, { center = false, tracking = 0 } = {}): number {
    const width = this.measure(runs, size, tracking);
    let cursor = center ? x - width / 2 : x;
    for (const run of runs) {
      const { width: advance, uses } = this.layout(run, size, tracking);
      const scale = size / fonts[run.face ?? 'text'].unitsPerEm;
      if (uses.length > 0) {
        this.parts.push(
          `<g fill="${run.fill ?? fill}" transform="translate(${round(cursor)} ${round(y)}) scale(${round(scale * 1e4) / 1e4} ${-round(scale * 1e4) / 1e4})">${uses.join('')}</g>`,
        );
      }
      cursor += advance;
    }
    return width;
  }

  private layout(run: Run, size: number, tracking: number) {
    const face = run.face ?? 'text';
    const font = fonts[face];
    const scale = size / font.unitsPerEm;
    // Code is set without ligatures, so "=>" and "/" sequences stay as typed.
    const glyphRun = font.layout(run.text, face === 'mono' || face === 'wordmark' ? { calt: false, liga: false } : undefined);
    const uses: string[] = [];
    let units = 0;
    glyphRun.glyphs.forEach((glyph, i) => {
      const position = glyphRun.positions[i];
      const id = this.glyph(face, glyph);
      if (id) uses.push(`<use href="#${id}" x="${Math.round(units + position.xOffset)}"/>`);
      units += position.xAdvance + tracking / scale;
    });
    return { width: units * scale, uses };
  }

  // Glyphs are keyed by face and outline; a glyph without an outline, such as a space, gets no id.
  private glyph(face: Face, glyph: fontkit.Glyph): string | undefined {
    const d = glyph.path.toSVG().replace(/-?\d+\.\d+/g, (n) => String(Math.round(Number(n))));
    if (!d) return undefined;
    const key = `${face}:${d}`;
    if (!this.glyphs.has(key)) {
      const id = `${face}${this.glyphs.size}`;
      this.glyphs.set(key, id);
      this.defs.push(`<path id="${id}" d="${d}"/>`);
    }
    return this.glyphs.get(key);
  }

  // The size can be given here when it depends on what was measured while drawing.
  svg(width = this.width, height = this.height): string {
    return [
      `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}" role="img">`,
      `<title>${this.title}</title>`,
      `<defs>${this.defs.join('')}</defs>`,
      ...this.parts,
      '</svg>',
    ].join('');
  }
}

interface Box {
  x: number;
  y: number;
  w: number;
  h: number;
}

// The colors of the landing page's hero, from src/styles/theme.css.
interface HeroColors {
  frame: string;
  frameBorder: string;
  code: string;
  comment: string;
  keyword: string;
  string: string;
  type: string;
  method: string;
  edge: string;
  coral: string;
  coralText: string;
  ok: string;
  wait: string;
  fail: string;
}

const heroDark: HeroColors = {
  frame: '#232120',
  frameBorder: '#3a3735',
  code: '#e8e5e2',
  comment: '#a8a29d',
  keyword: '#ad9ffc',
  string: '#66bd8b',
  type: '#9cd2ff',
  method: '#fd8a72',
  edge: '#7d7671',
  coral: '#f65c4f',
  coralText: '#fd8a72',
  ok: '#66bd8b',
  wait: '#ad9ffc',
  fail: '#c62828',
};

const heroLight: HeroColors = {
  frame: '#f2efeb',
  frameBorder: '#e2ddd8',
  code: '#1b1a19',
  comment: '#625c57',
  keyword: '#6222df',
  string: '#016239',
  type: '#1450b1',
  method: '#b8351c',
  edge: '#8f8882',
  coral: '#f65c4f',
  coralText: '#b8351c',
  ok: '#027a48',
  wait: '#6222df',
  fail: '#c62828',
};

// The landing page's hero as an animated picture: CheckoutFunction with the state of each step as Inngest runs it,
// from the same frames as the hero (src/data/hero.ts). Every state a step shows is drawn once and faded in and out by
// a CSS animation on one timeline, so the steps change together. Without animation, or with reduced motion, the
// picture keeps the hero's still frame: the retry of send-receipt.
function runSteps(c: HeroColors): string {
  const code = 15.5;
  const leading = 27;
  const chip = 11.5;
  const chipTracking = chip * 0.06;
  const chipHeight = 22;
  const chipPad = 8;
  const dot = 3.4;
  const pad = 26;
  const blockGap = 12;
  const bodyPad = 3;
  const bracketWidth = 12;
  const radius = 5;
  const fade = 250;

  const p = new Picture(
    0,
    0,
    'CheckoutFunction as Inngest runs it, step by step. The charge runs and completes. Send-receipt fails, and Inngest retries that step alone while the charge stays completed; the retry succeeds. The run then sleeps for 30 minutes, and ship runs.',
  );

  type Variant = { step: number; state: StepState; label: string; frames: boolean[] };
  const variants: Variant[] = [];
  frames.forEach((frame, f) =>
    frame.steps.forEach(([state, label], step) => {
      if (state === 'idle') return;
      let variant = variants.find((v) => v.step === step && v.state === state && v.label === label);
      if (!variant) {
        variant = { step, state, label: label ?? '', frames: frames.map(() => false) };
        variants.push(variant);
      }
      variant.frames[f] = true;
    }),
  );
  const still = (v: Variant) => stillFrame.steps[v.step][0] === v.state && stillFrame.steps[v.step][1] === v.label;

  const chipWidth = (text: string, withDot: boolean) =>
    chipPad * 2 + (withDot ? dot * 2 + 5 : 0) + p.measure([{ text: text.toUpperCase(), face: 'wordmark' }], chip, chipTracking);
  const moving = (state: StepState) => state === 'running' || state === 'retrying';
  const gutter = Math.ceil(Math.max(...variants.map((v) => chipWidth(v.label, moving(v.state)))));

  const tokenColor = { string: c.string, keyword: c.keyword, type: c.type, method: c.method } as const;
  const codeRuns = (line: string): Run[] =>
    tokenize(line).map((run) => ({ text: run.text, face: 'mono', fill: run.kind ? tokenColor[run.kind] : c.code }));
  const comment = '// CheckoutFunction.ExecuteAsync';
  const lines = [comment, returnLine, ...steps.flatMap((step) => step.lines)];
  const widest = Math.max(...lines.map((line) => p.measure([{ text: line, face: 'mono' }], code)));

  const bracketX = pad + gutter + 12;
  const bodyX = bracketX + bracketWidth;
  const codeX = bodyX + 8;
  const blockHeight = 2 * leading + 2 * bodyPad;
  const blockTop = (i: number) => pad + leading + blockGap / 2 + i * (blockHeight + blockGap);
  const returnTop = blockTop(steps.length) - blockGap / 2;
  const W = Math.ceil(codeX + widest + 12 + pad);
  const H = returnTop + leading + pad;
  const right = W - pad;
  const baseline = (top: number) => top + leading / 2 + code * 0.35;

  const total = frames.reduce((sum, frame) => sum + frame.ms, 0);
  const starts = frames.map((_, i) => frames.slice(0, i).reduce((sum, frame) => sum + frame.ms, 0));
  const keyframes = (shown: boolean[]) => {
    const points: [number, number][] = [[0, Number(shown[0])]];
    for (let i = 1; i < shown.length; i++) {
      if (shown[i] !== shown[i - 1]) points.push([starts[i], Number(shown[i - 1])], [starts[i] + fade, Number(shown[i])]);
    }
    if (shown.at(-1) !== shown[0]) points.push([total - fade, Number(shown.at(-1))]);
    points.push([total, Number(shown[0])]);
    return points.map(([ms, opacity]) => `${round((ms / total) * 100)}%{opacity:${opacity}}`).join('');
  };

  const animations = variants.map((v, i) => `.v${i}{animation:v${i} ${total / 1000}s linear infinite}@keyframes v${i}{${keyframes(v.frames)}}`);
  p.def(
    `<style>@media (prefers-reduced-motion:no-preference){${animations.join('')}.s{animation-timing-function:step-end}.dot{animation:pulse 1.2s ease-in-out infinite}@keyframes pulse{50%{opacity:.35}}}</style>`,
  );
  // Colors fade, as on the site; chips change at once, so two labels never show on top of each other.
  const layer = (v: Variant, markup: string, instant = false) =>
    `<g class="v${variants.indexOf(v)}${instant ? ' s' : ''}" opacity="${still(v) ? 1 : 0}">${markup}</g>`;

  const bracket = (top: number, stroke: string, opacity = 1) => {
    const y1 = top + 1;
    const y2 = top + blockHeight - 1;
    return `<path d="M${bodyX} ${y1}H${bracketX + radius}A${radius} ${radius} 0 0 0 ${bracketX} ${y1 + radius}V${y2 - radius}A${radius} ${radius} 0 0 0 ${bracketX + radius} ${y2}H${bodyX}" fill="none" stroke="${stroke}" stroke-width="2"${opacity < 1 ? ` stroke-opacity="${opacity}"` : ''}/>`;
  };
  const body = (top: number, fill: string, opacity: number) =>
    `<path d="M${bodyX} ${top}H${right - radius}A${radius} ${radius} 0 0 1 ${right} ${top + radius}V${top + blockHeight - radius}A${radius} ${radius} 0 0 1 ${right - radius} ${top + blockHeight}H${bodyX}Z" fill="${fill}" fill-opacity="${opacity}"/>`;
  const edge = (state: StepState) =>
    ({ idle: c.edge, running: c.coral, retrying: c.coral, failed: c.fail, completed: c.ok, sleeping: c.wait })[state];

  p.add(`<rect x="0.5" y="0.5" width="${W - 1}" height="${H - 1}" rx="14" fill="${c.frame}" stroke="${c.frameBorder}"/>`);

  // Tints go under the code: a running step in coral, a failed one in red.
  for (const v of variants) {
    if (moving(v.state)) p.add(layer(v, body(blockTop(v.step), c.coral, 0.13)));
    if (v.state === 'failed') p.add(layer(v, body(blockTop(v.step), c.fail, 0.2)));
  }

  steps.forEach((_, i) => p.add(bracket(blockTop(i), c.edge, 0.55)));
  p.line([{ text: comment, face: 'mono' }], code, codeX, baseline(pad), c.comment);
  steps.forEach((step, i) =>
    step.lines.forEach((line, j) => p.line(codeRuns(line), code, codeX, baseline(blockTop(i) + bodyPad + j * leading), c.code)),
  );
  p.line(codeRuns(returnLine), code, codeX, baseline(returnTop), c.code);

  // A chip ends at the gutter's right edge. Returns its markup, drawn on a picture of its own so its glyphs stay shared.
  const drawChip = (text: string, y: number, color: string, { filled = false, withDot = false } = {}) => {
    const width = chipWidth(text, withDot);
    const x = pad + gutter - width;
    const parts = [
      `<rect x="${round(x + 0.5)}" y="${y + 0.5}" width="${round(width - 1)}" height="${chipHeight - 1}" rx="${(chipHeight - 1) / 2}" fill="${filled ? c.fail : 'none'}" stroke="${filled ? c.fail : color}"/>`,
    ];
    let textX = x + chipPad;
    if (withDot) {
      parts.push(`<circle class="dot" cx="${round(textX + dot)}" cy="${y + chipHeight / 2}" r="${dot}" fill="${color}"/>`);
      textX += dot * 2 + 5;
    }
    const before = p.mark();
    p.line([{ text: text.toUpperCase(), face: 'wordmark' }], chip, textX, y + chipHeight / 2 + chip * 0.36, filled ? '#ffffff' : color, {
      tracking: chipTracking,
    });
    return parts.join('') + p.takeSince(before);
  };

  for (const v of variants) {
    const top = blockTop(v.step);
    const middle = top + blockHeight / 2;
    p.add(layer(v, bracket(top, edge(v.state))));
    let markup = '';
    if (v.state === 'retrying') {
      const first = middle - chipHeight - 2;
      markup += drawChip('Failed', first, c.fail, { filled: true });
      markup += drawChip(v.label, first + chipHeight + 4, c.coralText, { withDot: true });
    } else if (v.state === 'failed') {
      markup += drawChip(v.label, middle - chipHeight / 2, c.fail, { filled: true });
    } else {
      const color = moving(v.state) ? c.coralText : v.state === 'sleeping' ? c.wait : c.ok;
      markup += drawChip(v.label, middle - chipHeight / 2, color, { withDot: moving(v.state) });
    }
    p.add(layer(v, markup, true));
  }

  return p.svg(W, H);
}

// The landing page's hero as a banner: the wordmark, the title and the row of facts on the hero's band, with its
// coral glow and grain.
interface BannerColors {
  background: string;
  border: string;
  title: string;
  suffix: string;
  facts: string;
  grain: number;
  grainOpacity: number;
}

function banner(c: BannerColors): string {
  const W = 960;
  const H = 248;
  const p = new Picture(W, H, 'inngest-dotnet: durable functions for .NET');
  p.def(
    `<radialGradient id="glow" gradientUnits="userSpaceOnUse" cx="480" cy="-60" r="340" gradientTransform="translate(480 -60) scale(1.85 1) translate(-480 60)"><stop offset="0" stop-color="#f65c4f" stop-opacity="0.26"/><stop offset="0.72" stop-color="#f65c4f" stop-opacity="0"/></radialGradient>`,
  );
  p.def(
    `<filter id="grain" x="0" y="0" width="100%" height="100%"><feTurbulence type="fractalNoise" baseFrequency="0.9" numOctaves="3" stitchTiles="stitch"/><feColorMatrix values="0 0 0 0 ${c.grain} 0 0 0 0 ${c.grain} 0 0 0 0 ${c.grain} 0 0 0 1 0"/></filter>`,
  );
  p.def(`<clipPath id="band"><rect width="${W}" height="${H}" rx="14"/></clipPath>`);
  p.add(`<g clip-path="url(#band)"><rect width="${W}" height="${H}" fill="${c.background}"/><rect width="${W}" height="${H}" fill="url(#glow)"/>`);
  p.add(`<rect width="${W}" height="${H}" filter="url(#grain)" opacity="${c.grainOpacity}"/></g>`);
  p.add(`<rect x="0.5" y="0.5" width="${W - 1}" height="${H - 1}" rx="13.5" fill="none" stroke="${c.border}"/>`);

  p.line([{ text: 'inngest', face: 'wordmark' }, { text: '-dotnet', face: 'wordmark', fill: c.suffix }], 21, W / 2, 62, c.title, { center: true });
  p.line([{ text: 'Durable functions for .NET', face: 'display' }], 58, W / 2, 138, c.title, { center: true, tracking: -58 * 0.035 });

  // The facts, each after a coral dot, in two centered rows.
  const rows = [
    ['.NET 8 and .NET 10', 'ASP.NET Core or Connect', 'Native AOT'],
    ['2,126 unit tests', '204 end-to-end tests', 'Apache 2.0'],
  ];
  const size = 13;
  const tracking = size * 0.04;
  const dot = 15;
  const gap = 26;
  rows.forEach((facts, row) => {
    const widths = facts.map((fact) => p.measure([{ text: fact, face: 'mono' }], size, tracking));
    const total = widths.reduce((sum, w) => sum + dot + w, 0) + gap * (facts.length - 1);
    let x = (W - total) / 2;
    const y = 186 + row * 25;
    facts.forEach((fact, i) => {
      p.add(`<circle cx="${round(x + 3)}" cy="${y - 4.5}" r="3" fill="#f65c4f"/>`);
      p.line([{ text: fact, face: 'mono' }], size, x + dot, y, c.facts, { tracking });
      x += dot + widths[i] + gap;
    });
  });
  return p.svg();
}

// GitHub's social preview is 1280 by 640. It shows the landing page's social card, widened with the card's own
// background.
async function socialPreview(): Promise<Buffer> {
  const card = renderCardSvg({ title: 'Durable functions for .NET', home: true })
    .replace('width="1200" height="630" viewBox="0 0 1200 630"', 'width="1280" height="640" viewBox="-40 -5 1280 640"')
    .replaceAll('<rect width="1200" height="630"', '<rect x="-40" y="-5" width="1280" height="640"')
    .replace('<rect x="0" y="0" width="1200" height="6"', '<rect x="-40" y="-5" width="1280" height="6"');
  return sharp(Buffer.from(card)).png({ compressionLevel: 9 }).toBuffer();
}

mkdirSync(out, { recursive: true });
writeFileSync(
  join(out, 'banner-dark.svg'),
  banner({ background: '#1b1a19', border: '#2c2a28', title: '#f6f6f6', suffix: '#fd8a72', facts: '#a8a29d', grain: 1, grainOpacity: 0.07 }),
);
writeFileSync(
  join(out, 'banner-light.svg'),
  banner({ background: '#faf9f7', border: '#e2ddd8', title: '#1b1a19', suffix: '#b8351c', facts: '#625c57', grain: 0, grainOpacity: 0.05 }),
);
writeFileSync(join(out, 'how-a-run-works-dark.svg'), runSteps(heroDark));
writeFileSync(join(out, 'how-a-run-works-light.svg'), runSteps(heroLight));
writeFileSync(join(out, 'social-preview.png'), await socialPreview());
