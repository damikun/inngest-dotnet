// What each call of the replay scenario shows, in order. The tests check that there is one entry per captured call.
export const narrative: { title: string; text: string }[] = [
  {
    title: 'The first call',
    text: 'Inngest calls the function for the first time, with no memoized steps. The function runs until reserve-stock. That is the only new step and a run step, so the SDK runs it at once and answers 206 with a StepRun op that carries the result.',
  },
  {
    title: 'Replaying a memoized step',
    text: 'Inngest saved the reservation and calls again. The SDK runs the function from the top, and reserve-stock returns its saved result without calling the inventory service. The function reaches charge-card, which runs the same way.',
  },
  {
    title: 'A sleep ends the call',
    text: 'With two results memoized, the function reaches cooling-off. Inngest carries out sleeps, so the SDK answers with a Sleep op and the call ends. Nothing in the app waits for the 30 minutes.',
  },
  {
    title: 'Two steps at once',
    text: 'Thirty minutes later Inngest memoizes the sleep as null and calls again. The function starts send-receipt and ship-order together and awaits Task.WhenAll. There are two new steps, so the SDK runs neither and answers with two StepPlanned ops.',
  },
  {
    title: 'One planned step per call',
    text: 'Inngest runs each planned step in a call of its own, and makes both calls at the same time. This one has stepId set to the hash of send-receipt. The SDK replays up to the parallel section, runs only that step and answers with its result.',
  },
  {
    title: 'The other planned step',
    text: 'The same for ship-order, in a parallel call that carries the same memoized steps. disable_immediate_execution is true while the parallel steps run.',
  },
  {
    title: 'The function returns',
    text: 'Once both parallel steps are memoized, Inngest calls once more. Every step returns its saved result, the function returns OrderFulfilled, and the SDK answers 200 with the output. The run is complete.',
  },
];
