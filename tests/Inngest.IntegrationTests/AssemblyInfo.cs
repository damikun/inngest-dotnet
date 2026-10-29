using Inngest.IntegrationTests.Evaluation.Harness;
using Inngest.IntegrationTests.Harness;

// One dev server and one app for the whole assembly; each test class still runs in parallel with the others.
[assembly: AssemblyFixture(typeof(DevServerFixture))]

// The evaluation suite's servers and apps, each started when a scenario first needs it.
[assembly: AssemblyFixture(typeof(EvaluationEnvironment))]
