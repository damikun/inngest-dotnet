// The landing page's questions and answers. The page renders them and the route middleware turns the same entries
// into FAQPage structured data, so the two never disagree. Answers are HTML with site-relative links.
export interface Faq {
  question: string;
  answer: string;
}

const base = '/inngest-dotnet/';

export const faq: Faq[] = [
  {
    question: 'Is this an official Inngest SDK?',
    answer:
      'No. inngest-dotnet is a community port of the official TypeScript SDK, inngest-js 4.26.2, and is not affiliated with Inngest Inc. It speaks the same protocol, and its end-to-end tests run against the Inngest server in dev and cloud mode.',
  },
  {
    question: 'What do I need to run it?',
    answer: `.NET 8 or .NET 10 and an Inngest server: Inngest Cloud, a <a href="https://www.inngest.com/docs/platform-and-operations/self-host-inngest">self-hosted Inngest</a>, or the dev server on your machine. Functions are served from an ASP.NET Core endpoint, from Azure Functions or AWS Lambda, or from a <a href="${base}guides/connect/">Connect worker</a>.`,
  },
  {
    question: 'Does Inngest have to reach my app over the internet?',
    answer: `With <code>MapInngest</code>, Inngest calls a public HTTPS endpoint in your app. With <a href="${base}guides/connect/">Connect</a>, the app opens a WebSocket to Inngest instead and needs no inbound port.`,
  },
  {
    question: 'Is it ready for production?',
    answer: `The serve protocol, steps, flow control, signing, middleware and Connect are implemented and verified: 3,716 unit tests, 351 end-to-end tests against the Inngest server in Docker, and load and memory checks in dev and cloud mode. The <a href="${base}internals/readiness/">readiness report</a> sums them up. The packages are on NuGet as 1.0.0. Durable endpoints are the one feature still marked experimental.`,
  },
  {
    question: 'Does it work with Native AOT?',
    answer: `Yes. The SDK uses source-generated JSON for its own types; add a <code>JsonSerializerContext</code> for yours, as described in <a href="${base}guides/native-aot/">Native AOT and trimming</a>.`,
  },
];

export function plainText(html: string): string {
  return html
    .replace(/<[^>]+>/g, '')
    .replace(/&amp;/g, '&')
    .replace(/\s+/g, ' ')
    .trim();
}
