import { type KeyboardEvent, useId, useRef, useState } from 'react';
import data from '../../data/replay.json';
import { tokenizeCSharp } from '../../lib/highlight/highlight';
import { codeLines, opsOf, type ReplayData, shortHash, type StepState, stepIdOf, stepStates } from '../../lib/replay/model';
import { narrative } from '../../lib/replay/narrative';
import CodeOutput from './CodeOutput';

const replay = data as unknown as ReplayData;
const lines = codeLines(replay);

const LABELS: Record<StepState | 'returned', string> = {
  memoized: 'memoized',
  ran: 'ran in this call',
  planned: 'planned',
  reported: 'sent to Inngest',
  untouched: '',
  returned: 'returned',
};

const LINE_CLASSES: Record<StepState | 'returned', string> = {
  memoized: 'bg-ok-bg shadow-[inset_3px_0_0_var(--idn-ok)]',
  ran: 'bg-run-bg shadow-[inset_3px_0_0_var(--idn-run)]',
  planned: 'bg-plan-bg shadow-[inset_3px_0_0_var(--idn-plan)]',
  reported: 'bg-wait-bg shadow-[inset_3px_0_0_var(--idn-wait)]',
  untouched: '',
  returned: 'bg-ok-bg shadow-[inset_3px_0_0_var(--idn-ok)]',
};

const MARKERS: Record<StepState | 'returned', string> = {
  memoized: '✓',
  ran: '▶',
  planned: '◆',
  reported: '◷',
  untouched: '',
  returned: '✓',
};

const LABEL_CLASSES: Record<StepState | 'returned', string> = {
  memoized: 'text-ok',
  ran: 'text-run',
  planned: 'text-plan',
  reported: 'text-wait',
  untouched: '',
  returned: 'text-ok',
};

export default function ReplayVisualizer() {
  const [index, setIndex] = useState(0);
  const tabs = useRef<(HTMLButtonElement | null)[]>([]);
  const prefix = useId();
  const count = replay.calls.length;

  function select(next: number, focus: boolean) {
    const bounded = Math.min(Math.max(next, 0), count - 1);
    setIndex(bounded);
    if (focus) tabs.current[bounded]?.focus();
  }

  function onKeyDown(event: KeyboardEvent<HTMLButtonElement>) {
    const next =
      event.key === 'ArrowRight' ? (index + 1) % count
      : event.key === 'ArrowLeft' ? (index - 1 + count) % count
      : event.key === 'Home' ? 0
      : event.key === 'End' ? count - 1
      : undefined;
    if (next === undefined) return;
    event.preventDefault();
    select(next, true);
  }

  // Every call is rendered, on the server too. Without JavaScript they all show, one after another; with it, the
  // site CSS shows the selected one from the first paint, so hydrating moves nothing.
  return (
    <div className="idn-wide not-content @container flex flex-col gap-5">
      <div className="idn-js-only flex flex-wrap items-center gap-3">
        <div role="tablist" aria-label="Calls Inngest makes" className="flex flex-wrap gap-1.5">
          {replay.calls.map((call, i) => (
            <button
              key={i}
              ref={(element) => {
                tabs.current[i] = element;
              }}
              type="button"
              role="tab"
              id={`${prefix}-tab-${i}`}
              aria-selected={i === index}
              aria-controls={`${prefix}-panel-${i}`}
              tabIndex={i === index ? 0 : -1}
              onClick={() => select(i, false)}
              onKeyDown={onKeyDown}
              className={`flex flex-col items-center rounded-md border px-2.5 py-1 font-mono text-xs leading-tight ${
                i === index ? 'border-coral bg-raised text-strong' : 'border-line bg-transparent text-muted hover:border-control'
              }`}
            >
              <span>Call {i + 1}</span>
              <span className={`font-bold ${call.response.status === 200 ? 'text-ok' : 'text-plan'}`}>{call.response.status}</span>
            </button>
          ))}
        </div>
        <div className="flex gap-2 sm:ms-auto">
          <button type="button" className="idn-button" disabled={index === 0} onClick={() => select(index - 1, false)}>
            Previous call
          </button>
          <button type="button" className="idn-button" disabled={index === count - 1} onClick={() => select(index + 1, false)}>
            Next call
          </button>
        </div>
      </div>
      {replay.calls.map((_, i) => (
        <div
          key={i}
          role="tabpanel"
          id={`${prefix}-panel-${i}`}
          aria-labelledby={`${prefix}-tab-${i}`}
          data-idn-inactive={i === index ? undefined : ''}
          className="flex flex-col gap-4"
        >
          <CallView index={i} headingId={`${prefix}-heading-${i}`} />
        </div>
      ))}
    </div>
  );
}

function CallView({ index, headingId }: { index: number; headingId: string }) {
  const call = replay.calls[index];
  const states = stepStates(replay, index);
  const { title, text } = narrative[index];
  return (
    <>
      <div>
        <h3 id={headingId} className="m-0 text-lg font-semibold text-strong">
          Call {index + 1} of {replay.calls.length}: {title}
        </h3>
        <p className="mt-1 mb-0 max-w-3xl text-body">{text}</p>
      </div>
      <div className="grid grid-cols-1 gap-5 @[58rem]:grid-cols-[minmax(0,1.25fr)_minmax(0,1fr)]">
        <Code states={states} returned={call.response.status === 200} />
        <div className="flex min-w-0 flex-col gap-4">
          <Request index={index} />
          <Response index={index} />
        </div>
      </div>
    </>
  );
}

function Code({ states, returned }: { states: Map<string, StepState>; returned: boolean }) {
  return (
    <figure className="m-0 min-w-0">
      <figcaption className="eyebrow mb-2">FulfillOrder.cs</figcaption>
      <pre
        tabIndex={0}
        aria-label="The function's code, with what happened to each step in this call"
        className="relative m-0 overflow-x-auto rounded-lg border border-line bg-[var(--idn-code-bg)] py-3 font-mono text-[0.78rem] leading-relaxed text-[var(--idn-code-text)]"
      >
        <code className="idn-code block min-w-max">
          {lines.map((line) => {
            const state: StepState | 'returned' = line.step ? (states.get(line.step) ?? 'untouched') : line.isReturn && returned ? 'returned' : 'untouched';
            const first = line.step ? lines.find((l) => l.step === line.step)?.number === line.number : line.isReturn;
            return (
              <span key={line.number} className={`flex pe-4 ${LINE_CLASSES[state]}`}>
                <span aria-hidden="true" className={`w-5 shrink-0 select-none text-center font-sans ${LABEL_CLASSES[state]}`}>
                  {first ? MARKERS[state] : ''}
                </span>
                <span aria-hidden="true" className="w-8 shrink-0 select-none pe-3 text-end text-muted">
                  {line.number}
                </span>
                <span className="whitespace-pre">
                  {tokenizeCSharp(line.text).map((token, i) =>
                    token.kind === 'plain' ? token.text : (
                      <span key={i} className={`tok-${token.kind}`}>
                        {token.text}
                      </span>
                    ),
                  )}
                  {first && state !== 'untouched' && <span className="sr-only"> ({LABELS[state]})</span>}
                </span>
              </span>
            );
          })}
        </code>
      </pre>
      <ul aria-hidden="true" className="mt-2 mb-0 flex list-none flex-wrap gap-x-4 gap-y-1 p-0 text-xs text-muted">
        {(['memoized', 'ran', 'planned', 'reported'] as const).map((key) => (
          <li key={key} className="inline-flex items-center gap-1.5">
            <span className={LABEL_CLASSES[key]}>{MARKERS[key]}</span>
            {LABELS[key]}
          </li>
        ))}
      </ul>
    </figure>
  );
}

function Request({ index }: { index: number }) {
  const call = replay.calls[index];
  const memo = Object.entries(call.request.body.steps);
  const stepId = stepIdOf(call);
  const ctx = call.request.body.ctx as { stack: { stack: string[] }; disable_immediate_execution: boolean };
  return (
    <section aria-label="Request" className="rounded-lg border border-line bg-surface p-4">
      <h4 className="eyebrow m-0 mb-2">Request from Inngest</h4>
      <p className="m-0 font-mono text-xs break-all text-strong">
        {call.request.method} {call.request.url.replace(stepId, stepId === 'step' ? stepId : shortHash(stepId))}
      </p>
      {stepId !== 'step' && (
        <p className="mt-1 mb-0 text-sm text-muted">
          stepId is the hash of <code>{replay.stepNames[stepId]}</code>, so the SDK runs only that step.
        </p>
      )}
      <p className="mt-3 mb-1 text-sm font-semibold text-strong">Memoized steps ({memo.length})</p>
      {memo.length === 0 ? (
        <p className="m-0 text-sm text-muted">None yet: steps is an empty object.</p>
      ) : (
        <ul className="m-0 flex list-none flex-col gap-1 p-0 text-sm">
          {memo.map(([hash, value]) => (
            <li key={hash} className="flex min-w-0 flex-wrap items-baseline gap-x-2">
              <code className="text-ok">{replay.stepNames[hash]}</code>
              <span className="font-mono text-xs text-muted">{shortHash(hash)}</span>
              <span className="min-w-0 font-mono text-xs break-all text-body">{JSON.stringify(value)}</span>
            </li>
          ))}
        </ul>
      )}
      <p className="mt-3 mb-0 text-xs text-muted">
        ctx.stack lists {ctx.stack.stack.length} {ctx.stack.stack.length === 1 ? 'step' : 'steps'} in the order they completed;
        disable_immediate_execution is {String(ctx.disable_immediate_execution)}.
      </p>
      <details className="mt-3">
        <summary className="cursor-pointer text-sm font-semibold text-link">Full request body</summary>
        <div className="mt-2">
          <CodeOutput code={JSON.stringify(call.request.body, null, 2)} language="json" label={`Request body of call ${index + 1}`} canCopy={false} />
        </div>
      </details>
    </section>
  );
}

function Response({ index }: { index: number }) {
  const call = replay.calls[index];
  const ops = opsOf(call);
  const done = call.response.status === 200;
  return (
    <section aria-label="Response" className="rounded-lg border border-line bg-surface p-4">
      <h4 className="eyebrow m-0 mb-2">The SDK answers</h4>
      <p className="m-0 flex flex-wrap items-center gap-2 text-sm">
        <span className={`rounded px-1.5 py-0.5 font-mono text-xs font-bold ${done ? 'bg-ok-bg text-ok' : 'bg-plan-bg text-plan'}`}>
          {call.response.status}
        </span>
        <span className="text-strong">
          {done ? 'The function returned; the body is its output.' : `${ops.length === 1 ? 'One op' : `${ops.length} ops`} for Inngest to act on.`}
        </span>
      </p>
      {ops.length > 0 && (
        <ul className="mt-3 mb-0 flex list-none flex-col gap-1 p-0 text-sm">
          {ops.map((op) => (
            <li key={`${op.id}-${op.op}`} className="flex min-w-0 flex-wrap items-baseline gap-x-2">
              <span className="font-mono text-xs font-semibold text-strong">{op.op}</span>
              <code>{replay.stepNames[op.id] ?? op.displayName}</code>
              {'data' in op && <span className="min-w-0 font-mono text-xs break-all text-body">data: {JSON.stringify(op.data)}</span>}
              {op.op === 'Sleep' && <span className="font-mono text-xs text-body">duration: {String(op.opts?.duration)}</span>}
            </li>
          ))}
        </ul>
      )}
      <details className="mt-3">
        <summary className="cursor-pointer text-sm font-semibold text-link">Full response body</summary>
        <div className="mt-2">
          <CodeOutput code={JSON.stringify(call.response.body, null, 2)} language="json" label={`Response body of call ${index + 1}`} canCopy={false} />
        </div>
      </details>
    </section>
  );
}
