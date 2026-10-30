import { type ReactNode, useId, useMemo, useState } from 'react';
import { attributesCode, registrationCode } from '../../lib/options/csharp';
import type {
  AppModel,
  CancelOnModel,
  ConcurrencyModel,
  ConcurrencyScope,
  FunctionOptionsModel,
  TriggerModel,
} from '../../lib/options/model';
import { presets } from '../../lib/options/presets';
import { registrationBody } from '../../lib/options/registration';
import type { Duration, Unit } from '../../lib/options/time';
import { validate } from '../../lib/options/validate';
import CodeOutput from './CodeOutput';
import Tabs from './Tabs';
import { useEnhanced } from './useEnhanced';
import { useFunctionStyle } from './useFunctionStyle';

const DOCS = 'https://www.inngest.com/docs';

type Update = <K extends keyof FunctionOptionsModel>(key: K, value: FunctionOptionsModel[K]) => void;

export default function OptionsBuilder() {
  const enhanced = useEnhanced();
  const [presetId, setPresetId] = useState(presets[0].id);
  const [app, setApp] = useState<AppModel>(presets[0].app);
  const [options, setOptions] = useState<FunctionOptionsModel>(presets[0].options);
  const [style, setStyle] = useFunctionStyle();
  const [jsonShown, setJsonShown] = useState(false);

  const problems = useMemo(() => validate(options), [options]);
  const json = useMemo(() => JSON.stringify(registrationBody(options, app), null, 2), [options, app]);
  const attributes = useMemo(() => attributesCode(options, app), [options, app]);
  const code = useMemo(() => registrationCode(options, app), [options, app]);

  const update: Update = (key, value) => setOptions((current) => ({ ...current, [key]: value }));

  // The C# tabs carry the labels of the docs' synced tabs, so the output opens on the form the reader picked there,
  // and picking one here picks it there too.
  function chooseOutput(id: string) {
    setJsonShown(id === 'json');
    if (id === 'attributes') setStyle('Attributes');
    if (id === 'code') setStyle('Code first');
  }

  function choosePreset(id: string) {
    const preset = presets.find((p) => p.id === id);
    if (!preset) return;
    setPresetId(id);
    setApp(preset.app);
    setOptions(preset.options);
  }

  const preset = presets.find((p) => p.id === presetId);

  return (
    <div className="idn-wide not-content @container">
      <noscript>
        <p className="mb-4 rounded-md border border-line bg-surface p-3 text-sm text-body">
          JavaScript is off, so the form cannot update the output. The output shows the preset that is selected
          below.
        </p>
      </noscript>

      <div className="grid grid-cols-1 gap-8 @[60rem]:grid-cols-[minmax(0,1fr)_minmax(0,1.05fr)]">
        <form aria-label="Function options" onSubmit={(event) => event.preventDefault()}>
          {/* Read-only until the island hydrates, because nothing can update the output before that. */}
          <fieldset disabled={!enhanced} className="m-0 flex min-w-0 flex-col gap-5 border-0 p-0">
          <fieldset className="flex flex-col gap-3">
            <legend className="eyebrow mb-2">Start from a preset</legend>
            <div className="flex flex-wrap gap-2">
              {presets.map((p) => (
                <button
                  key={p.id}
                  type="button"
                  className="idn-button"
                  aria-pressed={p.id === presetId}
                  onClick={() => choosePreset(p.id)}
                >
                  {p.label}
                </button>
              ))}
            </div>
            {preset && <p className="m-0 text-sm text-muted">{preset.summary}</p>}
          </fieldset>

          <Section title="Function">
            <div className="grid grid-cols-1 gap-3 @[28rem]:grid-cols-2">
              <TextField label="App id" value={app.appId} mono onChange={(appId) => setApp({ ...app, appId })} />
              <TextField label="Function id" value={options.id} mono onChange={(id) => update('id', id)} />
              <TextField
                label="Name (optional)"
                value={options.name ?? ''}
                onChange={(name) => update('name', name === '' ? undefined : name)}
              />
              <NumberField
                label="Retries (empty: Inngest's default of 4)"
                value={options.retries}
                min={0}
                max={20}
                onChange={(retries) => update('retries', retries)}
              />
            </div>
            <label className="idn-check mt-1 text-sm">
              <input
                type="checkbox"
                checked={app.failureHandler}
                onChange={(event) => setApp({ ...app, failureHandler: event.target.checked })}
              />
              The class implements <code>IFailureHandler</code>
            </label>
          </Section>

          <Section title="Triggers" docs={`${DOCS}/durable-execution/guides-and-advanced/events-and-triggers/event-and-trigger-concepts`}>
            <Triggers triggers={options.triggers} onChange={(triggers) => update('triggers', triggers)} />
          </Section>

          <Optional
            title="Concurrency"
            docs={`${DOCS}/durable-execution/flow-control/concurrency`}
            enabled={options.concurrency !== undefined}
            onToggle={(on) => update('concurrency', on ? [{ limit: 5, scope: 'Function' }] : undefined)}
          >
            {options.concurrency && <Concurrency limits={options.concurrency} onChange={(limits) => update('concurrency', limits)} />}
          </Optional>

          <Optional
            title="Throttle"
            docs={`${DOCS}/durable-execution/flow-control/throttling`}
            enabled={options.throttle !== undefined}
            onToggle={(on) => update('throttle', on ? { limit: 10, period: { value: 1, unit: 'm' } } : undefined)}
          >
            {options.throttle && (
              <div className="grid grid-cols-1 gap-3 @[28rem]:grid-cols-2">
                <NumberField label="Limit" value={options.throttle.limit} min={1} required onChange={(limit) => update('throttle', { ...options.throttle!, limit: limit ?? 0 })} />
                <DurationField label="Period" value={options.throttle.period} onChange={(period) => update('throttle', { ...options.throttle!, period })} />
                <NumberField label="Burst (optional)" value={options.throttle.burst} min={0} onChange={(burst) => update('throttle', { ...options.throttle!, burst })} />
                <TextField label="Key (optional)" value={options.throttle.key ?? ''} mono onChange={(key) => update('throttle', { ...options.throttle!, key: key || undefined })} />
              </div>
            )}
          </Optional>

          <Optional
            title="Rate limit"
            docs={`${DOCS}/durable-execution/flow-control/rate-limiting`}
            enabled={options.rateLimit !== undefined}
            onToggle={(on) => update('rateLimit', on ? { limit: 100, period: { value: 1, unit: 'h' } } : undefined)}
          >
            {options.rateLimit && (
              <div className="grid grid-cols-1 gap-3 @[28rem]:grid-cols-2">
                <NumberField label="Limit" value={options.rateLimit.limit} min={1} required onChange={(limit) => update('rateLimit', { ...options.rateLimit!, limit: limit ?? 0 })} />
                <DurationField label="Period" value={options.rateLimit.period} onChange={(period) => update('rateLimit', { ...options.rateLimit!, period })} />
                <TextField label="Key (optional)" value={options.rateLimit.key ?? ''} mono onChange={(key) => update('rateLimit', { ...options.rateLimit!, key: key || undefined })} />
              </div>
            )}
          </Optional>

          <Optional
            title="Debounce"
            docs={`${DOCS}/durable-execution/flow-control/debounce`}
            enabled={options.debounce !== undefined}
            onToggle={(on) => update('debounce', on ? { period: { value: 30, unit: 's' } } : undefined)}
          >
            {options.debounce && (
              <div className="grid grid-cols-1 gap-3 @[28rem]:grid-cols-2">
                <DurationField label="Period" value={options.debounce.period} onChange={(period) => update('debounce', { ...options.debounce!, period })} />
                <TextField label="Key (optional)" value={options.debounce.key ?? ''} mono onChange={(key) => update('debounce', { ...options.debounce!, key: key || undefined })} />
                <OptionalDuration
                  label="Timeout"
                  value={options.debounce.timeout}
                  fallback={{ value: 5, unit: 'm' }}
                  onChange={(timeout) => update('debounce', { ...options.debounce!, timeout })}
                />
              </div>
            )}
          </Optional>

          <Optional
            title="Batch events"
            docs={`${DOCS}/durable-execution/flow-control/batching`}
            enabled={options.batchEvents !== undefined}
            onToggle={(on) => update('batchEvents', on ? { maxSize: 100, timeout: { value: 10, unit: 's' } } : undefined)}
          >
            {options.batchEvents && (
              <div className="grid grid-cols-1 gap-3 @[28rem]:grid-cols-2">
                <NumberField label="Maximum size" value={options.batchEvents.maxSize} min={2} required onChange={(maxSize) => update('batchEvents', { ...options.batchEvents!, maxSize: maxSize ?? 0 })} />
                <DurationField label="Timeout" value={options.batchEvents.timeout} onChange={(timeout) => update('batchEvents', { ...options.batchEvents!, timeout })} />
                <TextField label="Key (optional)" value={options.batchEvents.key ?? ''} mono onChange={(key) => update('batchEvents', { ...options.batchEvents!, key: key || undefined })} />
                <TextField label="If (optional)" value={options.batchEvents.if ?? ''} mono onChange={(condition) => update('batchEvents', { ...options.batchEvents!, if: condition || undefined })} />
              </div>
            )}
          </Optional>

          <Optional
            title="Priority"
            docs={`${DOCS}/durable-execution/flow-control/priority`}
            enabled={options.priority !== undefined}
            onToggle={(on) => update('priority', on ? { run: 'event.data.vip ? 120 : 0' } : undefined)}
          >
            {options.priority && (
              <TextField label="Run (an expression, in seconds)" value={options.priority.run} mono onChange={(run) => update('priority', { run })} />
            )}
          </Optional>

          <Optional
            title="Idempotency"
            docs={`${DOCS}/durable-execution/guides-and-advanced/idempotency`}
            enabled={options.idempotency !== undefined}
            onToggle={(on) => update('idempotency', on ? 'event.data.requestId' : undefined)}
          >
            {options.idempotency !== undefined && (
              <TextField label="Key expression" value={options.idempotency} mono onChange={(idempotency) => update('idempotency', idempotency)} />
            )}
          </Optional>

          <Optional
            title="Singleton"
            docs={`${DOCS}/durable-execution/flow-control/singleton`}
            enabled={options.singleton !== undefined}
            onToggle={(on) => update('singleton', on ? { mode: 'Skip' } : undefined)}
          >
            {options.singleton && (
              <div className="grid grid-cols-1 gap-3 @[28rem]:grid-cols-2">
                <SelectField
                  label="Mode"
                  value={options.singleton.mode}
                  options={[
                    ['Skip', 'Skip the new run'],
                    ['Cancel', 'Cancel the running one'],
                  ]}
                  onChange={(mode) => update('singleton', { ...options.singleton!, mode: mode as 'Skip' | 'Cancel' })}
                />
                <TextField label="Key (optional)" value={options.singleton.key ?? ''} mono onChange={(key) => update('singleton', { ...options.singleton!, key: key || undefined })} />
              </div>
            )}
          </Optional>

          <Optional
            title="Cancel on events"
            docs={`${DOCS}/durable-execution/guides-and-advanced/cancellation/events`}
            enabled={options.cancelOn !== undefined}
            onToggle={(on) => update('cancelOn', on ? [{ event: 'account/deleted', match: 'data.accountId' }] : undefined)}
          >
            {options.cancelOn && <CancelOnList items={options.cancelOn} onChange={(cancelOn) => update('cancelOn', cancelOn)} />}
          </Optional>

          <Optional
            title="Timeouts"
            docs={`${DOCS}/durable-execution/guides-and-advanced/cancellation/timeouts`}
            enabled={options.timeouts !== undefined}
            onToggle={(on) => update('timeouts', on ? { finish: { value: 1, unit: 'h' } } : undefined)}
          >
            {options.timeouts && (
              <div className="grid grid-cols-1 gap-3 @[28rem]:grid-cols-2">
                <OptionalDuration label="Start" value={options.timeouts.start} fallback={{ value: 10, unit: 'm' }} onChange={(start) => update('timeouts', { ...options.timeouts, start })} />
                <OptionalDuration label="Finish" value={options.timeouts.finish} fallback={{ value: 1, unit: 'h' }} onChange={(finish) => update('timeouts', { ...options.timeouts, finish })} />
              </div>
            )}
          </Optional>
          </fieldset>
        </form>

        <div className="flex flex-col gap-4 @[60rem]:sticky @[60rem]:top-[calc(var(--sl-nav-height)+1.5rem)] @[60rem]:self-start">
          <div role="status" aria-live="polite">
            {problems.length > 0 ? (
              <div className="rounded-md border border-fail bg-fail-bg p-3 text-sm text-strong">
                <p className="m-0 font-semibold text-fail">The SDK would refuse these options when the app starts:</p>
                <ul className="mt-2 mb-0 pl-5">
                  {problems.map((problem) => (
                    <li key={problem}>{problem}</li>
                  ))}
                </ul>
              </div>
            ) : (
              <p className="m-0 rounded-md border border-line bg-surface p-3 text-sm text-body">
                <span className="font-semibold text-ok">Valid.</span> The SDK accepts these options and sends the
                JSON below when Inngest syncs the app.
              </p>
            )}
          </div>
          <Tabs
            label="Generated output"
            selected={jsonShown ? 'json' : style === 'Code first' ? 'code' : 'attributes'}
            onSelect={chooseOutput}
            tabs={[
              {
                id: 'attributes',
                label: 'Attributes',
                content: (
                  <>
                    <CodeOutput code={attributes} language="csharp" label="C# attributes and registration" canCopy />
                    <p className="mt-2 mb-0 text-sm text-muted">
                      Middleware and checkpointing have no attributes. Pass them to <code>AddFunction</code> in a
                      callback, <code>{'options => options with { ... }'}</code>, which{' '}
                      <a className="text-link" href="/inngest-dotnet/guides/functions-and-triggers/#code-first-or-attributes">
                        starts from the attributes
                      </a>
                      .
                    </p>
                  </>
                ),
              },
              { id: 'code', label: 'Code first', content: <CodeOutput code={code} language="csharp" label="C# registration with FunctionOptions" canCopy /> },
              { id: 'json', label: 'Registration JSON', content: <CodeOutput code={json} language="json" label="Registration JSON" canCopy /> },
            ]}
          />
        </div>
      </div>
    </div>
  );
}

function Section({ title, docs, children }: { title: string; docs?: string; children: ReactNode }) {
  return (
    <fieldset className="flex flex-col gap-3 rounded-lg border border-line p-4">
      <legend className="px-1 font-semibold text-strong">{title}</legend>
      {children}
      {docs && <DocsLink href={docs} topic={title} />}
    </fieldset>
  );
}

function Optional(props: { title: string; docs: string; enabled: boolean; onToggle: (on: boolean) => void; children: ReactNode }) {
  return (
    <fieldset className={`flex flex-col gap-3 rounded-lg border p-4 ${props.enabled ? 'border-control' : 'border-line'}`}>
      <legend className="px-1">
        <label className="idn-check">
          <input type="checkbox" checked={props.enabled} onChange={(event) => props.onToggle(event.target.checked)} />
          {props.title}
        </label>
      </legend>
      {props.enabled && props.children}
      <DocsLink href={props.docs} topic={props.title} />
    </fieldset>
  );
}

function DocsLink({ href, topic }: { href: string; topic: string }) {
  return (
    <a className="self-start text-xs text-link" href={href}>
      {topic} in the Inngest docs
    </a>
  );
}

function TextField(props: { label: string; value: string; mono?: boolean; onChange: (value: string) => void }) {
  return (
    <label className="idn-field">
      <span>{props.label}</span>
      <input
        className={`idn-input${props.mono ? ' mono' : ''}`}
        type="text"
        value={props.value}
        spellCheck={false}
        onChange={(event) => props.onChange(event.target.value)}
      />
    </label>
  );
}

function NumberField(props: {
  label: string;
  value: number | undefined;
  min?: number;
  max?: number;
  required?: boolean;
  onChange: (value: number | undefined) => void;
}) {
  return (
    <label className="idn-field">
      <span>{props.label}</span>
      <input
        className="idn-input"
        type="number"
        inputMode="numeric"
        step={1}
        min={props.min}
        max={props.max}
        required={props.required}
        value={props.value ?? ''}
        onChange={(event) => {
          const text = event.target.value;
          props.onChange(text === '' ? undefined : Math.trunc(Number(text)));
        }}
      />
    </label>
  );
}

function SelectField(props: { label: string; value: string; options: [string, string][]; onChange: (value: string) => void }) {
  return (
    <label className="idn-field">
      <span>{props.label}</span>
      <select className="idn-input" value={props.value} onChange={(event) => props.onChange(event.target.value)}>
        {props.options.map(([value, label]) => (
          <option key={value} value={value}>
            {label}
          </option>
        ))}
      </select>
    </label>
  );
}

const UNITS: [Unit, string][] = [
  ['s', 'seconds'],
  ['m', 'minutes'],
  ['h', 'hours'],
  ['d', 'days'],
];

function DurationField(props: { label: string; value: Duration; onChange: (value: Duration) => void }) {
  const id = useId();
  return (
    <div className="idn-field" role="group" aria-labelledby={`${id}-label`}>
      <span id={`${id}-label`}>{props.label}</span>
      <div className="flex gap-2">
        <input
          className="idn-input"
          type="number"
          inputMode="numeric"
          min={0}
          step={1}
          aria-label={`${props.label}, amount`}
          value={props.value.value}
          onChange={(event) => props.onChange({ ...props.value, value: Math.max(0, Math.trunc(Number(event.target.value || 0))) })}
        />
        <select
          className="idn-input"
          aria-label={`${props.label}, unit`}
          value={props.value.unit}
          onChange={(event) => props.onChange({ ...props.value, unit: event.target.value as Unit })}
        >
          {UNITS.map(([unit, label]) => (
            <option key={unit} value={unit}>
              {label}
            </option>
          ))}
        </select>
      </div>
    </div>
  );
}

function OptionalDuration(props: { label: string; value: Duration | undefined; fallback: Duration; onChange: (value: Duration | undefined) => void }) {
  return (
    <div className="flex flex-col gap-2">
      <label className="idn-check text-sm">
        <input type="checkbox" checked={props.value !== undefined} onChange={(event) => props.onChange(event.target.checked ? props.fallback : undefined)} />
        {props.label}
      </label>
      {props.value && <DurationField label={props.label} value={props.value} onChange={props.onChange} />}
    </div>
  );
}

function replace<T>(items: T[], index: number, item: T): T[] {
  return items.map((current, i) => (i === index ? item : current));
}

function Triggers({ triggers, onChange }: { triggers: TriggerModel[]; onChange: (triggers: TriggerModel[]) => void }) {
  return (
    <div className="flex flex-col gap-3">
      {triggers.length === 0 && (
        <p className="m-0 text-sm text-muted">No triggers: the function only runs when another function invokes it.</p>
      )}
      {triggers.map((trigger, index) => (
        <div key={index} className="grid grid-cols-1 gap-3 rounded-md bg-surface p-3 @[28rem]:grid-cols-2">
          {trigger.kind === 'event' ? (
            <>
              <TextField label={`Trigger ${index + 1}: event`} value={trigger.event} mono onChange={(event) => onChange(replace(triggers, index, { ...trigger, event }))} />
              <TextField label="If (optional)" value={trigger.if ?? ''} mono onChange={(condition) => onChange(replace(triggers, index, { ...trigger, if: condition || undefined }))} />
            </>
          ) : (
            <>
              <TextField label={`Trigger ${index + 1}: cron schedule`} value={trigger.schedule} mono onChange={(schedule) => onChange(replace(triggers, index, { ...trigger, schedule }))} />
              <TextField label="Time zone (optional)" value={trigger.timezone ?? ''} mono onChange={(timezone) => onChange(replace(triggers, index, { ...trigger, timezone: timezone || undefined }))} />
            </>
          )}
          <button type="button" className="idn-button justify-self-start" onClick={() => onChange(triggers.filter((_, i) => i !== index))}>
            Remove trigger {index + 1}
          </button>
        </div>
      ))}
      <div className="flex flex-wrap gap-2">
        <button type="button" className="idn-button" disabled={triggers.length >= 10} onClick={() => onChange([...triggers, { kind: 'event', event: 'app/something.happened' }])}>
          Add an event trigger
        </button>
        <button type="button" className="idn-button" disabled={triggers.length >= 10} onClick={() => onChange([...triggers, { kind: 'cron', schedule: '0 9 * * 1-5' }])}>
          Add a cron trigger
        </button>
      </div>
    </div>
  );
}

const SCOPES: [ConcurrencyScope, string][] = [
  ['Function', 'This function'],
  ['Environment', 'Environment'],
  ['Account', 'Account'],
];

function Concurrency({ limits, onChange }: { limits: ConcurrencyModel[]; onChange: (limits: ConcurrencyModel[]) => void }) {
  return (
    <div className="flex flex-col gap-3">
      {limits.map((limit, index) => (
        <div key={index} className="grid grid-cols-1 gap-3 rounded-md bg-surface p-3 @[28rem]:grid-cols-3">
          <NumberField label={`Limit ${index + 1}`} value={limit.limit} min={1} required onChange={(value) => onChange(replace(limits, index, { ...limit, limit: value ?? 0 }))} />
          <TextField label="Key (optional)" value={limit.key ?? ''} mono onChange={(key) => onChange(replace(limits, index, { ...limit, key: key || undefined }))} />
          <SelectField label="Scope" value={limit.scope} options={SCOPES} onChange={(scope) => onChange(replace(limits, index, { ...limit, scope: scope as ConcurrencyScope }))} />
          {limits.length > 1 && (
            <button type="button" className="idn-button justify-self-start" onClick={() => onChange(limits.filter((_, i) => i !== index))}>
              Remove limit {index + 1}
            </button>
          )}
        </div>
      ))}
      {limits.length < 2 && (
        <button type="button" className="idn-button self-start" onClick={() => onChange([...limits, { limit: 1, key: 'event.data.accountId', scope: 'Function' }])}>
          Add a second limit
        </button>
      )}
    </div>
  );
}

function CancelOnList({ items, onChange }: { items: CancelOnModel[]; onChange: (items: CancelOnModel[]) => void }) {
  return (
    <div className="flex flex-col gap-3">
      {items.map((item, index) => {
        const byMatch = item.if === undefined;
        return (
          <div key={index} className="grid grid-cols-1 gap-3 rounded-md bg-surface p-3 @[28rem]:grid-cols-2">
            <TextField label={`Event ${index + 1}`} value={item.event} mono onChange={(event) => onChange(replace(items, index, { ...item, event }))} />
            <SelectField
              label="Compare by"
              value={byMatch ? 'match' : 'if'}
              options={[
                ['match', 'A property of both events'],
                ['if', 'An expression'],
              ]}
              onChange={(mode) =>
                onChange(
                  replace(
                    items,
                    index,
                    mode === 'match'
                      ? { event: item.event, match: 'data.accountId', timeout: item.timeout }
                      : { event: item.event, if: 'async.data.accountId == event.data.accountId', timeout: item.timeout },
                  ),
                )
              }
            />
            {byMatch ? (
              <TextField label="Match (a property path)" value={item.match ?? ''} mono onChange={(match) => onChange(replace(items, index, { ...item, match }))} />
            ) : (
              <TextField label="If" value={item.if ?? ''} mono onChange={(condition) => onChange(replace(items, index, { ...item, if: condition }))} />
            )}
            <OptionalDuration label="Timeout" value={item.timeout} fallback={{ value: 1, unit: 'd' }} onChange={(timeout) => onChange(replace(items, index, { ...item, timeout }))} />
            <button type="button" className="idn-button justify-self-start" onClick={() => onChange(items.filter((_, i) => i !== index))}>
              Remove event {index + 1}
            </button>
          </div>
        );
      })}
      <button
        type="button"
        className="idn-button self-start"
        disabled={items.length >= 5}
        onClick={() => onChange([...items, { event: 'account/deleted', match: 'data.accountId' }])}
      >
        Add an event
      </button>
    </div>
  );
}
