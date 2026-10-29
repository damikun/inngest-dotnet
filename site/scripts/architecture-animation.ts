// The landing page's architecture figure as an animated picture, in the site's dark and light colors, for the landing
// page and the README. It plays one run over HTTP: an event reaches Inngest, Inngest calls the app once per step, a
// step that fails is retried after a backoff, and the last call returns the output. The Connect lane stays still.
//
// It follows scripts/readme-images.ts: text is set as outlines from the site's own fonts, so the pictures carry no
// fonts and look the same everywhere, and every state is drawn once and faded in and out by CSS animations on one
// timeline. Without animation, or with reduced motion, the picture shows the end of the run.
//
//   node scripts/architecture-animation.ts
//
// Writes architecture-dark.svg and architecture-light.svg to src/assets.

import { mkdirSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const out = join(resolve(dirname(fileURLToPath(import.meta.url)), '..'), 'src', 'assets');
const require = createRequire(import.meta.url);
const fontkit = require('fontkit');
const load = (file: string) => fontkit.openSync(require.resolve(file));

const fonts = {
  name: load('@fontsource/figtree/files/figtree-latin-700-normal.woff2'),
  text: load('@fontsource/figtree/files/figtree-latin-500-normal.woff2'),
  mono: load('@fontsource/jetbrains-mono/files/jetbrains-mono-latin-500-normal.woff2'),
  chip: load('@fontsource/jetbrains-mono/files/jetbrains-mono-latin-600-normal.woff2'),
};
type Face = keyof typeof fonts;

// The colors of the README's architecture picture, from src/styles/theme.css, plus the failure red.
interface Palette {
  frame: string;
  frameBorder: string;
  node: string;
  border: string;
  borderStrong: string;
  accentBorder: string;
  name: string;
  text: string;
  muted: string;
  code: string;
  icon: string;
  arrow: string;
  coral: string;
  plan: string;
  ok: string;
  run: string;
  fail: string;
  // The failed answer's arrow and message, a red that stands apart from the 206 arrow.
  failLine: string;
  // The bright coral of the glow around a lit card.
  glow: string;
}

const dark: Palette = {
  frame: '#232120',
  frameBorder: '#3a3735',
  node: '#2c2a28',
  border: '#3a3735',
  borderStrong: '#7d7671',
  accentBorder: '#a14b43',
  name: '#f6f6f6',
  text: '#d6d2ce',
  muted: '#a8a29d',
  code: '#e8e5e2',
  icon: '#fd8a72',
  arrow: '#d6d2ce',
  coral: '#fd8a72',
  plan: '#fcc43f',
  ok: '#66bd8b',
  run: '#52b2fd',
  fail: '#fa8d86',
  failLine: '#fa8d86',
  glow: '#f65c4f',
};

const light: Palette = {
  frame: '#f2efeb',
  frameBorder: '#e2ddd8',
  node: '#ffffff',
  border: '#e2ddd8',
  borderStrong: '#8f8882',
  accentBorder: '#ed968d',
  name: '#1b1a19',
  text: '#3b3835',
  muted: '#625c57',
  code: '#2c2a28',
  icon: '#b8351c',
  arrow: '#3b3835',
  coral: '#b8351c',
  plan: '#95360f',
  ok: '#027a48',
  run: '#1450b1',
  fail: '#a52015',
  failLine: '#c62828',
  glow: '#f65c4f',
};

// The story, in seconds from the start of the loop. A message takes TRAVEL seconds along an arrow.
const TRAVEL = 0.75;
const story = {
  fire: 0.3, // the event source sends the event
  event: 0.45, // the event leaves for Inngest, and takes 0.6s
  lit: 1.05, // Inngest starts the run
  call1: 1.45, // POST: the app runs charge
  answer1: 3.1, // 206 with the step result
  call2: 4.2, // POST: the app runs send-receipt
  fail: 5.85, // send-receipt throws
  answer2: 6.1, // 206 with the step error
  backoff: 6.95, // Inngest waits before retry 1, until call3
  call3: 8.7, // POST: send-receipt again
  answer3: 10.2, // 206 with the step result
  call4: 11.3, // POST: the function returns
  answer4: 12.75, // 200 with the output
  reset: 15.4, // everything has faded back by here
  total: 16,
};
const arrive = (start: number) => start + TRAVEL;
const FADE = 0.25;
const EASE = 'cubic-bezier(.45,0,.4,1)';

const title =
  'An event reaches Inngest, which starts a run and calls your ASP.NET Core app over HTTP, once per step. The app runs ' +
  'the charge step and answers 206 with its result. The send-receipt step fails, so the app answers 206 with the step ' +
  'error, and Inngest retries that step after a backoff. The retry succeeds, the last call answers 200 with the ' +
  'output, and the run is completed. Inngest can also send executions to a Connect worker over a WebSocket the worker ' +
  'opened.';

// A piece of a line of text, in one face and color.
interface Run {
  text: string;
  face?: Face;
  fill?: string;
}

const round = (n: number) => Math.round(n * 100) / 100;

// Rewrites a glyph's outline, absolute and in whole font units, with relative commands, which are shorter.
function compact(d: string): string {
  const tokens = d.match(/[MLQCZ]|-?\d+/g) ?? [];
  let out = '';
  let last = '';
  let x = 0;
  let y = 0;
  let startX = 0;
  let startY = 0;
  const emit = (cmd: string, values: number[]) => {
    // A repeated command can leave out its letter, except a move or a close.
    let text = cmd === last && cmd !== 'm' && cmd !== 'z' ? '' : cmd;
    values.forEach((v, i) => {
      // A letter or a minus sign already separates a number from what comes before it.
      text += (i === 0 && text) || v < 0 ? String(v) : ` ${v}`;
    });
    out += text;
    last = cmd;
  };
  for (let i = 0; i < tokens.length; ) {
    const cmd = tokens[i++];
    const take = (n: number) => tokens.slice(i, (i += n)).map(Number);
    if (cmd === 'M') {
      const [nx, ny] = take(2);
      emit('m', [nx - x, ny - y]);
      x = startX = nx;
      y = startY = ny;
    } else if (cmd === 'L') {
      const [nx, ny] = take(2);
      if (ny === y) emit('h', [nx - x]);
      else if (nx === x) emit('v', [ny - y]);
      else emit('l', [nx - x, ny - y]);
      x = nx;
      y = ny;
    } else if (cmd === 'Q') {
      const [x1, y1, nx, ny] = take(4);
      emit('q', [x1 - x, y1 - y, nx - x, ny - y]);
      x = nx;
      y = ny;
    } else if (cmd === 'C') {
      const [x1, y1, x2, y2, nx, ny] = take(6);
      emit('c', [x1 - x, y1 - y, x2 - x, y2 - y, nx - x, ny - y]);
      x = nx;
      y = ny;
    } else if (cmd === 'Z') {
      emit('z', []);
      x = startX;
      y = startY;
    } else {
      throw new Error(`unexpected path command ${cmd}`);
    }
  }
  return out;
}

// Text as glyph outlines, each glyph defined once and reused, as in site/scripts/readme-images.ts.
class Picture {
  private readonly glyphs = new Map<string, string>();
  private readonly defs: string[] = [];
  private readonly parts: string[] = [];

  add(markup: string) {
    this.parts.push(markup);
  }

  mark(): number {
    return this.parts.length;
  }

  takeSince(mark: number): string {
    return this.parts.splice(mark).join('');
  }

  measure(runs: Run[], size: number, tracking = 0): number {
    return runs.reduce((sum, run) => sum + this.layout(run, size, tracking).width, 0);
  }

  // Draws a line of runs from x, or centered on x, with its baseline at y. Returns its width.
  line(runs: Run[], size: number, x: number, y: number, fill: string, { center = false, tracking = 0 } = {}): number {
    const width = this.measure(runs, size, tracking);
    let cursor = center ? x - width / 2 : x;
    for (const run of runs) {
      const { width: advance, uses } = this.layout(run, size, tracking);
      const scale = Math.round((size / fonts[run.face ?? 'text'].unitsPerEm) * 1e4) / 1e4;
      if (uses.length > 0) {
        this.parts.push(
          `<g fill="${run.fill ?? fill}" transform="translate(${round(cursor)} ${round(y)}) scale(${scale} ${-scale})">${uses.join('')}</g>`,
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
    // Code is set without ligatures, so "/" sequences stay as typed.
    const glyphRun = font.layout(run.text, face === 'mono' || face === 'chip' ? { calt: false, liga: false } : undefined);
    const uses: string[] = [];
    let units = 0;
    glyphRun.glyphs.forEach((glyph: { path: { toSVG(): string } }, i: number) => {
      const position = glyphRun.positions[i];
      const id = this.glyph(face, glyph);
      if (id) uses.push(`<use href="#${id}" x="${Math.round(units + position.xOffset)}"/>`);
      units += position.xAdvance + tracking / scale;
    });
    return { width: units * scale, uses };
  }

  // Glyphs are keyed by face and outline; a glyph without an outline, such as a space, gets no id.
  private glyph(face: Face, glyph: { path: { toSVG(): string } }): string | undefined {
    const d = glyph.path.toSVG().replace(/-?\d+\.\d+/g, (n) => String(Math.round(Number(n))));
    if (!d) return undefined;
    const key = `${face}:${d}`;
    if (!this.glyphs.has(key)) {
      const id = `g${this.glyphs.size}`;
      this.glyphs.set(key, id);
      this.defs.push(`<path id="${id}" d="${compact(d)}"/>`);
    }
    return this.glyphs.get(key);
  }

  svg(width: number, height: number, label: string, style: string): string {
    return [
      `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}" role="img">`,
      `<title>${label}</title>`,
      `<defs><style>${style}</style>${this.defs.join('')}</defs>`,
      ...this.parts,
      '</svg>',
    ].join('');
  }
}

// CSS animations that all run on the loop's one timeline, so nothing drifts apart. Each set of keyframes is written
// once and named by the class that plays it.
class Timeline {
  private readonly total: number;
  private readonly names = new Map<string, string>();
  readonly rules: string[] = [];

  constructor(total: number) {
    this.total = total;
  }

  // Keyframes as [seconds, declarations, timing function of the stretch that starts there]. Returns the class.
  keyframes(frames: [number, string, string?][]): string {
    for (let i = 1; i < frames.length; i++) {
      if (frames[i][0] < frames[i - 1][0]) throw new Error(`keyframes out of order: ${JSON.stringify(frames)}`);
    }
    const at = (s: number) => `${round((s / this.total) * 100)}%`;
    const body = frames.map(([s, decl, ease]) => `${at(s)}{${decl}${ease ? `;animation-timing-function:${ease}` : ''}}`).join('');
    let name = this.names.get(body);
    if (!name) {
      name = `a${this.names.size}`;
      this.names.set(body, name);
      this.rules.push(`.${name}{animation:${name} ${this.total}s linear infinite}@keyframes ${name}{${body}}`);
    }
    return name;
  }

  // Shown during each interval: it fades in from the start of the interval and is gone by its end.
  shown(intervals: [number, number][], fade = FADE): string {
    return this.opacity(intervals, fade, 0, 1);
  }

  // Hidden during each interval, the other way around.
  hidden(intervals: [number, number][], fade = FADE): string {
    return this.opacity(intervals, fade, 1, 0);
  }

  // A glow that flares up at a moment and dies away.
  flash(at: number, rise = 0.15, fall = 0.8): string {
    return this.keyframes([
      [0, 'opacity:0'],
      [at, 'opacity:0'],
      [at + rise, 'opacity:1'],
      [at + rise + fall, 'opacity:0'],
      [this.total, 'opacity:0'],
    ]);
  }

  private opacity(intervals: [number, number][], fade: number, outside: number, inside: number): string {
    const frames: [number, string][] = [[0, `opacity:${outside}`]];
    for (const [from, to] of intervals) {
      if (to - from < 2 * fade) throw new Error(`interval ${from}-${to} is shorter than its fades`);
      frames.push([from, `opacity:${outside}`], [from + fade, `opacity:${inside}`], [to - fade, `opacity:${inside}`], [to, `opacity:${outside}`]);
    }
    frames.push([this.total, `opacity:${outside}`]);
    return this.keyframes(frames);
  }
}

// Icons of the architecture figure, on a 24 by 24 grid, stroked.
const icons = {
  source: '<path d="M13 2 4 14h7l-1 8 9-12h-7z"/>',
  inngest: '<path d="M7 18h10a4 4 0 0 0 .6-7.95A6 6 0 0 0 6.2 9.2 4.4 4.4 0 0 0 7 18z"/>',
  app: '<rect x="3" y="4" width="18" height="16" rx="2.5"/><path d="M3 9h18M6.5 6.5h.01M9.5 6.5h.01M8 14l2 2-2 2M12 18h4"/>',
  worker: '<rect x="6" y="6" width="12" height="12" rx="2"/><path d="M9 2v4M15 2v4M9 18v4M15 18v4M2 9h4M2 15h4M18 9h4M18 15h4"/>',
  store:
    '<ellipse cx="12" cy="5" rx="7.5" ry="2.8"/><path d="M4.5 5v14c0 1.6 3.4 2.8 7.5 2.8s7.5-1.2 7.5-2.8V5"/><path d="M4.5 12c0 1.6 3.4 2.8 7.5 2.8s7.5-1.2 7.5-2.8"/>',
};

const icon = (markup: string, cx: number, top: number, size: number, stroke: string, width = 1.7) =>
  `<g transform="translate(${round(cx - size / 2)} ${round(top)}) scale(${round(size / 24)})" fill="none" stroke="${stroke}" stroke-width="${width}" stroke-linecap="round" stroke-linejoin="round">${markup}</g>`;

interface Box {
  x: number;
  y: number;
  w: number;
  h: number;
}

type Mark = 'dot' | 'check' | 'cross';

function architecture(c: Palette): string {
  const W = 960;
  const H = 368;
  const pad = 24;
  const p = new Picture();
  const t = new Timeline(story.total);

  const source: Box = { x: pad, y: (H - 122) / 2, w: 150, h: 122 };
  const inngest: Box = { x: 262, y: pad, w: 238, h: H - 2 * pad };
  const app: Box = { x: 716, y: 52, w: W - pad - 716, h: 140 };
  const worker: Box = { x: 716, y: 234, w: W - pad - 716, h: 110 };
  const gap = 8;

  // Draws with fn and returns the markup instead of adding it, so it can be wrapped in an animated group.
  const draw = (fn: () => void): string => {
    const mark = p.mark();
    fn();
    return p.takeSince(mark);
  };
  // A group that a class animates. Its opacity attribute is the still frame: the end of the run.
  const layer = (cls: string, still: 0 | 1, markup: string) =>
    p.add(`<g class="${cls}"${still ? '' : ' opacity="0"'}>${markup}</g>`);

  p.add(`<rect x="0.5" y="0.5" width="${W - 1}" height="${H - 1}" rx="14" fill="${c.frame}" stroke="${c.frameBorder}"/>`);

  const node = (box: Box, dashed = false) =>
    p.add(
      `<rect x="${box.x + 0.5}" y="${box.y + 0.5}" width="${box.w - 1}" height="${box.h - 1}" rx="12" fill="${c.node}" stroke="${dashed ? c.borderStrong : c.border}"${dashed ? ' stroke-dasharray="5 4"' : ''}/>`,
    );
  // A lit card: the accent border of the site's figure and a soft ring around it.
  const lit = (box: Box, color: string, border?: string) =>
    `<rect x="${box.x - 2.5}" y="${box.y - 2.5}" width="${box.w + 5}" height="${box.h + 5}" rx="14.5" fill="none" stroke="${color}" stroke-opacity="0.14" stroke-width="4"/>` +
    `<rect x="${box.x + 0.5}" y="${box.y + 0.5}" width="${box.w - 1}" height="${box.h - 1}" rx="12" fill="none" stroke="${border ?? color}"${border ? '' : ' stroke-opacity="0.75"'}/>`;
  // The wider ring of a flash.
  const flare = (box: Box, color: string) =>
    `<rect x="${box.x - 4}" y="${box.y - 4}" width="${box.w + 8}" height="${box.h + 8}" rx="16" fill="none" stroke="${color}" stroke-opacity="0.35" stroke-width="7"/>`;

  // Writes the lines of a node's detail centered under its name, and checks that they fit.
  const detail = (box: Box, lines: Run[][], top: number, size = 13, leading = 18) => {
    lines.forEach((runs, i) => {
      const width = p.measure(runs, size);
      if (width > box.w - 24) throw new Error(`"${runs.map((r) => r.text).join('')}" is ${Math.round(width)}px wide`);
      p.line(runs, size, box.x + box.w / 2, top + i * leading, c.muted, { center: true });
    });
  };

  // A state mark centered on (x, y): a pulsing dot, a check, or a cross.
  const mark = (kind: Mark, x: number, y: number, color: string, cls = '') => {
    if (kind === 'dot') return `<circle class="dot" cx="${round(x)}" cy="${round(y)}" r="3.4" fill="${color}"/>`;
    const d = kind === 'check' ? `M${round(x - 4.5)} ${round(y + 0.5)}l3 3 6-7` : `M${round(x - 3)} ${round(y - 3)}l6 6m0-6-6 6`;
    const dash = cls ? ` class="${cls}" stroke-dasharray="14"` : '';
    return `<path${dash} d="${d}" fill="none" stroke="${color}" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/>`;
  };
  // A check that draws itself in when its row appears.
  const tick = (at: number) =>
    t.keyframes([
      [0, 'stroke-dashoffset:14'],
      [at + 0.1, 'stroke-dashoffset:14', 'ease-out'],
      [at + 0.45, 'stroke-dashoffset:0'],
      [story.total, 'stroke-dashoffset:0'],
    ]);

  // A chip like the hero's: uppercase, outlined, with a pulsing dot while something runs. Centered on cx.
  const chip = (label: string, cx: number, top: number, color: string, withDot: boolean) =>
    draw(() => {
      const text = label.toUpperCase();
      const size = 11.5;
      const tracking = size * 0.06;
      const height = 22;
      const dot = 3.4;
      const width = 16 + (withDot ? dot * 2 + 5 : 0) + p.measure([{ text, face: 'chip' }], size, tracking);
      const x = cx - width / 2;
      p.add(
        `<rect x="${round(x + 0.5)}" y="${top + 0.5}" width="${round(width - 1)}" height="${height - 1}" rx="${(height - 1) / 2}" fill="none" stroke="${color}"/>`,
      );
      let textX = x + 8;
      if (withDot) {
        p.add(mark('dot', textX + dot, top + height / 2, color));
        textX += dot * 2 + 5;
      }
      p.line([{ text, face: 'chip' }], size, textX, top + height / 2 + size * 0.36, color, { tracking });
    });

  // A status pill: a tinted, rounded band with a mark and a short line. Centered on (cx, cy).
  const pill = (runs: Run[], cx: number, cy: number, color: string, kind: Mark, maxWidth: number) =>
    draw(() => {
      const size = 12.5;
      const width = 34 + p.measure(runs, size);
      if (width > maxWidth) throw new Error(`"${runs.map((r) => r.text).join('')}" pill is ${Math.round(width)}px wide`);
      const x = cx - width / 2;
      p.add(
        `<rect x="${round(x)}" y="${cy - 11}" width="${round(width)}" height="22" rx="11" fill="${color}" fill-opacity="0.13" stroke="${color}" stroke-opacity="0.45"/>`,
      );
      p.add(mark(kind, x + 14, cy, color));
      p.line(runs, size, x + 24, cy + 4.3, color);
    });

  // An arrow from x1 to x2 at height y, and its label above it, drawn apart so the label can change.
  const arrowLine = (x1: number, x2: number, y: number, color: string, forward: boolean, dashed: boolean) =>
    `<path d="M${forward ? x1 : x1 + 7} ${y}H${forward ? x2 - 7 : x2}" stroke="${color}" stroke-width="2"${dashed ? ' stroke-dasharray="5 4"' : ''}/>` +
    (forward ? `<path d="M${x2} ${y}l-9-6v12z" fill="${color}"/>` : `<path d="M${x1} ${y}l9-6v12z" fill="${color}"/>`);
  const arrowLabel = (x1: number, x2: number, y: number, label: string, fill: string) =>
    draw(() => {
      const width = p.measure([{ text: label, face: 'mono' }], 12);
      if (width > x2 - x1) throw new Error(`"${label}" is ${Math.round(width)}px wide`);
      p.line([{ text: label, face: 'mono' }], 12, (x1 + x2) / 2, y - 8, fill, { center: true });
    });
  const arrow = (x1: number, x2: number, y: number, label: string, color: string, forward: boolean, dashed: boolean) =>
    p.add(arrowLabel(x1, x2, y, label, c.code) + arrowLine(x1, x2, y, color, forward, dashed));

  // A message: a dot with a halo that travels from x1 to x2 along an arrow, leaving at start.
  const pulses: string[] = [];
  const pulse = (x1: number, x2: number, y: number, color: string, start: number, travel = TRAVEL) => {
    const shown = t.keyframes([
      [0, 'opacity:0'],
      [start, 'opacity:0'],
      [start + 0.08, 'opacity:1'],
      [start + travel - 0.1, 'opacity:1'],
      [start + travel, 'opacity:0'],
      [story.total, 'opacity:0'],
    ]);
    const to = `transform:translateX(${round(x2 - x1)}px)`;
    const moves = t.keyframes([
      [0, 'transform:translateX(0)'],
      [start, 'transform:translateX(0)', EASE],
      [start + travel, to],
      [story.total, to],
    ]);
    pulses.push(
      `<g class="${shown}" opacity="0"><g class="${moves}"><circle cx="${x1}" cy="${y}" r="8" fill="${color}" fill-opacity="0.2"/><circle cx="${x1}" cy="${y}" r="4.5" fill="${color}" stroke="${c.frame}" stroke-width="1.5"/></g></g>`,
    );
  };

  // Event source. It lights up as it sends the event.
  node(source);
  layer(t.shown([[story.fire, story.lit + 0.3]]), 0, lit(source, c.glow, c.accentBorder));
  p.add(icon(icons.source, source.x + source.w / 2, source.y + 18, 26, c.icon));
  p.line([{ text: 'Event source', face: 'name' }], 15.5, source.x + source.w / 2, source.y + 68, c.name, { center: true });
  detail(source, [[{ text: 'Your API, a webhook' }], [{ text: 'or a schedule' }]], source.y + 88);

  // Inngest, spanning both ways to your code. It lights up when the event starts a run and stays lit until the loop
  // starts over.
  node(inngest);
  const end = story.reset;
  const done = arrive(story.answer4);
  layer(t.shown([[story.lit, end]]), 1, lit(inngest, c.glow, c.accentBorder));
  layer(t.flash(story.lit), 0, flare(inngest, c.glow));
  layer(t.flash(done), 0, flare(inngest, c.ok));
  const mid = inngest.x + inngest.w / 2;
  p.add(icon(icons.inngest, mid, inngest.y + 27, 36, c.icon));
  p.line([{ text: 'Inngest', face: 'name' }], 21, mid, inngest.y + 90, c.name, { center: true });
  detail(inngest, [[{ text: 'Inngest Cloud, self-hosted' }], [{ text: 'or the dev server' }]], inngest.y + 112);

  // The run's state, as a chip like the hero's.
  const chipTop = inngest.y + 146;
  layer(t.shown([[story.lit, done]]), 0, chip('Running', mid, chipTop, c.coral, true));
  layer(t.shown([[done, end]]), 1, chip('Completed', mid, chipTop, c.ok, false));

  // The step state Inngest keeps, filled in as the answers arrive.
  const store: Box = { x: inngest.x + 18, y: inngest.y + 184, w: inngest.w - 36, h: 108 };
  p.add(
    `<rect x="${store.x + 0.5}" y="${store.y + 0.5}" width="${store.w - 1}" height="${store.h - 1}" rx="8" fill="none" stroke="${c.borderStrong}" stroke-dasharray="4 3"/>`,
  );
  p.add(icon(icons.store, store.x + 21, store.y + 12, 18, c.ok));
  p.line([{ text: 'Step state stored by Inngest' }], 12, store.x + 36, store.y + 26, c.text);
  const rowY = (i: number) => store.y + 51 + i * 21;
  const row = (i: number, kind: Mark, step: string, state: string, color: string, tickAt?: number) =>
    draw(() => {
      p.add(mark(kind, store.x + 18.5, rowY(i) - 4.5, color, tickAt === undefined ? '' : tick(tickAt)));
      p.line([{ text: step, face: 'mono', fill: c.code }, { text: ` ${state}`, fill: kind === 'check' ? c.text : color }], 12.5, store.x + 30, rowY(i), c.text);
    });
  const charged = arrive(story.answer1);
  const failed = arrive(story.answer2);
  const sent = arrive(story.answer3);
  layer(t.hidden([[charged - FADE, end + FADE]]), 0, draw(() => p.line([{ text: 'no steps yet' }], 12.5, store.x + 14, rowY(0), c.muted)));
  layer(t.shown([[charged, end]]), 1, row(0, 'check', 'charge', 'done', c.ok, charged));
  layer(t.shown([[failed, story.call3]]), 0, row(1, 'cross', 'send-receipt', 'failed', c.fail));
  layer(t.shown([[story.call3, sent]]), 0, row(1, 'dot', 'send-receipt', 'retrying', c.coral));
  layer(t.shown([[sent, end]]), 1, row(1, 'check', 'send-receipt', 'done', c.ok, sent));

  // The retry, after a backoff that a bar counts down.
  layer(
    t.shown([[failed, story.call3 + 0.1]]),
    0,
    draw(() => {
      const y = rowY(2);
      const width = p.line([{ text: 'retry 1 of 4 in 10s' }], 12, store.x + 30, y, c.muted);
      const x = round(store.x + 30 + width + 9);
      const w = round(store.x + store.w - 14 - x);
      const fills = t.keyframes([
        [0, 'transform:scaleX(0)'],
        [story.backoff, 'transform:scaleX(0)'],
        [story.call3, 'transform:scaleX(1)'],
        [story.total, 'transform:scaleX(1)'],
      ]);
      p.add(`<rect x="${x}" y="${y - 6}" width="${w}" height="4" rx="2" fill="${c.muted}" fill-opacity="0.3"/>`);
      p.add(`<g transform="translate(${x} ${y - 6})"><rect class="${fills}" width="${w}" height="4" rx="2" fill="${c.coral}"/></g>`);
    }),
  );

  // Event in.
  const eventY = source.y + source.h / 2;
  const eventFrom = source.x + source.w + gap;
  const eventTo = inngest.x - gap - 2;
  arrow(eventFrom, eventTo, eventY, 'event in', c.arrow, true, false);
  pulse(eventFrom + 4, eventTo - 8, eventY, c.glow, story.event, story.lit - story.event);

  const from = inngest.x + inngest.w + gap + 2;
  const to = app.x - gap;
  const laneTitle = (text: string, y: number) =>
    p.line([{ text: text.toUpperCase(), face: 'name' }], 11, from + 2, y, c.text, { tracking: 0.7 });

  // Over HTTP: one call per step, and the answers.
  laneTitle('Over HTTP', app.y - 14);
  node(app);
  const running: [number, number][] = [
    [arrive(story.call1), story.answer1 + 0.25],
    [arrive(story.call2), story.fail],
    [arrive(story.call3), story.answer3 + 0.25],
    [arrive(story.call4), story.answer4 + 0.25],
  ];
  // The failure shows until its answer reaches Inngest.
  const throwing: [number, number] = [story.fail, arrive(story.answer2)];
  layer(t.shown(running), 0, lit(app, c.glow, c.accentBorder));
  layer(t.shown([throwing]), 0, lit(app, c.fail));
  const appMid = app.x + app.w / 2;
  p.add(icon(icons.app, appMid, app.y + 14, 26, c.icon));
  p.line([{ text: 'Your ASP.NET Core app', face: 'name' }], 15.5, appMid, app.y + 60, c.name, { center: true });
  detail(app, [[{ text: 'MapInngest', face: 'mono', fill: c.code }, { text: ' runs one step' }], [{ text: 'per call' }]], app.y + 80, 12.5, 16);

  // What the app is doing during each call.
  const statusY = app.y + 117;
  const status = (runs: Run[], color: string, kind: Mark) => pill(runs, appMid, statusY, color, kind, app.w - 16);
  layer(t.shown([running[0]]), 0, status([{ text: 'running ' }, { text: 'charge', face: 'mono', fill: c.code }], c.coral, 'dot'));
  layer(t.shown([running[1], running[2]]), 0, status([{ text: 'running ' }, { text: 'send-receipt', face: 'mono', fill: c.code }], c.coral, 'dot'));
  layer(t.shown([throwing]), 0, status([{ text: 'send-receipt', face: 'mono', fill: c.code }, { text: ' failed' }], c.fail, 'cross'));
  layer(t.shown([running[3]]), 1, status([{ text: 'function returns' }], c.ok, 'check'));

  const http = app.y + app.h / 2 - 24;
  arrow(from, to, http, 'POST /api/inngest', c.coral, true, false);
  // The step error comes back as a 206 too, so the answer's arrow turns red and says so while it travels.
  const erring: [number, number] = [story.fail, failed + 1.2];
  layer(t.hidden([erring]), 1, arrowLabel(from, to, http + 30, '206 · step result', c.code));
  p.add(arrowLine(from, to, http + 30, c.plan, false, true));
  layer(t.shown([erring]), 0, arrowLine(from, to, http + 30, c.failLine, false, true));
  layer(t.shown([[erring[0] + FADE, erring[1] - FADE]]), 0, arrowLabel(from, to, http + 30, '206 · step error', c.fail));
  arrow(from, to, http + 60, '200 · done', c.ok, false, true);

  for (const call of [story.call1, story.call2, story.call3, story.call4]) pulse(from + 4, to - 8, http, c.glow, call);
  pulse(to - 4, from + 8, http + 30, c.plan, story.answer1);
  pulse(to - 4, from + 8, http + 30, c.failLine, story.answer2);
  pulse(to - 4, from + 8, http + 30, c.plan, story.answer3);
  pulse(to - 4, from + 8, http + 60, c.ok, story.answer4);

  // Or over Connect, which stays still.
  laneTitle('Or over Connect, with no public endpoint', worker.y - 14);
  node(worker, true);
  const workerMid = worker.x + worker.w / 2;
  p.add(icon(icons.worker, workerMid, worker.y + 14, 26, c.icon));
  p.line([{ text: 'Your Connect worker', face: 'name' }], 15.5, workerMid, worker.y + 60, c.name, { center: true });
  detail(worker, [[{ text: 'No public endpoint, so it can' }], [{ text: 'run behind a firewall' }]], worker.y + 80, 12.5, 16);
  const socket = worker.y + worker.h / 2 - 24;
  arrow(from, to, socket, 'WebSocket from your worker', c.run, false, true);
  arrow(from, to, socket + 30, 'executions over the socket', c.coral, true, true);
  arrow(from, to, socket + 60, 'results over the socket', c.ok, false, true);

  // Messages go over everything else.
  p.add(pulses.join(''));

  const style =
    `@media (prefers-reduced-motion:no-preference){${t.rules.join('')}` +
    '.dot{animation:pulse 1.2s ease-in-out infinite}@keyframes pulse{50%{opacity:.35}}}';
  return p.svg(W, H, title, style);
}

mkdirSync(out, { recursive: true });
for (const [name, palette] of [
  ['dark', dark],
  ['light', light],
] as const) {
  const svg = architecture(palette);
  writeFileSync(join(out, `architecture-${name}.svg`), svg);
  console.log(`architecture-${name}.svg: ${(svg.length / 1024).toFixed(1)} KB, a ${story.total}s loop`);
}
