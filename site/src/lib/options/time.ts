// Durations as the SDK sends them to Inngest, matching TimeString.Format in src/Inngest/Internal/TimeString.cs:
// whole seconds in w, d, h, m and s units, "0s" for zero, and positive values under a second rounded up to "1s".

export type Unit = 's' | 'm' | 'h' | 'd';

export interface Duration {
  value: number;
  unit: Unit;
}

const SECONDS: Record<Unit, number> = { s: 1, m: 60, h: 3600, d: 86400 };

const UNITS: [string, number][] = [
  ['w', 7 * 86400],
  ['d', 86400],
  ['h', 3600],
  ['m', 60],
  ['s', 1],
];

export function toSeconds(duration: Duration): number {
  return duration.value * SECONDS[duration.unit];
}

export function formatSeconds(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds < 0) throw new RangeError(`A duration cannot be negative: ${seconds}`);
  if (seconds === 0) return '0s';
  let remaining = seconds < 1 ? 1 : Math.floor(seconds);
  let text = '';
  for (const [suffix, size] of UNITS) {
    const count = Math.floor(remaining / size);
    if (count > 0) {
      text += `${count}${suffix}`;
      remaining -= count * size;
    }
  }
  return text;
}

export function formatDuration(duration: Duration): string {
  return formatSeconds(toSeconds(duration));
}

// How the SDK's validator describes a duration in an error message.
export function describeSeconds(seconds: number): string {
  return seconds >= 1 ? formatSeconds(seconds) : `${seconds}s`;
}
