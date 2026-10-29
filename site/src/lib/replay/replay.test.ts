import { describe, expect, it } from 'vitest';
import data from '../../data/replay.json' with { type: 'json' };
import { codeLines, type ReplayData, opsOf, stepIdOf, stepStates } from './model.ts';
import { narrative } from './narrative.ts';

const replay = data as unknown as ReplayData;

describe('replay scenario', () => {
  it('has a narrative for every captured call', () => {
    expect(narrative).toHaveLength(replay.calls.length);
  });

  it('answers 206 until the last call, which answers 200', () => {
    expect(replay.calls.map((call) => call.response.status)).toEqual([206, 206, 206, 206, 206, 206, 200]);
  });

  it('runs, sleeps, plans the parallel steps, runs each in its own call, then returns', () => {
    expect(replay.calls.map((call) => opsOf(call).map((op) => `${op.op} ${op.displayName}`))).toEqual([
      ['StepRun reserve-stock'],
      ['StepRun charge-card'],
      ['Sleep cooling-off'],
      ['StepPlanned send-receipt', 'StepPlanned ship-order'],
      ['StepRun send-receipt'],
      ['StepRun ship-order'],
      [],
    ]);
    expect(replay.calls.map(stepIdOf).map((id) => (id === 'step' ? 'step' : replay.stepNames[id]))).toEqual([
      'step',
      'step',
      'step',
      'step',
      'send-receipt',
      'ship-order',
      'step',
    ]);
  });

  it('derives what happened to each step in a call', () => {
    expect(Object.fromEntries(stepStates(replay, 3))).toEqual({
      'reserve-stock': 'memoized',
      'charge-card': 'memoized',
      'cooling-off': 'memoized',
      'send-receipt': 'planned',
      'ship-order': 'planned',
    });
    expect(stepStates(replay, 2).get('cooling-off')).toBe('reported');
    expect(stepStates(replay, 0).get('charge-card')).toBe('untouched');
  });

  it('maps every step to the lines of its call in the source', () => {
    const lines = codeLines(replay);
    const byStep = (step: string) => lines.filter((line) => line.step === step).map((line) => line.text.trim());
    expect(byStep('reserve-stock')).toEqual([
      'var reservation = await context.Step.RunAsync("reserve-stock",',
      'ct => inventory.ReserveAsync(order.OrderId, ct));',
    ]);
    expect(byStep('send-receipt')).toEqual([
      'context.Step.RunAsync("send-receipt",',
      'ct => mailer.SendReceiptAsync(order.OrderId, payment.PaymentId, ct)),',
    ]);
    expect(byStep('ship-order')).toHaveLength(2);
    expect(lines.filter((line) => line.isReturn)).toHaveLength(1);
  });
});
