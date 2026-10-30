import type { AppModel, FunctionOptionsModel } from './model.ts';

export interface Preset {
  id: string;
  label: string;
  summary: string;
  app: AppModel;
  options: FunctionOptionsModel;
}

const app: AppModel = { appId: 'shop', serveUrl: 'https://shop.example.com/api/inngest', failureHandler: false };

// Starting points for the function options builder. Every preset is valid; the tests check that, and the snippet
// check compiles their C#, as attributes and in code, and compares their JSON with what the SDK registers for each.
export const presets: Preset[] = [
  {
    id: 'per-customer',
    label: 'Per-customer queue',
    summary: 'At most one run per account at a time, enterprise accounts first, cancelled when the account goes away.',
    app,
    options: {
      id: 'sync-account',
      name: 'Sync account',
      triggers: [{ kind: 'event', event: 'account/updated' }],
      retries: 5,
      concurrency: [
        { limit: 20, scope: 'Function' },
        { limit: 1, key: 'event.data.accountId', scope: 'Function' },
      ],
      priority: { run: "event.data.plan == 'enterprise' ? 120 : 0" },
      cancelOn: [{ event: 'account/deleted', match: 'data.accountId', timeout: { value: 1, unit: 'd' } }],
    },
  },
  {
    id: 'webhook',
    label: 'Webhook consumer',
    summary: 'One run per delivery, at most ten runs a minute per merchant, so the downstream API is not flooded.',
    app: { ...app, failureHandler: true },
    options: {
      id: 'process-payment-webhook',
      triggers: [{ kind: 'event', event: 'payments/webhook.received', if: "event.data.type != 'ping'" }],
      retries: 8,
      idempotency: 'event.data.deliveryId',
      throttle: { limit: 10, period: { value: 1, unit: 'm' }, burst: 5, key: 'event.data.merchantId' },
      timeouts: { start: { value: 30, unit: 'm' } },
    },
  },
  {
    id: 'nightly',
    label: 'Nightly job',
    summary: 'Runs at 02:00 in Prague, never overlaps with itself and is cancelled if it is still going after two hours.',
    app,
    options: {
      id: 'close-billing-day',
      name: 'Close the billing day',
      triggers: [{ kind: 'cron', schedule: '0 2 * * *', timezone: 'Europe/Prague' }],
      retries: 3,
      singleton: { mode: 'Skip' },
      timeouts: { finish: { value: 2, unit: 'h' } },
    },
  },
  {
    id: 'batch',
    label: 'Batched import',
    summary: 'Takes rows 100 at a time per import, or whatever arrived after 10 seconds, with two batches in flight.',
    app,
    options: {
      id: 'import-rows',
      triggers: [{ kind: 'event', event: 'import/row.parsed' }],
      batchEvents: { maxSize: 100, timeout: { value: 10, unit: 's' }, key: 'event.data.importId' },
      concurrency: [{ limit: 2, scope: 'Function' }],
    },
  },
  {
    id: 'debounce',
    label: 'Debounced reindex',
    summary: 'Reindexes a product once its edits stop for 30 seconds, and at least every 5 minutes during a long edit.',
    app,
    options: {
      id: 'reindex-product',
      triggers: [{ kind: 'event', event: 'catalog/product.updated' }],
      debounce: { period: { value: 30, unit: 's' }, key: 'event.data.productId', timeout: { value: 5, unit: 'm' } },
      rateLimit: { limit: 1000, period: { value: 1, unit: 'h' } },
    },
  },
];
