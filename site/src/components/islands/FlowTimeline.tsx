import { useId, useState } from 'react';
import { DEFAULT_PARAMS, format, type Mode, type Outcome, type Params, type Simulation, simulate, summarize } from '../../lib/flow/simulate';

const DOCS = 'https://www.inngest.com/docs/durable-execution/flow-control';

const MODES: { id: Mode; label: string; description: string; docs: string }[] = [
  {
    id: 'concurrency',
    label: 'Concurrency',
    description: 'At most a number of runs execute at the same time. Others wait for a free slot and start in order.',
    docs: `${DOCS}/concurrency`,
  },
  {
    id: 'throttle',
    label: 'Throttle',
    description: 'At most a number of runs start per period. Others are queued and start later, spread out evenly.',
    docs: `${DOCS}/throttling`,
  },
  {
    id: 'rateLimit',
    label: 'Rate limit',
    description: 'At most a number of runs start per period. Events beyond that are skipped and never run.',
    docs: `${DOCS}/rate-limiting`,
  },
  {
    id: 'debounce',
    label: 'Debounce',
    description: 'One run starts once events stop arriving for a period, with the last event. A timeout caps the wait.',
    docs: `${DOCS}/debounce`,
  },
];

const FIELDS: Record<Mode, { key: keyof Params; label: string; min: number; max: number; unit?: string }[]> = {
  concurrency: [
    { key: 'limit', label: 'Limit', min: 1, max: 5 },
    { key: 'duration', label: 'Run duration', min: 1, max: 20, unit: 's' },
  ],
  throttle: [
    { key: 'limit', label: 'Limit', min: 1, max: 10 },
    { key: 'period', label: 'Period', min: 1, max: 30, unit: 's' },
    { key: 'burst', label: 'Burst', min: 0, max: 5 },
  ],
  rateLimit: [
    { key: 'limit', label: 'Limit', min: 1, max: 10 },
    { key: 'period', label: 'Period', min: 1, max: 30, unit: 's' },
  ],
  debounce: [
    { key: 'period', label: 'Period', min: 1, max: 15, unit: 's' },
    { key: 'timeout', label: 'Timeout (0 for none)', min: 0, max: 60, unit: 's' },
  ],
};

const OUTCOME_COLORS: Record<Outcome, string> = {
  started: 'var(--idn-ok)',
  delayed: 'var(--idn-plan)',
  dropped: 'var(--idn-fail)',
  merged: 'var(--sl-color-gray-3)',
};

const OUTCOME_LABELS: Record<Outcome, string> = {
  started: 'started at once',
  delayed: 'delayed',
  dropped: 'skipped',
  merged: 'merged into a later run',
};

export default function FlowTimeline() {
  const [mode, setMode] = useState<Mode>('concurrency');
  const [params, setParams] = useState(DEFAULT_PARAMS);
  const id = useId();

  // All four timelines are rendered, on the server too. Without JavaScript they all show with their defaults; with
  // it, the controls appear and the site CSS shows only the selected one, from the first paint.
  return (
    <div className="not-content flex flex-col gap-4">
      <fieldset className="idn-js-only m-0 border-0 p-0">
        <legend className="eyebrow mb-2">Flow control</legend>
        <div className="flex flex-wrap gap-x-5 gap-y-2">
          {MODES.map((m) => (
            <label key={m.id} className="idn-check text-sm">
              <input type="radio" name={`${id}-mode`} value={m.id} checked={mode === m.id} onChange={() => setMode(m.id)} />
              {m.label}
            </label>
          ))}
        </div>
      </fieldset>
      {MODES.map((m) => (
        <section
          key={m.id}
          aria-labelledby={`${id}-${m.id}`}
          data-idn-inactive={m.id === mode ? undefined : ''}
          className="flex flex-col gap-3"
        >
          <h3 id={`${id}-${m.id}`} className="m-0 text-base font-semibold text-strong">
            {m.label}
          </h3>
          <p className="m-0 text-sm text-body">
            {m.description} <a href={m.docs}>{m.label} in the Inngest docs</a>.
          </p>
          <fieldset className="idn-js-only m-0 flex flex-wrap gap-x-6 gap-y-3 rounded-lg border border-line p-3">
            <legend className="px-1 text-xs font-semibold text-body">Settings</legend>
            {FIELDS[m.id].map((field) => (
              <label key={field.key} className="idn-field min-w-[10rem] flex-1">
                <span>
                  {field.label}: {params[m.id][field.key]}
                  {field.unit ?? ''}
                </span>
                <input
                  type="range"
                  min={field.min}
                  max={field.max}
                  step={1}
                  value={params[m.id][field.key]}
                  onChange={(event) =>
                    setParams({ ...params, [m.id]: { ...params[m.id], [field.key]: Number(event.target.value) } })
                  }
                  className="accent-[var(--sl-color-accent)]"
                />
              </label>
            ))}
          </fieldset>
          <Timeline mode={m.id} simulation={simulate(m.id, params[m.id])} params={params[m.id]} />
        </section>
      ))}
    </div>
  );
}

function Timeline({ mode, simulation, params }: { mode: Mode; simulation: Simulation; params: Params }) {
  const id = useId();
  const lanes = mode === 'concurrency' ? Math.max(1, params.limit) : 1;
  const horizon = Math.max(60, Math.ceil(Math.max(...simulation.runs.map((r) => r.end), 0) / 10) * 10);
  const left = 64;
  const width = 720;
  const x = (t: number) => left + (t / horizon) * (width - left - 16);
  const eventsY = 30;
  const laneY = (lane: number) => 84 + lane * 26;
  const axisY = laneY(lanes - 1) + 30;
  const height = axisY + 26;
  const summary = summarize(simulation);
  const ticks = Array.from({ length: horizon / 10 + 1 }, (_, i) => i * 10);

  return (
    <figure className="m-0 flex flex-col gap-2">
      <div className="overflow-x-auto rounded-lg border border-line bg-surface p-2">
        <svg viewBox={`0 0 ${width} ${height}`} role="img" aria-labelledby={`${id}-title ${id}-desc`} className="block w-full min-w-[34rem]">
          <title id={`${id}-title`}>Timeline of events and runs</title>
          <desc id={`${id}-desc`}>{summary}</desc>
          <text x={8} y={eventsY + 4} style={{ fill: 'var(--sl-color-gray-3)', fontSize: 11, fontFamily: 'var(--__sl-font-mono)' }}>
            events
          </text>
          <text x={8} y={laneY(0) + 4} style={{ fill: 'var(--sl-color-gray-3)', fontSize: 11, fontFamily: 'var(--__sl-font-mono)' }}>
            runs
          </text>
          <line x1={left} x2={width - 16} y1={eventsY} y2={eventsY} style={{ stroke: 'var(--idn-border)' }} />
          {Array.from({ length: lanes }, (_, lane) => (
            <line key={lane} x1={left} x2={width - 16} y1={laneY(lane)} y2={laneY(lane)} style={{ stroke: 'var(--idn-border)' }} />
          ))}
          {simulation.runs.map((run, i) => {
            const y = laneY(run.lane);
            return (
              <g key={i}>
                {run.events.map((index) => (
                  <line
                    key={index}
                    x1={x(simulation.events[index].arrival)}
                    y1={eventsY + 6}
                    x2={x(run.start)}
                    y2={y - 7}
                    style={{ stroke: OUTCOME_COLORS[simulation.events[index].outcome], strokeOpacity: 0.55 }}
                  />
                ))}
                <rect
                  x={x(run.start)}
                  y={y - 7}
                  width={Math.max(3, x(run.end) - x(run.start))}
                  height={14}
                  rx={3}
                  style={{ fill: 'var(--idn-run-bg)', stroke: 'var(--idn-run)' }}
                />
              </g>
            );
          })}
          {simulation.events.map((event, i) =>
            event.outcome === 'dropped' ? (
              <g key={i} style={{ stroke: OUTCOME_COLORS.dropped, strokeWidth: 2 }}>
                <line x1={x(event.arrival) - 4} y1={eventsY - 4} x2={x(event.arrival) + 4} y2={eventsY + 4} />
                <line x1={x(event.arrival) - 4} y1={eventsY + 4} x2={x(event.arrival) + 4} y2={eventsY - 4} />
              </g>
            ) : (
              <circle key={i} cx={x(event.arrival)} cy={eventsY} r={4.5} style={{ fill: OUTCOME_COLORS[event.outcome] }} />
            ),
          )}
          {ticks.map((t) => (
            <g key={t}>
              <line x1={x(t)} x2={x(t)} y1={axisY - 6} y2={axisY} style={{ stroke: 'var(--idn-border-strong)' }} />
              <text x={x(t)} y={axisY + 14} textAnchor="middle" style={{ fill: 'var(--sl-color-gray-3)', fontSize: 11, fontFamily: 'var(--__sl-font-mono)' }}>
                {t}s
              </text>
            </g>
          ))}
        </svg>
      </div>
      <figcaption className="flex flex-wrap items-center gap-x-4 gap-y-1 text-xs text-muted">
        <span aria-live="polite" className="text-body">
          {summary}
        </span>
        {(Object.keys(OUTCOME_LABELS) as Outcome[]).map((outcome) => (
          <span key={outcome} className="inline-flex items-center gap-1.5">
            <svg width="10" height="10" aria-hidden="true">
              <circle cx="5" cy="5" r="4.5" style={{ fill: OUTCOME_COLORS[outcome] }} />
            </svg>
            {OUTCOME_LABELS[outcome]}
          </span>
        ))}
      </figcaption>
      <details className="text-sm">
        <summary className="cursor-pointer font-semibold text-link">The numbers</summary>
        <table className="mt-2 w-full text-sm">
          <thead>
            <tr>
              <th scope="col" className="text-start">Event arrives</th>
              <th scope="col" className="text-start">Outcome</th>
              <th scope="col" className="text-start">Its run starts</th>
            </tr>
          </thead>
          <tbody>
            {simulation.events.map((event, i) => (
              <tr key={i}>
                <td>{format(event.arrival)}</td>
                <td>{OUTCOME_LABELS[event.outcome]}</td>
                <td>{event.start === undefined ? 'never' : format(event.start)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </details>
    </figure>
  );
}
