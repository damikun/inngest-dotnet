// Reads the replay scenario in src/data/replay.json: the calls Inngest made to FulfillOrder and the SDK's real
// answers, as scripts/check-snippets.ts captured them.

export interface ReplayCall {
  request: { method: string; url: string; body: { steps: Record<string, unknown>; ctx: Record<string, unknown> } & Record<string, unknown> };
  response: { status: number; headers: Record<string, string>; body: unknown };
}

export interface ReplayData {
  source: string;
  stepNames: Record<string, string>;
  calls: ReplayCall[];
}

export interface Op {
  id: string;
  op: string;
  displayName?: string;
  data?: unknown;
  opts?: Record<string, unknown>;
}

// What happened to a step in one call. "reported" steps are carried out by Inngest, such as a sleep.
export type StepState = 'memoized' | 'ran' | 'planned' | 'reported' | 'untouched';

export interface CodeLine {
  number: number;
  text: string;
  step?: string;
  isReturn: boolean;
}

export function stepIdOf(call: ReplayCall): string {
  return new URL(call.request.url, 'http://localhost').searchParams.get('stepId') ?? 'step';
}

export function opsOf(call: ReplayCall): Op[] {
  return call.response.status === 206 && Array.isArray(call.response.body) ? (call.response.body as Op[]) : [];
}

export function stepStates(data: ReplayData, index: number): Map<string, StepState> {
  const call = data.calls[index];
  const states = new Map<string, StepState>(Object.values(data.stepNames).map((name) => [name, 'untouched']));
  for (const hash of Object.keys(call.request.body.steps)) {
    states.set(data.stepNames[hash] ?? hash, 'memoized');
  }
  for (const op of opsOf(call)) {
    const name = data.stepNames[op.id] ?? op.displayName ?? op.id;
    states.set(name, op.op === 'StepRun' || op.op === 'StepError' || op.op === 'StepFailed' ? 'ran' : op.op === 'StepPlanned' ? 'planned' : 'reported');
  }
  return states;
}

// Splits the source into lines and marks which step each line belongs to. A step's call starts on the line with
// its id and continues on more deeply indented lines while a line ends with a comma.
export function codeLines(data: ReplayData): CodeLine[] {
  const texts = data.source.split('\n');
  const names = Object.values(data.stepNames);
  const lines: CodeLine[] = texts.map((text, i) => ({ number: i + 1, text, isReturn: /^\s*return\b/.test(text) }));
  const indent = (text: string) => text.length - text.trimStart().length;
  lines.forEach((line, i) => {
    const step = names.find((name) => line.text.includes(`"${name}"`));
    if (!step) return;
    line.step = step;
    for (let j = i; lines[j].text.trimEnd().endsWith(',') && lines[j + 1] && indent(lines[j + 1].text) > indent(line.text); j++) {
      lines[j + 1].step = step;
    }
  });
  return lines;
}

export function shortHash(hash: string): string {
  return hash.length > 12 ? `${hash.slice(0, 7)}…` : hash;
}
