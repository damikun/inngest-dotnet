using Inngest.IntegrationTests.Evaluation.Harness;
using Inngest.IntegrationTests.Harness;

// Docker Desktop carries every container's call to the app through one host gateway. One collection per logical CPU
// opens more of those calls than the gateway completes, a dropped handshake still spends an attempt, and the run
// fails. Four collections at a time stay inside what that path delivers.
[assembly: Xunit.v3.Parallelization(MaxThreads = 4)]

// One dev server and one app for the whole assembly; each test class still runs in parallel with the others.
[assembly: AssemblyFixture(typeof(DevServerFixture))]

// The evaluation suite's servers and apps, each started when a scenario first needs it.
[assembly: AssemblyFixture(typeof(EvaluationEnvironment))]
