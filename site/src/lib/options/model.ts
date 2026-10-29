import type { Duration } from './time.ts';

// The function options builder's model of FunctionOptions (src/Inngest/Functions/FunctionOptions.cs and
// FlowControl.cs). Optional parts are undefined when they are not set, as the C# properties are null.

export type ConcurrencyScope = 'Function' | 'Environment' | 'Account';
export type SingletonMode = 'Skip' | 'Cancel';

export interface EventTriggerModel {
  kind: 'event';
  event: string;
  if?: string;
}

export interface CronTriggerModel {
  kind: 'cron';
  schedule: string;
  timezone?: string;
}

export type TriggerModel = EventTriggerModel | CronTriggerModel;

export interface ConcurrencyModel {
  limit: number;
  key?: string;
  scope: ConcurrencyScope;
}

export interface CancelOnModel {
  event: string;
  match?: string;
  if?: string;
  timeout?: Duration;
}

export interface FunctionOptionsModel {
  id: string;
  name?: string;
  triggers: TriggerModel[];
  retries?: number;
  concurrency?: ConcurrencyModel[];
  throttle?: { limit: number; period: Duration; burst?: number; key?: string };
  rateLimit?: { limit: number; period: Duration; key?: string };
  debounce?: { period: Duration; key?: string; timeout?: Duration };
  batchEvents?: { maxSize: number; timeout: Duration; key?: string; if?: string };
  priority?: { run: string };
  idempotency?: string;
  cancelOn?: CancelOnModel[];
  timeouts?: { start?: Duration; finish?: Duration };
  singleton?: { mode: SingletonMode; key?: string };
}

// The app around the function, and whether the function class implements IFailureHandler.
export interface AppModel {
  appId: string;
  appVersion?: string;
  // The URL Inngest reaches the app at, such as https://shop.example.com/api/inngest.
  serveUrl: string;
  failureHandler: boolean;
}

export function isSet(value: string | undefined): value is string {
  return value !== undefined && value.trim() !== '';
}
