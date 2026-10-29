import { type AppModel, type FunctionOptionsModel, isSet, type TriggerModel } from './model.ts';
import { formatDuration } from './time.ts';

// The sync body the SDK sends for an app, built the way src/Inngest/Registration/RegistrationWriter.cs writes it:
// the same properties in the same order, with options that are not set left out.

export const SDK_VERSION = '0.1.0-preview';

type Json = string | number | boolean | null | Json[] | { [key: string]: Json };
type JsonObject = { [key: string]: Json };

function setIf(target: JsonObject, name: string, value: string | undefined) {
  if (isSet(value)) target[name] = value;
}

// Uri.EscapeDataString: everything but RFC 3986 unreserved characters is escaped.
function escapeDataString(value: string): string {
  return encodeURIComponent(value).replace(/[!'()*]/g, (c) => `%${c.charCodeAt(0).toString(16).toUpperCase()}`);
}

// The URLs a sync reports, as src/Inngest/Registration/ServeUrl.cs builds them: deployId is dropped, every other
// query parameter is kept, and the step URL addresses the function by fnId and stepId.
export function serveUrls(serveUrl: string) {
  const url = new URL(serveUrl);
  const path = `${url.protocol}//${url.host}${url.pathname}`;
  const pairs = url.search.replace(/^\?/, '').split('&').filter(Boolean);
  const without = (...names: string[]) =>
    pairs.filter((pair) => !names.includes(pair.includes('=') ? pair.slice(0, pair.indexOf('=')) : pair)).join('&');
  const appQuery = without('deployId');
  const stepQuery = without('deployId', 'fnId', 'stepId');
  return {
    app: appQuery ? `${path}?${appQuery}` : path,
    step: (compositeId: string) => {
      const addressing = `fnId=${escapeDataString(compositeId)}&stepId=step`;
      return stepQuery ? `${path}?${stepQuery}&${addressing}` : `${path}?${addressing}`;
    },
  };
}

function cronSchedule(schedule: string, timezone: string | undefined): string {
  const hasTimezone = schedule.startsWith('TZ=') || schedule.startsWith('CRON_TZ=');
  return !isSet(timezone) || hasTimezone ? schedule : `TZ=${timezone} ${schedule}`;
}

function trigger(model: TriggerModel): JsonObject {
  if (model.kind === 'event') {
    const value: JsonObject = { event: model.event };
    setIf(value, 'expression', model.if);
    return value;
  }
  return { cron: cronSchedule(model.schedule, model.timezone) };
}

const SCOPES = { Function: 'fn', Environment: 'env', Account: 'account' } as const;

function writeFunction(options: FunctionOptionsModel, compositeId: string, stepUrl: string): JsonObject {
  const fn: JsonObject = {
    id: compositeId,
    name: isSet(options.name) ? options.name : options.id,
    triggers: options.triggers.map(trigger),
  };

  const step: JsonObject = { id: 'step', name: 'step', runtime: { type: 'http', url: stepUrl } };
  // The attempts field holds the number of retries, not attempts.
  if (options.retries !== undefined) step.retries = { attempts: options.retries };
  fn.steps = { step };

  if (options.concurrency && options.concurrency.length > 0) {
    fn.concurrency = options.concurrency.map((limit) => {
      const value: JsonObject = { limit: limit.limit };
      setIf(value, 'key', limit.key);
      value.scope = SCOPES[limit.scope];
      return value;
    });
  }

  if (options.throttle) {
    const throttle: JsonObject = { limit: options.throttle.limit, period: formatDuration(options.throttle.period) };
    if (options.throttle.burst !== undefined) throttle.burst = options.throttle.burst;
    setIf(throttle, 'key', options.throttle.key);
    fn.throttle = throttle;
  }

  if (options.rateLimit) {
    const rateLimit: JsonObject = { limit: options.rateLimit.limit, period: formatDuration(options.rateLimit.period) };
    setIf(rateLimit, 'key', options.rateLimit.key);
    fn.rateLimit = rateLimit;
  }

  setIf(fn, 'idempotency', options.idempotency);

  if (options.debounce) {
    const debounce: JsonObject = { period: formatDuration(options.debounce.period) };
    setIf(debounce, 'key', options.debounce.key);
    if (options.debounce.timeout) debounce.timeout = formatDuration(options.debounce.timeout);
    fn.debounce = debounce;
  }

  if (options.batchEvents) {
    const batch: JsonObject = { maxSize: options.batchEvents.maxSize, timeout: formatDuration(options.batchEvents.timeout) };
    setIf(batch, 'key', options.batchEvents.key);
    setIf(batch, 'if', options.batchEvents.if);
    fn.batchEvents = batch;
  }

  if (options.priority) fn.priority = { run: options.priority.run };

  if (options.cancelOn && options.cancelOn.length > 0) {
    fn.cancel = options.cancelOn.map((cancel) => {
      const value: JsonObject = { event: cancel.event };
      if (cancel.timeout) value.timeout = formatDuration(cancel.timeout);
      // Match is shorthand for comparing one property of both events, and replaces If.
      setIf(value, 'if', isSet(cancel.match) ? `event.${cancel.match} == async.${cancel.match}` : cancel.if);
      return value;
    });
  }

  const { start, finish } = options.timeouts ?? {};
  if (start || finish) {
    const timeouts: JsonObject = {};
    if (start) timeouts.start = formatDuration(start);
    if (finish) timeouts.finish = formatDuration(finish);
    fn.timeouts = timeouts;
  }

  if (options.singleton) {
    const singleton: JsonObject = { mode: options.singleton.mode === 'Skip' ? 'skip' : 'cancel' };
    setIf(singleton, 'key', options.singleton.key);
    fn.singleton = singleton;
  }

  return fn;
}

// A failure handler is a second function, "<id>-failure", triggered by inngest/function.failed for this function,
// as src/Inngest/Functions/FailureFunction.cs and RegisteredFunction.cs define it.
export function failureOptions(options: FunctionOptionsModel, appId: string): FunctionOptionsModel {
  const failedId = `${appId}-${options.id}`.replace(/\\/g, '\\\\').replace(/'/g, "\\'");
  return {
    id: `${options.id}-failure`,
    name: `${options.name ?? options.id} (failure)`,
    triggers: [{ kind: 'event', event: 'inngest/function.failed', if: `event.data.function_id == '${failedId}'` }],
    retries: 1,
  };
}

export function registrationFunctions(options: FunctionOptionsModel, app: AppModel): JsonObject[] {
  const urls = serveUrls(app.serveUrl);
  const all = app.failureHandler ? [options, failureOptions(options, app.appId)] : [options];
  return all.map((fn) => {
    const compositeId = `${app.appId}-${fn.id}`;
    return writeFunction(fn, compositeId, urls.step(compositeId));
  });
}

export function registrationBody(options: FunctionOptionsModel, app: AppModel, sdkVersion = SDK_VERSION): JsonObject {
  const body: JsonObject = {
    url: serveUrls(app.serveUrl).app,
    deployType: 'ping',
    appName: app.appId,
  };
  setIf(body, 'appVersion', app.appVersion?.trim());
  body.sdk = `dotnet:v${sdkVersion}`;
  body.v = '0.1';
  body.framework = 'aspnetcore';
  body.capabilities = { trust_probe: 'v1' };
  body.functions = registrationFunctions(options, app);
  return body;
}
