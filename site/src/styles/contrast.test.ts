import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';

// WCAG 2.2 contrast: 4.5:1 for body text, 3:1 for large text and for the edges of controls.
const TEXT = 4.5;
const UI = 3;

const css = readFileSync(new URL('./theme.css', import.meta.url), 'utf8');

function block(selector: string): Map<string, string> {
  const start = css.indexOf(selector);
  if (start < 0) throw new Error(`No block for ${selector}`);
  const open = css.indexOf('{', start);
  const close = css.indexOf('}', open);
  const values = new Map<string, string>();
  for (const match of css.slice(open + 1, close).matchAll(/(--[\w-]+):\s*([^;]+);/g)) {
    values.set(match[1], match[2].trim());
  }
  return values;
}

function resolve(values: Map<string, string>, name: string): string {
  let value = values.get(name);
  for (let depth = 0; value?.startsWith('var('); depth++) {
    if (depth > 5) throw new Error(`${name} does not resolve`);
    value = values.get(value.slice(4, -1).trim());
  }
  if (!value || !/^#[0-9a-f]{6}$/i.test(value)) throw new Error(`${name} is not a hex color: ${value}`);
  return value;
}

function luminance(hex: string): number {
  const channels = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16) / 255);
  const [r, g, b] = channels.map((c) => (c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4));
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

function contrast(a: string, b: string): number {
  const [light, dark] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (light + 0.05) / (dark + 0.05);
}

const backgrounds = ['--sl-color-black', '--sl-color-bg-nav', '--sl-color-bg-sidebar', '--idn-surface'];
const textPairs: [string, string][] = [
  ...['--sl-color-white', '--sl-color-gray-1', '--sl-color-text', '--sl-color-gray-3', '--sl-color-text-accent'].flatMap(
    (text) => backgrounds.map((bg): [string, string] => [text, bg]),
  ),
  ['--sl-color-text', '--sl-color-bg-inline-code'],
  ['--sl-color-text-invert', '--sl-color-bg-accent'],
  ['--sl-color-text-invert', '--sl-color-text-accent'],
  ['--sl-color-accent-high', '--sl-color-accent-low'],
  ...['blue', 'green', 'orange', 'red', 'purple'].flatMap((hue): [string, string][] => [
    ['--sl-color-white', `--sl-color-${hue}-low`],
    [`--sl-color-${hue}-high`, `--sl-color-${hue}-low`],
  ]),
  ...['ok', 'run', 'plan', 'wait', 'fail'].flatMap((state): [string, string][] => [
    [`--idn-${state}`, `--idn-${state}-bg`],
    [`--idn-${state}`, '--sl-color-black'],
    [`--idn-${state}`, '--idn-surface'],
  ]),
];
textPairs.push(
  ['--idn-coral-text', '--sl-color-black'],
  ['--idn-coral-text', '--idn-surface'],
  ['--idn-on-coral', '--idn-coral'],
  ['--idn-on-coral', '--idn-coral-hover'],
);

const codeTokens = ['text', 'comment', 'keyword', 'string', 'number', 'type', 'property', 'punctuation'];
textPairs.push(...codeTokens.map((token): [string, string] => [`--idn-code-${token}`, '--idn-code-bg']));

const uiPairs: [string, string][] = [
  ['--idn-border-strong', '--sl-color-black'],
  ['--idn-border-strong', '--idn-surface'],
  ['--idn-focus', '--sl-color-black'],
  ['--idn-focus', '--idn-surface'],
  ['--sl-color-accent', '--sl-color-black'],
  ['--idn-coral', '--sl-color-black'],
];

describe.each([
  ['dark', ':root,\n::backdrop {'],
  ['light', ":root[data-theme='light'],"],
])('%s theme', (_, selector) => {
  const values = block(selector);

  it.each(textPairs)('%s on %s meets 4.5:1', (fg, bg) => {
    expect(contrast(resolve(values, fg), resolve(values, bg))).toBeGreaterThanOrEqual(TEXT);
  });

  it.each(uiPairs)('%s against %s meets 3:1', (fg, bg) => {
    expect(contrast(resolve(values, fg), resolve(values, bg))).toBeGreaterThanOrEqual(UI);
  });
});
