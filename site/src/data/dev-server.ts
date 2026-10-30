// The ways to start the Inngest dev server. The DevServer component shows them as tabs and the Markdown copies of
// the pages write them out as sections, from this one list. The order follows Inngest's local development docs
// (install script, Homebrew, npx, Docker); Compose is added for teams. Every command is one line, so it pastes into
// any shell; long lines wrap on screen.

const IMAGE = 'inngest/inngest:v1.49.0';

export interface DevServerBlock {
  lang: string;
  code: string;
  title?: string;
  wrap?: boolean;
}

export interface DevServerTab {
  label: string;
  blocks: DevServerBlock[];
  // A note under the blocks, as HTML.
  note?: string;
}

// For a Connect worker (`connect`): the dev server stops looking for HTTP apps, and the worker dials in on port 8289.
export function devServerTabs(connect: boolean): DevServerTab[] {
  const app = (host: string) => (connect ? '--no-discovery' : `-u http://${host}:5280/api/inngest`);
  const compose = [
    'services:',
    '  inngest:',
    `    image: ${IMAGE}`,
    `    command: inngest dev ${app('host.docker.internal')}`,
    '    ports:',
    '      - "8288:8288"',
    '      - "8289:8289"',
    ...(connect ? [] : ['    extra_hosts:', '      - "host.docker.internal:host-gateway"']),
  ].join('\n');

  return [
    {
      label: 'Install script',
      blocks: [{ lang: 'bash', wrap: true, code: `curl -sSfL https://cli.inngest.com/install.sh | sh\ninngest dev ${app('localhost')}` }],
    },
    {
      label: 'Homebrew',
      blocks: [{ lang: 'bash', wrap: true, code: `brew install inngest/tap/inngest\ninngest dev ${app('localhost')}` }],
    },
    {
      label: 'npx',
      blocks: [{ lang: 'bash', wrap: true, code: `npx --ignore-scripts=false inngest-cli@latest dev ${app('localhost')}` }],
    },
    {
      label: 'Docker',
      blocks: [
        {
          lang: 'bash',
          wrap: true,
          code: `docker run --rm -p 8288:8288 -p 8289:8289 ${IMAGE} inngest dev ${app('host.docker.internal')}`,
        },
      ],
      note: connect
        ? undefined
        : 'The container reaches the app at <code>host.docker.internal</code>, since <code>localhost</code> inside it is the container itself. On Linux without Docker Desktop, add <code>--add-host=host.docker.internal:host-gateway</code>.',
    },
    {
      label: 'Compose',
      blocks: [
        { lang: 'yaml', title: 'compose.yaml', code: compose },
        { lang: 'bash', code: 'docker compose up' },
      ],
    },
  ];
}
