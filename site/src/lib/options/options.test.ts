import { describe, expect, it } from 'vitest';
import { attributesCode, className, csString, functionAttributes, functionOptionsCode, registrationCode } from './csharp.ts';
import type { AppModel, ConcurrencyModel, FunctionOptionsModel } from './model.ts';
import { presets } from './presets.ts';
import { registrationBody, registrationFunctions, serveUrls } from './registration.ts';
import { formatSeconds } from './time.ts';
import { validate } from './validate.ts';

const app: AppModel = { appId: 'my-app', serveUrl: 'https://example.com/api/inngest', failureHandler: false };

describe('formatSeconds', () => {
  // The cases of tests/Inngest.Tests/Internal/TimeStringTests.cs, in seconds.
  it.each([
    [0, '0s'],
    [0.999, '1s'],
    [1, '1s'],
    [1.5, '1s'],
    [60, '1m'],
    [5400, '1h30m'],
    [86400, '1d'],
    [694861, '1w1d1h1m1s'],
  ])('formats %s seconds as %s', (seconds, expected) => {
    expect(formatSeconds(seconds)).toBe(expected);
  });

  it('rejects negative durations', () => {
    expect(() => formatSeconds(-1)).toThrow(RangeError);
  });
});

describe('registration JSON', () => {
  // The function of tests/Inngest.Tests/Registration/FullExampleTests.cs, which uses every option, and the JSON
  // that test expects from the SDK.
  const everyOption: FunctionOptionsModel = {
    id: 'sync-user',
    name: 'Sync user',
    triggers: [
      { kind: 'event', event: 'app/user.created', if: "event.data.plan == 'pro'" },
      { kind: 'cron', schedule: '0 9 * * 1-5', timezone: 'Europe/Prague' },
    ],
    retries: 5,
    concurrency: [
      { limit: 10, scope: 'Function' },
      { limit: 1, key: 'event.data.user_id', scope: 'Environment' },
    ],
    throttle: { limit: 5, period: { value: 1, unit: 'm' }, burst: 2, key: 'event.data.user_id' },
    rateLimit: { limit: 1, period: { value: 4, unit: 'h' }, key: 'event.data.user_id' },
    idempotency: 'event.data.user_id',
    debounce: { period: { value: 5, unit: 's' }, key: 'event.data.user_id', timeout: { value: 1, unit: 'm' } },
    priority: { run: "event.data.plan == 'pro' ? 120 : 0" },
    timeouts: { start: { value: 10, unit: 'm' }, finish: { value: 1, unit: 'h' } },
    singleton: { mode: 'Skip', key: 'event.data.user_id' },
    cancelOn: [
      { event: 'app/user.deleted', match: 'data.user_id', timeout: { value: 1, unit: 'h' } },
      { event: 'app/user.paused', if: 'async.data.id == event.data.id', timeout: { value: 1, unit: 'm' } },
    ],
  };

  it('matches the SDK for a function with every option and a failure handler', () => {
    const body = registrationBody(everyOption, { ...app, failureHandler: true }, '1.2.3');
    expect(JSON.stringify(body)).toBe(
      JSON.stringify({
        url: 'https://example.com/api/inngest',
        deployType: 'ping',
        appName: 'my-app',
        sdk: 'dotnet:v1.2.3',
        v: '0.1',
        framework: 'aspnetcore',
        capabilities: { trust_probe: 'v1' },
        functions: [
          {
            id: 'my-app-sync-user',
            name: 'Sync user',
            triggers: [
              { event: 'app/user.created', expression: "event.data.plan == 'pro'" },
              { cron: 'TZ=Europe/Prague 0 9 * * 1-5' },
            ],
            steps: {
              step: {
                id: 'step',
                name: 'step',
                runtime: { type: 'http', url: 'https://example.com/api/inngest?fnId=my-app-sync-user&stepId=step' },
                retries: { attempts: 5 },
              },
            },
            concurrency: [
              { limit: 10, scope: 'fn' },
              { limit: 1, key: 'event.data.user_id', scope: 'env' },
            ],
            throttle: { limit: 5, period: '1m', burst: 2, key: 'event.data.user_id' },
            rateLimit: { limit: 1, period: '4h', key: 'event.data.user_id' },
            idempotency: 'event.data.user_id',
            debounce: { period: '5s', key: 'event.data.user_id', timeout: '1m' },
            priority: { run: "event.data.plan == 'pro' ? 120 : 0" },
            cancel: [
              { event: 'app/user.deleted', timeout: '1h', if: 'event.data.user_id == async.data.user_id' },
              { event: 'app/user.paused', timeout: '1m', if: 'async.data.id == event.data.id' },
            ],
            timeouts: { start: '10m', finish: '1h' },
            singleton: { mode: 'skip', key: 'event.data.user_id' },
          },
          {
            id: 'my-app-sync-user-failure',
            name: 'Sync user (failure)',
            triggers: [{ event: 'inngest/function.failed', expression: "event.data.function_id == 'my-app-sync-user'" }],
            steps: {
              step: {
                id: 'step',
                name: 'step',
                runtime: { type: 'http', url: 'https://example.com/api/inngest?fnId=my-app-sync-user-failure&stepId=step' },
                retries: { attempts: 1 },
              },
            },
          },
        ],
      }),
    );
  });

  it('keeps query parameters other than deployId, as the SDK does', () => {
    const functions = registrationFunctions(
      { id: 'orders', triggers: [{ kind: 'event', event: 'order/created' }], retries: 3 },
      { ...app, serveUrl: 'https://example.com/api/inngest?region=eu&deployId=01HZ' },
    );
    expect(functions[0].steps).toEqual({
      step: {
        id: 'step',
        name: 'step',
        runtime: { type: 'http', url: 'https://example.com/api/inngest?region=eu&fnId=my-app-orders&stepId=step' },
        retries: { attempts: 3 },
      },
    });
    expect(serveUrls('https://example.com/api/inngest?region=eu&deployId=01HZ').app).toBe('https://example.com/api/inngest?region=eu');
  });

  it('leaves out options that are not set, and names a function after its id', () => {
    const [fn] = registrationFunctions({ id: 'hello', triggers: [] }, app);
    expect(Object.keys(fn)).toEqual(['id', 'name', 'triggers', 'steps']);
    expect(fn.name).toBe('hello');
    expect(fn.triggers).toEqual([]);
  });

  it('keeps a TZ prefix the schedule already has', () => {
    const [fn] = registrationFunctions(
      { id: 'report', triggers: [{ kind: 'cron', schedule: 'TZ=UTC 0 0 * * *', timezone: 'Europe/Prague' }] },
      app,
    );
    expect(fn.triggers).toEqual([{ cron: 'TZ=UTC 0 0 * * *' }]);
  });

  // The cases of tests/Inngest.Tests/Registration/FunctionConfigTests.cs with a limit of 0, which means no limit.
  it('leaves out a concurrency limit of 0', () => {
    const concurrency = (limits: ConcurrencyModel[]) =>
      registrationFunctions({ id: 'orders', triggers: [], concurrency: limits }, app)[0].concurrency;
    expect(concurrency([{ limit: 0, scope: 'Function' }, { limit: 10, key: 'event.data.accountId', scope: 'Function' }])).toEqual([
      { limit: 10, key: 'event.data.accountId', scope: 'fn' },
    ]);
    expect(concurrency([{ limit: 0, scope: 'Function' }])).toBeUndefined();
  });

  it('escapes the id in the failure trigger', () => {
    const functions = registrationFunctions({ id: "it's", triggers: [] }, { ...app, failureHandler: true });
    expect(functions[1].triggers).toEqual([
      { event: 'inngest/function.failed', expression: "event.data.function_id == 'my-app-it\\'s'" },
    ]);
  });
});

describe('C# code', () => {
  it('writes every option as the docs do', () => {
    expect(
      functionOptionsCode(
        {
          id: 'sync-account',
          triggers: [{ kind: 'event', event: 'account/updated' }],
          concurrency: [{ limit: 5, key: 'event.data.accountId', scope: 'Function' }],
          throttle: { limit: 10, period: { value: 1, unit: 'm' } },
          cancelOn: [{ event: 'account/deleted', match: 'data.accountId' }],
          singleton: { mode: 'Skip', key: 'event.data.accountId' },
        },
        '',
      ),
    ).toBe(
      [
        'new FunctionOptions',
        '{',
        '    Id = "sync-account",',
        '    Triggers = [Trigger.Event("account/updated")],',
        '    Concurrency = [new ConcurrencyLimit(5) { Key = "event.data.accountId" }],',
        '    Throttle = new Throttle(10, TimeSpan.FromMinutes(1)),',
        '    CancelOn = [new CancelOn("account/deleted") { Match = "data.accountId" }],',
        '    Singleton = new Singleton(SingletonMode.Skip) { Key = "event.data.accountId" },',
        '}',
      ].join('\n'),
    );
  });

  it('lists several triggers on their own lines and names arguments', () => {
    const code = functionOptionsCode(
      {
        id: 'report',
        triggers: [
          { kind: 'event', event: 'report/requested', if: "event.data.kind == 'daily'" },
          { kind: 'cron', schedule: '0 6 * * *', timezone: 'Europe/Prague' },
        ],
      },
      '',
    );
    expect(code).toContain('    Triggers =\n    [\n');
    expect(code).toContain(`        Trigger.Event("report/requested", @if: "event.data.kind == 'daily'"),\n`);
    expect(code).toContain('        Trigger.Cron("0 6 * * *", timezone: "Europe/Prague"),\n');
  });

  it('escapes strings', () => {
    expect(csString('a "b" \\ c\nd')).toBe('"a \\"b\\" \\\\ c\\nd"');
  });

  it('derives a class name from the id', () => {
    expect(className('sync-account')).toBe('SyncAccount');
    expect(className('2fa.reset')).toBe('Function2faReset');
    expect(className('--')).toBe('MyFunction');
  });

  it('wraps the options in a registration and mentions the failure function', () => {
    const code = registrationCode({ id: 'charge-card', triggers: [] }, { ...app, appVersion: ' 1.4.0 ', failureHandler: true });
    expect(code).toContain('// ChargeCard implements IFailureHandler, so the SDK also registers "charge-card-failure".');
    expect(code).toContain('options.AppVersion = "1.4.0";');
    expect(code).toContain('.AddFunction<ChargeCard>(functionOptions);');
    expect(code.startsWith('// ChargeCard implements IFailureHandler')).toBe(true);
    expect(code).toContain('var functionOptions = new FunctionOptions\n{\n    Id = "charge-card",');
  });
});

describe('C# attributes', () => {
  it('writes every option as the attribute that sets it, with durations as time strings', () => {
    expect(
      functionAttributes({
        id: 'sync-user',
        name: 'Sync user',
        triggers: [
          { kind: 'event', event: 'app/user.created', if: "event.data.plan == 'pro'" },
          { kind: 'cron', schedule: '0 9 * * 1-5', timezone: 'Europe/Prague' },
        ],
        retries: 5,
        concurrency: [
          { limit: 10, scope: 'Function' },
          { limit: 1, key: 'event.data.user_id', scope: 'Environment' },
        ],
        throttle: { limit: 5, period: { value: 90, unit: 'm' }, burst: 2, key: 'event.data.user_id' },
        rateLimit: { limit: 1, period: { value: 4, unit: 'h' }, key: 'event.data.user_id' },
        idempotency: 'event.data.user_id',
        debounce: { period: { value: 5, unit: 's' }, key: 'event.data.user_id', timeout: { value: 1, unit: 'm' } },
        priority: { run: "event.data.plan == 'pro' ? 120 : 0" },
        timeouts: { start: { value: 10, unit: 'm' }, finish: { value: 1, unit: 'h' } },
        singleton: { mode: 'Skip', key: 'event.data.user_id' },
        cancelOn: [
          { event: 'app/user.deleted', match: 'data.user_id', timeout: { value: 1, unit: 'h' } },
          { event: 'app/user.paused', if: 'async.data.id == event.data.id' },
        ],
      }),
    ).toEqual([
      '[InngestFunction("sync-user", Name = "Sync user", Retries = 5, Idempotency = "event.data.user_id")]',
      `[EventTrigger("app/user.created", If = "event.data.plan == 'pro'")]`,
      '[CronTrigger("0 9 * * 1-5", Timezone = "Europe/Prague")]',
      '[Concurrency(10)]',
      '[Concurrency(1, Key = "event.data.user_id", Scope = ConcurrencyScope.Environment)]',
      '[Throttle(5, "1h30m", Burst = 2, Key = "event.data.user_id")]',
      '[RateLimit(1, "4h", Key = "event.data.user_id")]',
      '[Debounce("5s", Key = "event.data.user_id", Timeout = "1m")]',
      `[Priority("event.data.plan == 'pro' ? 120 : 0")]`,
      '[CancelOn("app/user.deleted", Match = "data.user_id", Timeout = "1h")]',
      '[CancelOn("app/user.paused", If = "async.data.id == event.data.id")]',
      '[Timeouts(Start = "10m", Finish = "1h")]',
      '[Singleton(SingletonMode.Skip, Key = "event.data.user_id")]',
    ]);
  });

  it('writes batching with its key and condition', () => {
    expect(
      functionAttributes({
        id: 'import-rows',
        triggers: [],
        batchEvents: { maxSize: 100, timeout: { value: 10, unit: 's' }, key: 'event.data.importId', if: 'event.data.valid' },
      }),
    ).toEqual(['[InngestFunction("import-rows")]', '[BatchEvents(100, "10s", Key = "event.data.importId", If = "event.data.valid")]']);
  });

  it('sets Retries and Burst when they are 0, and leaves out timeouts that are not set', () => {
    expect(
      functionAttributes({ id: 'orders', triggers: [], retries: 0, throttle: { limit: 1, period: { value: 1, unit: 's' }, burst: 0 }, timeouts: {} }),
    ).toEqual(['[InngestFunction("orders", Retries = 0)]', '[Throttle(1, "1s", Burst = 0)]']);
  });

  it('escapes strings', () => {
    expect(functionAttributes({ id: 'greet', name: 'Say "hi"', triggers: [] })).toEqual(['[InngestFunction("greet", Name = "Say \\"hi\\"")]']);
  });

  it('puts the attributes on the class and registers it without options', () => {
    const code = attributesCode(
      { id: 'charge-card', triggers: [{ kind: 'event', event: 'shop/order.placed' }] },
      { ...app, appVersion: ' 1.4.0 ', failureHandler: true },
    );
    expect(code).toBe(
      [
        '// ChargeCard implements IFailureHandler, so the SDK also registers "charge-card-failure".',
        '[InngestFunction("charge-card")]',
        '[EventTrigger("shop/order.placed")]',
        'public sealed class ChargeCard : InngestFunction<JsonElement>, IFailureHandler',
        '{',
        '    // ...',
        '}',
        '',
        'builder.Services',
        '    .AddInngest(options =>',
        '    {',
        '        options.AppId = "my-app";',
        '        options.AppVersion = "1.4.0";',
        '    })',
        '    .AddFunction<ChargeCard>();',
      ].join('\n'),
    );
  });
});

describe('validation', () => {
  const base: FunctionOptionsModel = { id: 'orders', triggers: [{ kind: 'event', event: 'order/created' }] };

  it('accepts valid options', () => {
    expect(validate(base)).toEqual([]);
  });

  it('accepts every preset', () => {
    for (const preset of presets) expect(validate(preset.options), preset.id).toEqual([]);
  });

  it('uses the messages of FunctionOptionsValidator', () => {
    expect(validate({ ...base, retries: 21 })).toEqual(['Function "orders": Retries must be between 0 and 20, but was 21.']);
    expect(validate({ ...base, id: ' ' })).toEqual([
      'A function has no Id. Set FunctionOptions.Id to an id that is unique in the app and never changes.',
    ]);
    expect(validate({ ...base, rateLimit: { limit: 1, period: { value: 2, unit: 'd' } } })).toEqual([
      'Function "orders": RateLimit.Period must be between 1s and 24h, but was 2d.',
    ]);
    expect(validate({ ...base, concurrency: [{ limit: 1, scope: 'Account' }] })).toEqual([
      'Function "orders": A concurrency limit with the Account scope needs a Key. Only the Function scope can go without one.',
    ]);
  });

  it('rejects batching combined with options Inngest refuses alongside it', () => {
    expect(
      validate({
        ...base,
        batchEvents: { maxSize: 100, timeout: { value: 30, unit: 's' } },
        debounce: { period: { value: 5, unit: 's' } },
        priority: { run: 'event.data.vip ? 120 : 0' },
      }),
    ).toEqual([
      'Function "orders": BatchEvents cannot be combined with Debounce.',
      'Function "orders": BatchEvents cannot be combined with Priority.',
    ]);
  });

  it('describes sub-second durations as given', () => {
    expect(validate({ ...base, throttle: { limit: 1, period: { value: 0, unit: 's' } } })).toEqual([
      'Function "orders": Throttle.Period must be at least 1s, but was 0s.',
    ]);
  });

  // Inngest refuses the whole sync for these.
  it('rejects a trigger given twice', () => {
    const total = 'event.data.total > 100';
    expect(
      validate({
        ...base,
        triggers: [
          { kind: 'event', event: 'order/created', if: total },
          { kind: 'event', event: 'order/created', if: total },
        ],
      }),
    ).toEqual(['Function "orders": The event trigger "order/created" is given twice.']);
    // A blank condition is the same as none, and every repeat is a problem of its own.
    const blank = (condition: string) => ({ kind: 'event', event: 'order/created', if: condition }) as const;
    expect(validate({ ...base, triggers: [...base.triggers, blank(''), blank(' ')] })).toEqual([
      'Function "orders": The event trigger "order/created" is given twice.',
      'Function "orders": The event trigger "order/created" is given twice.',
    ]);
    // The time zone becomes part of the schedule.
    expect(
      validate({
        ...base,
        triggers: [
          { kind: 'cron', schedule: '0 9 * * *', timezone: 'Europe/Prague' },
          { kind: 'cron', schedule: 'TZ=Europe/Prague 0 9 * * *' },
        ],
      }),
    ).toEqual([
      'Function "orders": The cron schedule "TZ=Europe/Prague 0 9 * * *" is given twice, which Inngest refuses even with different jitter.',
    ]);
  });

  it('accepts one event under different conditions and one schedule in two time zones', () => {
    expect(validate({ ...base, triggers: [...base.triggers, { kind: 'event', event: 'order/created', if: 'event.data.total > 100' }] })).toEqual([]);
    expect(
      validate({
        ...base,
        triggers: [
          { kind: 'cron', schedule: '0 9 * * *', timezone: 'Europe/Prague' },
          { kind: 'cron', schedule: '0 9 * * *', timezone: 'America/New_York' },
        ],
      }),
    ).toEqual([]);
  });

  it('accepts a concurrency limit of 0, which means no limit, and rejects a negative one', () => {
    expect(validate({ ...base, concurrency: [{ limit: 0, scope: 'Function' }] })).toEqual([]);
    expect(validate({ ...base, concurrency: [{ limit: -3, scope: 'Function' }] })).toEqual([
      'Function "orders": A concurrency limit cannot be negative, but one was -3. Use 0 for no limit.',
    ]);
  });

  it('leaves the upper batch timeout to the plan', () => {
    expect(validate({ ...base, batchEvents: { maxSize: 10, timeout: { value: 5, unit: 'm' } } })).toEqual([]);
    expect(validate({ ...base, batchEvents: { maxSize: 10, timeout: { value: 0, unit: 's' } } })).toEqual([
      'Function "orders": BatchEvents.Timeout must be at least 1s, but was 0s.',
    ]);
  });

  it('reports only the missing event of a CancelOn entry without one', () => {
    expect(validate({ ...base, cancelOn: [{ event: ' ', timeout: { value: 0, unit: 's' } }] })).toEqual([
      'Function "orders": A CancelOn entry needs an event name.',
    ]);
  });

  // Lists_every_problem_of_a_function_at_once and Checks_each_concurrency_limit_even_when_there_are_too_many in
  // tests/Inngest.Tests/Functions/FunctionOptionsValidatorTests.cs.
  it('lists every problem in the order the SDK finds them', () => {
    expect(
      validate({
        ...base,
        retries: 21,
        throttle: { limit: 0, period: { value: 1, unit: 'm' }, burst: -1 },
        debounce: { period: { value: 5, unit: 's' } },
        batchEvents: { maxSize: 10, timeout: { value: 5, unit: 's' } },
      }),
    ).toEqual([
      'Function "orders": Retries must be between 0 and 20, but was 21.',
      'Function "orders": Throttle.Limit must be greater than 0, but was 0.',
      'Function "orders": Throttle.Burst must not be negative, but was -1.',
      'Function "orders": BatchEvents cannot be combined with Debounce.',
    ]);
    expect(
      validate({
        ...base,
        concurrency: [
          { limit: 1, scope: 'Function' },
          { limit: -1, scope: 'Function' },
          { limit: 5, scope: 'Account' },
        ],
      }),
    ).toEqual([
      'Function "orders": Concurrency allows at most 2 limits, but 3 were given.',
      'Function "orders": A concurrency limit cannot be negative, but one was -1. Use 0 for no limit.',
      'Function "orders": A concurrency limit with the Account scope needs a Key. Only the Function scope can go without one.',
    ]);
  });

  it('reports only the missing id until the function has one', () => {
    expect(validate({ ...base, id: '', retries: 21, priority: { run: '' } })).toEqual([
      'A function has no Id. Set FunctionOptions.Id to an id that is unique in the app and never changes.',
    ]);
  });
});
