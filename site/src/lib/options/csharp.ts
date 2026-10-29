import { type AppModel, type FunctionOptionsModel, isSet, type TriggerModel } from './model.ts';
import type { Duration } from './time.ts';

// C# for a FunctionOptions value and its registration, in the style the docs use.

const FACTORIES = { s: 'FromSeconds', m: 'FromMinutes', h: 'FromHours', d: 'FromDays' } as const;

export function csString(value: string): string {
  const escaped = value
    .replace(/\\/g, '\\\\')
    .replace(/"/g, '\\"')
    .replace(/\r/g, '\\r')
    .replace(/\n/g, '\\n')
    .replace(/\t/g, '\\t');
  return `"${escaped}"`;
}

export function csTimeSpan(duration: Duration): string {
  return `TimeSpan.${FACTORIES[duration.unit]}(${duration.value})`;
}

// "sync-account" becomes SyncAccount, the class the generated registration refers to.
export function className(id: string): string {
  const name = id
    .split(/[^A-Za-z0-9]+/)
    .filter(Boolean)
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join('');
  if (name === '') return 'MyFunction';
  return /^[0-9]/.test(name) ? `Function${name}` : name;
}

function initializer(members: string[]): string {
  return members.length === 0 ? '' : ` { ${members.join(', ')} }`;
}

function trigger(model: TriggerModel): string {
  if (model.kind === 'event') {
    return isSet(model.if)
      ? `Trigger.Event(${csString(model.event)}, @if: ${csString(model.if)})`
      : `Trigger.Event(${csString(model.event)})`;
  }
  return isSet(model.timezone)
    ? `Trigger.Cron(${csString(model.schedule)}, timezone: ${csString(model.timezone)})`
    : `Trigger.Cron(${csString(model.schedule)})`;
}

function list(name: string, items: string[], indent: string): string[] {
  if (items.length === 1) return [`${indent}${name} = [${items[0]}],`];
  return [`${indent}${name} =`, `${indent}[`, ...items.map((item) => `${indent}    ${item},`), `${indent}],`];
}

// The object initializer, indented to sit inside AddFunction(...).
export function functionOptionsCode(options: FunctionOptionsModel, indent = '        '): string {
  const inner = `${indent}    `;
  const lines: string[] = [`${indent}new FunctionOptions`, `${indent}{`, `${inner}Id = ${csString(options.id)},`];

  if (isSet(options.name)) lines.push(`${inner}Name = ${csString(options.name)},`);
  lines.push(...(options.triggers.length === 0 ? [`${inner}Triggers = [],`] : list('Triggers', options.triggers.map(trigger), inner)));
  if (options.retries !== undefined) lines.push(`${inner}Retries = ${options.retries},`);

  if (options.concurrency && options.concurrency.length > 0) {
    const limits = options.concurrency.map((limit) => {
      const members: string[] = [];
      if (isSet(limit.key)) members.push(`Key = ${csString(limit.key)}`);
      if (limit.scope !== 'Function') members.push(`Scope = ConcurrencyScope.${limit.scope}`);
      return `new ConcurrencyLimit(${limit.limit})${initializer(members)}`;
    });
    lines.push(...list('Concurrency', limits, inner));
  }

  if (options.throttle) {
    const { limit, period, burst, key } = options.throttle;
    const members: string[] = [];
    if (burst !== undefined) members.push(`Burst = ${burst}`);
    if (isSet(key)) members.push(`Key = ${csString(key)}`);
    lines.push(`${inner}Throttle = new Throttle(${limit}, ${csTimeSpan(period)})${initializer(members)},`);
  }

  if (options.rateLimit) {
    const { limit, period, key } = options.rateLimit;
    const members = isSet(key) ? [`Key = ${csString(key)}`] : [];
    lines.push(`${inner}RateLimit = new RateLimit(${limit}, ${csTimeSpan(period)})${initializer(members)},`);
  }

  if (options.debounce) {
    const { period, key, timeout } = options.debounce;
    const members: string[] = [];
    if (isSet(key)) members.push(`Key = ${csString(key)}`);
    if (timeout) members.push(`Timeout = ${csTimeSpan(timeout)}`);
    lines.push(`${inner}Debounce = new Debounce(${csTimeSpan(period)})${initializer(members)},`);
  }

  if (options.batchEvents) {
    const { maxSize, timeout, key, if: condition } = options.batchEvents;
    const members: string[] = [];
    if (isSet(key)) members.push(`Key = ${csString(key)}`);
    if (isSet(condition)) members.push(`If = ${csString(condition)}`);
    lines.push(`${inner}BatchEvents = new BatchEvents(${maxSize}, ${csTimeSpan(timeout)})${initializer(members)},`);
  }

  if (options.priority) lines.push(`${inner}Priority = new Priority(${csString(options.priority.run)}),`);
  if (isSet(options.idempotency)) lines.push(`${inner}Idempotency = ${csString(options.idempotency)},`);

  if (options.cancelOn && options.cancelOn.length > 0) {
    const cancellations = options.cancelOn.map((cancel) => {
      const members: string[] = [];
      if (isSet(cancel.match)) members.push(`Match = ${csString(cancel.match)}`);
      else if (isSet(cancel.if)) members.push(`If = ${csString(cancel.if)}`);
      if (cancel.timeout) members.push(`Timeout = ${csTimeSpan(cancel.timeout)}`);
      return `new CancelOn(${csString(cancel.event)})${initializer(members)}`;
    });
    lines.push(...list('CancelOn', cancellations, inner));
  }

  if (options.timeouts && (options.timeouts.start || options.timeouts.finish)) {
    const members: string[] = [];
    if (options.timeouts.start) members.push(`Start = ${csTimeSpan(options.timeouts.start)}`);
    if (options.timeouts.finish) members.push(`Finish = ${csTimeSpan(options.timeouts.finish)}`);
    lines.push(`${inner}Timeouts = new FunctionTimeouts${initializer(members)},`);
  }

  if (options.singleton) {
    const members = isSet(options.singleton.key) ? [`Key = ${csString(options.singleton.key)}`] : [];
    lines.push(`${inner}Singleton = new Singleton(SingletonMode.${options.singleton.mode})${initializer(members)},`);
  }

  lines.push(`${indent}}`);
  return lines.join('\n');
}

export function registrationCode(options: FunctionOptionsModel, app: AppModel): string {
  const configure: string[] = [`options.AppId = ${csString(app.appId)}`];
  if (isSet(app.appVersion)) configure.push(`options.AppVersion = ${csString(app.appVersion.trim())}`);
  const lines = [
    `var functionOptions = ${functionOptionsCode(options, '')};`,
    '',
    'builder.Services',
    configure.length === 1
      ? `    .AddInngest(options => ${configure[0]})`
      : `    .AddInngest(options =>\n    {\n${configure.map((line) => `        ${line};`).join('\n')}\n    })`,
    `    .AddFunction<${className(options.id)}>(functionOptions);`,
  ];
  const code = lines.join('\n');
  if (!app.failureHandler) return code;
  return `// ${className(options.id)} implements IFailureHandler, so the SDK also registers "${options.id}-failure".\n${code}`;
}
