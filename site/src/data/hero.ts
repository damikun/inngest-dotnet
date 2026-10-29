// The checkout function the site uses to show a durable run, shared by the landing page's hero, the step-through on
// How it works and the landing page's social card. The lines are an excerpt of CheckoutFunction in the landing
// page's code section, which the snippet check compiles.

export type StepState = 'idle' | 'running' | 'completed' | 'failed' | 'retrying' | 'sleeping';

export const steps: { id: string; lines: string[] }[] = [
  { id: 'charge', lines: ['var charge = await context.Step.RunAsync("charge",', '    ct => pay.ChargeAsync(order, ct));'] },
  {
    id: 'send-receipt',
    lines: ['await context.Step.RunAsync("send-receipt",', '    ct => mail.SendReceiptAsync(order, ct));'],
  },
  { id: 'cooling-off', lines: ['await context.Step.SleepAsync("cooling-off",', '    TimeSpan.FromMinutes(30));'] },
  { id: 'ship', lines: ['await context.Step.RunAsync("ship",', '    ct => warehouse.ShipAsync(order, ct));'] },
];

export const returnLine = 'return new { charge.Id };';

// The hero: one timeline of frames, each holding the state and the chip of every step. A script shows the frames in
// turn, so the steps cannot drift apart. The charge runs, send-receipt fails once and its retry succeeds, the run
// sleeps, then ship runs; the last frame stays up a little longer.
type Chip = readonly [StepState, string?];
export interface Frame {
  ms: number;
  steps: readonly [Chip, Chip, Chip, Chip];
}

const idle: Chip = ['idle'];
const charged: Chip = ['completed', 'Completed · 1.2s'];
const receipt: Chip = ['completed', 'Completed · 1 retry'];
const cooled: Chip = ['completed', 'Completed · 30m'];

export const frames: Frame[] = [
  { ms: 1300, steps: [['running', 'Running'], idle, idle, idle] },
  { ms: 1300, steps: [charged, ['running', 'Running'], idle, idle] },
  { ms: 1300, steps: [charged, ['failed', 'Failed · retry 1/4'], idle, idle] },
  { ms: 1300, steps: [charged, ['retrying', 'Running · retry 1/4'], idle, idle] },
  { ms: 1300, steps: [charged, receipt, ['sleeping', 'Sleeping · 30m'], idle] },
  { ms: 1300, steps: [charged, receipt, cooled, ['running', 'Running']] },
  { ms: 2600, steps: [charged, receipt, cooled, ['completed', 'Completed · 0.4s']] },
];

// Shown without motion, and drawn on the social card: the retry of send-receipt, with the failure before it, after
// the charge completed.
export const stillFrame = frames[3];

// How it works: the same function on its happy path, one call at a time, in the reader's own pace.
export type LoopState = 'idle' | 'running' | 'saved' | 'sleeping' | 'returns';
export interface Call {
  // The state of each step and of the return statement.
  states: readonly [LoopState, LoopState, LoopState, LoopState, LoopState];
  // The steps whose results Inngest sends with the call.
  saved: number;
  answer: { code: 206 | 200; label: string };
  story: string;
}

export const calls: Call[] = [
  {
    states: ['running', 'idle', 'idle', 'idle', 'idle'],
    saved: 0,
    answer: { code: 206, label: 'charge result' },
    story: 'Nothing is saved yet, so <code>charge</code> runs. The app answers 206 with its result, and Inngest saves it.',
  },
  {
    states: ['saved', 'running', 'idle', 'idle', 'idle'],
    saved: 1,
    answer: { code: 206, label: 'send-receipt result' },
    story: '<code>charge</code> returns its saved result at once, without charging again, and <code>send-receipt</code> runs.',
  },
  {
    states: ['saved', 'saved', 'sleeping', 'idle', 'idle'],
    saved: 2,
    answer: { code: 206, label: 'sleep 30 minutes' },
    story: 'Both finished steps return their saved results. The sleep is new: the app reports it, and Inngest calls again in 30 minutes.',
  },
  {
    states: ['saved', 'saved', 'saved', 'running', 'idle'],
    saved: 3,
    answer: { code: 206, label: 'ship result' },
    story: 'Thirty minutes later, every finished step returns its saved result, and <code>ship</code> runs.',
  },
  {
    states: ['saved', 'saved', 'saved', 'saved', 'returns'],
    saved: 4,
    answer: { code: 200, label: 'run complete' },
    story: 'Every step returns its saved result, the function returns, and the app answers 200. The run is complete.',
  },
];

// Shown without JavaScript.
export const stillCall = 3;

export const loopLabels: Record<LoopState, string> = {
  idle: 'not reached',
  running: 'running',
  saved: 'saved result',
  sleeping: 'sleeping · 30 minutes',
  returns: 'returns · 200',
};

// Icons for the How it works states, as SVG path data on a 16 by 16 grid. The running dot is filled.
export const loopIcons: Record<LoopState, string> = {
  idle: 'M8 3.5a4.5 4.5 0 1 1 0 9 4.5 4.5 0 0 1 0-9z',
  running: 'M8 4.5a3.5 3.5 0 1 1 0 7 3.5 3.5 0 0 1 0-7z',
  saved: 'M13 8a5 5 0 1 1-1.6-3.7M13 3v2.6h-2.6',
  sleeping: 'M12.5 10A5 5 0 0 1 6 3.5a5 5 0 1 0 6.5 6.5z',
  returns: 'M3.5 8.5 6.5 11.5 12.5 4.5',
};

// The C# tokens the pictures colour, in the order they are applied.
export const heroTokens: { pattern: RegExp; kind: 'string' | 'keyword' | 'type' | 'method' }[] = [
  { pattern: /"[^"]*"/g, kind: 'string' },
  { pattern: /\b(?:var|await|return|new)\b/g, kind: 'keyword' },
  { pattern: /\bTimeSpan\b/g, kind: 'type' },
  { pattern: /(?<=\.)(?:RunAsync|SleepAsync|ChargeAsync|SendReceiptAsync|ShipAsync|FromMinutes)\b/g, kind: 'method' },
];

// Splits a line into runs of plain text and coloured tokens. Earlier patterns win where they overlap.
export function tokenize(line: string): { text: string; kind?: (typeof heroTokens)[number]['kind'] }[] {
  const owner: ((typeof heroTokens)[number]['kind'] | undefined)[] = Array.from({ length: line.length });
  for (const { pattern, kind } of heroTokens) {
    for (const match of line.matchAll(pattern)) {
      const start = match.index ?? 0;
      const span = owner.slice(start, start + match[0].length);
      if (span.every((k) => k === undefined)) span.forEach((_, i) => (owner[start + i] = kind));
    }
  }
  const runs: { text: string; kind?: (typeof heroTokens)[number]['kind'] }[] = [];
  for (let i = 0; i < line.length; i++) {
    const last = runs.at(-1);
    if (last && last.kind === owner[i]) last.text += line[i];
    else runs.push({ text: line[i], kind: owner[i] });
  }
  return runs;
}
