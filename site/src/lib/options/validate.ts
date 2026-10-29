import { type FunctionOptionsModel, isSet } from './model.ts';
import { describeSeconds, toSeconds } from './time.ts';

// The checks src/Inngest/Functions/FunctionOptionsValidator.cs makes at startup, with its messages. The SDK throws
// on the first problem; the builder lists all of them, in the order the SDK checks them.

const MAX_RETRIES = 20;
const MAX_TRIGGERS = 10;
const MAX_CONCURRENCY_LIMITS = 2;
const MAX_CANCELLATIONS = 5;
const MIN_BATCH_SIZE = 2;
const MIN_PERIOD = 1;
const MAX_BATCH_TIMEOUT = 60;
const MAX_DEBOUNCE_PERIOD = 7 * 86400;
const MAX_RATE_LIMIT_PERIOD = 86400;

export function validate(options: FunctionOptionsModel): string[] {
  if (!isSet(options.id)) {
    const which = isSet(options.name) ? `The function named "${options.name}"` : 'A function';
    return [`${which} has no Id. Set FunctionOptions.Id to an id that is unique in the app and never changes.`];
  }

  const problems: string[] = [];
  const invalid = (problem: string) => problems.push(`Function "${options.id}": ${problem}`);

  if (options.retries !== undefined && (options.retries < 0 || options.retries > MAX_RETRIES)) {
    invalid(`Retries must be between 0 and ${MAX_RETRIES}, but was ${options.retries}.`);
  }

  if (options.triggers.length > MAX_TRIGGERS) {
    invalid(`A function can have at most ${MAX_TRIGGERS} triggers, but has ${options.triggers.length}.`);
  }
  for (const trigger of options.triggers) {
    if (trigger.kind === 'event' && !isSet(trigger.event)) invalid('An event trigger needs an event name.');
    if (trigger.kind === 'cron' && !isSet(trigger.schedule)) invalid('A cron trigger needs a schedule.');
  }

  const limits = options.concurrency;
  if (limits) {
    if (limits.length > MAX_CONCURRENCY_LIMITS) {
      invalid(`Concurrency allows at most ${MAX_CONCURRENCY_LIMITS} limits, but ${limits.length} were given.`);
    }
    for (const limit of limits) {
      if (limit.limit <= 0) invalid(`A concurrency limit must be greater than 0, but one was ${limit.limit}.`);
      if (limit.scope !== 'Function' && !isSet(limit.key)) {
        invalid(`A concurrency limit with the ${limit.scope} scope needs a Key. Only the Function scope can go without one.`);
      }
    }
  }

  const throttle = options.throttle;
  if (throttle) {
    const period = toSeconds(throttle.period);
    if (throttle.limit <= 0) invalid(`Throttle.Limit must be greater than 0, but was ${throttle.limit}.`);
    if (period < MIN_PERIOD) invalid(`Throttle.Period must be at least 1s, but was ${describeSeconds(period)}.`);
    if (throttle.burst !== undefined && throttle.burst < 0) {
      invalid(`Throttle.Burst must not be negative, but was ${throttle.burst}.`);
    }
  }

  const rateLimit = options.rateLimit;
  if (rateLimit) {
    const period = toSeconds(rateLimit.period);
    if (rateLimit.limit <= 0) invalid(`RateLimit.Limit must be greater than 0, but was ${rateLimit.limit}.`);
    if (period < MIN_PERIOD || period > MAX_RATE_LIMIT_PERIOD) {
      invalid(`RateLimit.Period must be between 1s and 24h, but was ${describeSeconds(period)}.`);
    }
  }

  const debounce = options.debounce;
  if (debounce) {
    const period = toSeconds(debounce.period);
    if (period < MIN_PERIOD || period > MAX_DEBOUNCE_PERIOD) {
      invalid(`Debounce.Period must be between 1s and 7d, but was ${describeSeconds(period)}.`);
    }
    if (debounce.timeout && toSeconds(debounce.timeout) <= 0) {
      invalid(`Debounce.Timeout must be greater than zero, but was ${describeSeconds(toSeconds(debounce.timeout))}.`);
    }
  }

  const batch = options.batchEvents;
  if (batch) {
    const timeout = toSeconds(batch.timeout);
    if (batch.maxSize < MIN_BATCH_SIZE) invalid(`BatchEvents.MaxSize must be at least ${MIN_BATCH_SIZE}, but was ${batch.maxSize}.`);
    if (timeout < MIN_PERIOD || timeout > MAX_BATCH_TIMEOUT) {
      invalid(`BatchEvents.Timeout must be between 1s and 60s, but was ${describeSeconds(timeout)}.`);
    }
    if (options.cancelOn && options.cancelOn.length > 0) invalid('BatchEvents cannot be combined with CancelOn.');
    if (options.debounce) invalid('BatchEvents cannot be combined with Debounce.');
    if (options.singleton) invalid('BatchEvents cannot be combined with Singleton.');
    if (options.priority) invalid('BatchEvents cannot be combined with Priority.');
  }

  if (options.priority && !isSet(options.priority.run)) {
    invalid('Priority.Run needs an expression, for example "event.data.vip ? 120 : 0".');
  }

  const cancellations = options.cancelOn;
  if (cancellations) {
    if (cancellations.length > MAX_CANCELLATIONS) {
      invalid(`CancelOn allows at most ${MAX_CANCELLATIONS} entries, but ${cancellations.length} were given.`);
    }
    for (const cancel of cancellations) {
      if (!isSet(cancel.event)) invalid('A CancelOn entry needs an event name.');
      if (cancel.timeout && toSeconds(cancel.timeout) <= 0) {
        invalid(`The CancelOn timeout for "${cancel.event}" must be greater than zero, but was ${describeSeconds(toSeconds(cancel.timeout))}.`);
      }
    }
  }

  const timeouts = options.timeouts;
  if (timeouts?.start && toSeconds(timeouts.start) <= 0) {
    invalid(`Timeouts.Start must be greater than zero, but was ${describeSeconds(toSeconds(timeouts.start))}.`);
  }
  if (timeouts?.finish && toSeconds(timeouts.finish) <= 0) {
    invalid(`Timeouts.Finish must be greater than zero, but was ${describeSeconds(toSeconds(timeouts.finish))}.`);
  }

  return problems;
}

