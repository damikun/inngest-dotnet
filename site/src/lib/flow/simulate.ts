// A small model of how Inngest schedules runs under concurrency, throttling, rate limiting and debouncing, for the
// flow control timeline. It is deliberately simple: one key, every run is a single step, and time is in seconds.
// Inngest's scheduler differs in details, so this explains the ideas rather than predicting exact timings.

export type Mode = 'concurrency' | 'throttle' | 'rateLimit' | 'debounce';

export interface Params {
  limit: number;
  period: number;
  burst: number;
  // How long a run takes, for concurrency.
  duration: number;
  // The debounce timeout; 0 for none.
  timeout: number;
}

export type Outcome = 'started' | 'delayed' | 'dropped' | 'merged';

export interface EventResult {
  arrival: number;
  outcome: Outcome;
  // When the run this event belongs to starts. Undefined for dropped events.
  start?: number;
}

export interface Run {
  start: number;
  end: number;
  // The lane a concurrent run occupies, from 0.
  lane: number;
  // Indexes of the events the run handles.
  events: number[];
}

export interface Simulation {
  events: EventResult[];
  runs: Run[];
}

export const DEFAULT_ARRIVALS = [0, 1, 1.5, 2, 3, 4, 10, 11, 12, 13, 25, 26, 40, 41, 42];

export const DEFAULT_PARAMS: Record<Mode, Params> = {
  concurrency: { limit: 2, period: 10, burst: 0, duration: 8, timeout: 0 },
  throttle: { limit: 3, period: 10, burst: 0, duration: 2, timeout: 0 },
  rateLimit: { limit: 3, period: 10, burst: 0, duration: 2, timeout: 0 },
  debounce: { limit: 1, period: 5, burst: 0, duration: 2, timeout: 15 },
};

const EPSILON = 1e-9;

export function simulate(mode: Mode, params: Params, arrivals: number[] = DEFAULT_ARRIVALS): Simulation {
  const sorted = [...arrivals].sort((a, b) => a - b);
  switch (mode) {
    case 'concurrency':
      return concurrency(sorted, params);
    case 'throttle':
      return throttle(sorted, params);
    case 'rateLimit':
      return rateLimit(sorted, params);
    case 'debounce':
      return debounce(sorted, params);
  }
}

// Runs start in arrival order as soon as fewer than limit runs are active.
function concurrency(arrivals: number[], { limit, duration }: Params): Simulation {
  const freeAt = Array.from({ length: Math.max(1, limit) }, () => 0);
  const events: EventResult[] = [];
  const runs: Run[] = [];
  arrivals.forEach((arrival, i) => {
    const lane = freeAt.indexOf(Math.min(...freeAt));
    const start = Math.max(arrival, freeAt[lane]);
    freeAt[lane] = start + duration;
    events.push({ arrival, start, outcome: start > arrival + EPSILON ? 'delayed' : 'started' });
    runs.push({ start, end: start + duration, lane, events: [i] });
  });
  return { events, runs };
}

// Generic cell rate algorithm: one start every period / limit, with room for burst extra starts at once. Runs
// that do not fit wait in a queue, in order.
function throttle(arrivals: number[], { limit, period, burst, duration }: Params): Simulation {
  const interval = period / Math.max(1, limit);
  const tolerance = Math.max(0, burst) * interval;
  let theoretical = -Infinity;
  let previous = -Infinity;
  const events: EventResult[] = [];
  const runs: Run[] = [];
  arrivals.forEach((arrival, i) => {
    const start = Math.max(arrival, previous, theoretical - tolerance);
    theoretical = Math.max(theoretical, start) + interval;
    previous = start;
    events.push({ arrival, start, outcome: start > arrival + EPSILON ? 'delayed' : 'started' });
    runs.push({ start, end: start + duration, lane: 0, events: [i] });
  });
  return { events, runs };
}

// The same algorithm with room for limit starts at once, but a run that does not fit is skipped, not queued.
function rateLimit(arrivals: number[], { limit, period, duration }: Params): Simulation {
  const interval = period / Math.max(1, limit);
  const tolerance = (Math.max(1, limit) - 1) * interval;
  let theoretical = -Infinity;
  const events: EventResult[] = [];
  const runs: Run[] = [];
  arrivals.forEach((arrival, i) => {
    if (arrival + EPSILON < theoretical - tolerance) {
      events.push({ arrival, outcome: 'dropped' });
      return;
    }
    theoretical = Math.max(theoretical, arrival) + interval;
    events.push({ arrival, start: arrival, outcome: 'started' });
    runs.push({ start: arrival, end: arrival + duration, lane: 0, events: [i] });
  });
  return { events, runs };
}

// Each event pushes the run back to period after it, but never past timeout after the first event of the
// window. The run gets the last event; the ones before it are merged into it.
function debounce(arrivals: number[], { period, timeout, duration }: Params): Simulation {
  const events: EventResult[] = arrivals.map((arrival) => ({ arrival, outcome: 'merged' as Outcome }));
  const runs: Run[] = [];
  let window: number[] = [];
  const close = () => {
    if (window.length === 0) return;
    const first = arrivals[window[0]];
    const last = arrivals[window[window.length - 1]];
    const start = timeout > 0 ? Math.min(last + period, first + timeout) : last + period;
    for (const index of window) events[index].start = start;
    events[window[window.length - 1]].outcome = 'started';
    runs.push({ start, end: start + duration, lane: 0, events: [...window] });
    window = [];
  };
  arrivals.forEach((arrival, i) => {
    if (window.length > 0) {
      const first = arrivals[window[0]];
      const last = arrivals[window[window.length - 1]];
      const due = timeout > 0 ? Math.min(last + period, first + timeout) : last + period;
      if (arrival > due - EPSILON) close();
    }
    window.push(i);
  });
  close();
  return { events, runs };
}

export function summarize(simulation: Simulation): string {
  const count = (outcome: Outcome) => simulation.events.filter((e) => e.outcome === outcome).length;
  const delays = simulation.events.filter((e) => e.outcome === 'delayed').map((e) => (e.start ?? e.arrival) - e.arrival);
  const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? '' : 's'}`;
  const parts = [`${plural(simulation.events.length, 'event')}, ${plural(simulation.runs.length, 'run')}`];
  if (count('delayed') > 0) parts.push(`${count('delayed')} delayed by up to ${format(Math.max(...delays))}`);
  if (count('dropped') > 0) parts.push(`${count('dropped')} skipped`);
  if (count('merged') > 0) parts.push(`${count('merged')} merged into a later event's run`);
  return `${parts.join(', ')}.`;
}

export function format(seconds: number): string {
  return `${Number.isInteger(seconds) ? seconds : seconds.toFixed(1)}s`;
}
