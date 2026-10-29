import { describe, expect, it } from 'vitest';
import { DEFAULT_PARAMS, simulate, summarize } from './simulate.ts';

const params = (mode: keyof typeof DEFAULT_PARAMS, overrides: Partial<(typeof DEFAULT_PARAMS)['throttle']> = {}) => ({
  ...DEFAULT_PARAMS[mode],
  ...overrides,
});

describe('concurrency', () => {
  it('never runs more than the limit at once and starts runs in arrival order', () => {
    const { events, runs } = simulate('concurrency', params('concurrency', { limit: 2, duration: 5 }), [0, 0, 0, 1]);
    expect(events.map((e) => e.start)).toEqual([0, 0, 5, 5]);
    for (let t = 0; t < 12; t += 0.5) {
      expect(runs.filter((r) => r.start <= t && t < r.end).length).toBeLessThanOrEqual(2);
    }
  });
});

describe('throttle', () => {
  it('spreads starts out at one per period / limit and never drops a run', () => {
    const { events } = simulate('throttle', params('throttle', { limit: 2, period: 10, burst: 0 }), [0, 0, 0, 0]);
    expect(events.map((e) => e.start)).toEqual([0, 5, 10, 15]);
    expect(events.every((e) => e.outcome !== 'dropped')).toBe(true);
  });

  it('lets burst extra runs start at once', () => {
    const { events } = simulate('throttle', params('throttle', { limit: 2, period: 10, burst: 1 }), [0, 0, 0, 0]);
    expect(events.map((e) => e.start)).toEqual([0, 0, 5, 10]);
  });
});

describe('rate limit', () => {
  it('skips runs over the limit instead of queueing them', () => {
    const { events } = simulate('rateLimit', params('rateLimit', { limit: 2, period: 10 }), [0, 1, 2, 3, 11]);
    expect(events.map((e) => e.outcome)).toEqual(['started', 'started', 'dropped', 'dropped', 'started']);
  });
});

describe('debounce', () => {
  it('starts one run with the last event once events stop for the period', () => {
    const { events, runs } = simulate('debounce', params('debounce', { period: 5, timeout: 0 }), [0, 2, 4, 20]);
    expect(runs.map((r) => r.start)).toEqual([9, 25]);
    expect(events.map((e) => e.outcome)).toEqual(['merged', 'merged', 'started', 'started']);
  });

  it('starts the run at the timeout during a long stream of events', () => {
    const { runs } = simulate('debounce', params('debounce', { period: 5, timeout: 10 }), [0, 3, 6, 9, 12]);
    expect(runs[0].start).toBe(10);
    expect(runs[0].events).toEqual([0, 1, 2, 3]);
    expect(runs[1].start).toBe(17);
  });
});

it('summarizes a simulation in words', () => {
  expect(summarize(simulate('rateLimit', params('rateLimit', { limit: 1, period: 10 }), [0, 1]))).toBe('2 events, 1 run, 1 skipped.');
});
