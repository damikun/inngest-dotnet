// Compiles every C# code block of the docs against the SDK in this repository, checks the JSON the function
// options builder shows against what the SDK registers for both forms of its C#, attributes and code first, and
// checks the replay visualizer's data against what the SDK answers.
//
//   node scripts/check-snippets.ts [--out <dir>] [--write-replay]
//
// The harness is generated outside the repository (by default in the system's temp folder) and needs the .NET SDK.
// A code block is compiled as a file of types, or as a program when it has top-level statements. An MDX comment
// right before a block changes that:
//
//   {/* check: body OrderPlaced; IEmailSender email */}  statements of a function body with `context` of
//                                                        FunctionContext<OrderPlaced>, `cancellationToken` and the
//                                                        listed parameters
//   {/* check: statements; WebApplicationBuilder builder */}  statements with the listed parameters
//   {/* check: expression FunctionOptions */}            an expression of that type
//   {/* check: members FunctionOptions */}               object initializer members of that type
//   {/* check: attributes */}                            attributes of a function class, put on a class of their own
//   {/* check: file isolated */}                         types in a namespace of their own
//   {/* check: skip */}                                  not compiled
//
// Blocks of one page share a namespace, so a later block can use the types of an earlier one.

import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { attributeRegistrationCode, className, functionClassCode, registrationCode } from '../src/lib/options/csharp.ts';
import type { AppModel, FunctionOptionsModel } from '../src/lib/options/model.ts';
import { presets } from '../src/lib/options/presets.ts';
import { registrationBody } from '../src/lib/options/registration.ts';

const site = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repo = resolve(site, '..');
const harness = join(site, 'scripts', 'harness');
const replayData = join(site, 'src', 'data', 'replay.json');

const argv = process.argv.slice(2);
const option = (name: string) => {
  const index = argv.indexOf(name);
  return index >= 0 ? argv[index + 1] : undefined;
};
const out = resolve(option('--out') ?? join(tmpdir(), 'inngest-dotnet-snippets'));
const writeReplay = argv.includes('--write-replay');

if (out === repo || out.startsWith(repo + sep)) {
  console.error(`The harness must live outside the repository, not in ${out}.`);
  process.exit(2);
}

type Kind = 'auto' | 'file' | 'program' | 'body' | 'statements' | 'expression' | 'members' | 'attributes' | 'skip';

interface Directive {
  kind: Kind;
  type?: string;
  params: string[];
  isolated: boolean;
}

interface Line {
  number: number;
  text: string;
}

interface Snippet {
  file: string;
  page: string;
  index: number;
  lines: Line[];
  directive: Directive;
}

// ---------------------------------------------------------------------------------------------------- extraction

function walk(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) return entry.name === 'internals' ? [] : walk(path);
    return /\.mdx?$/.test(entry.name) ? [path] : [];
  });
}

function parseDirective(text: string): Directive {
  const [head = '', ...params] = text.split(';').map((part) => part.trim());
  const words = head.split(/\s+/).filter(Boolean);
  const kinds: Kind[] = ['file', 'program', 'body', 'statements', 'expression', 'members', 'attributes', 'skip'];
  const kind = kinds.find((k) => k === words[0]?.replace(/[,:]$/, '')) ?? 'auto';
  const rest = words.slice(1).filter((word) => word !== 'isolated');
  return { kind, type: rest[0], params: params.filter(Boolean), isolated: words.includes('isolated') };
}

function extract(file: string, page: string): Snippet[] {
  const lines = readFileSync(file, 'utf8').split('\n');
  const snippets: Snippet[] = [];
  let directive: Directive | undefined;
  for (let i = 0; i < lines.length; i++) {
    const comment = /^\s*\{\/\*\s*check:\s*(.*?)\s*\*\/\}\s*$/.exec(lines[i]);
    if (comment) {
      directive = parseDirective(comment[1]);
      continue;
    }
    const fence = /^(\s*)(`{3,}|~{3,})\s*([^\s`]*)/.exec(lines[i]);
    if (!fence) {
      if (lines[i].trim() !== '') directive = undefined;
      continue;
    }
    const [, indent, marker, language] = fence;
    const body: Line[] = [];
    let j = i + 1;
    for (; j < lines.length; j++) {
      const close = new RegExp(`^\\s*${marker[0] === '`' ? '`' : '~'}{${marker.length},}\\s*$`);
      if (close.test(lines[j])) break;
      body.push({ number: j + 1, text: lines[j].startsWith(indent) ? lines[j].slice(indent.length) : lines[j].trimStart() });
    }
    if (/^(csharp|cs|c#)$/i.test(language)) {
      snippets.push({ file, page, index: snippets.length + 1, lines: body, directive: directive ?? parseDirective('auto') });
    }
    directive = undefined;
    i = j;
  }
  return snippets;
}

// ---------------------------------------------------------------------------------------------------- generation

const USING = /^(global\s+)?using\s+(static\s+)?[\w.]+(\s*=\s*[\w.<>, ]+)?;\s*$/;
const TYPE = /^(\[.*\]\s*)?((public|internal|file|private|protected|sealed|static|abstract|partial|readonly|unsafe)\s+)*(class|record|struct|interface|enum)\s+\w/;

function splitUsings(lines: Line[]): { usings: string[]; rest: Line[] } {
  const usings: string[] = [];
  let start = 0;
  for (; start < lines.length; start++) {
    const text = lines[start].text.trim();
    if (USING.test(text)) usings.push(text);
    else if (text !== '') break;
  }
  return { usings, rest: lines.slice(start) };
}

function hasStatements(lines: Line[]): boolean {
  for (const { text } of lines) {
    const trimmed = text.trim();
    if (trimmed === '' || trimmed.startsWith('//') || trimmed.startsWith('#')) continue;
    return !(TYPE.test(text) || text.startsWith('['));
  }
  return false;
}

// Top-level statements end where the first type declaration at column 0 begins.
function splitProgram(lines: Line[]): { statements: Line[]; types: Line[] } {
  for (let i = 0; i < lines.length; i++) {
    const text = lines[i].text;
    if (TYPE.test(text) || (text.startsWith('[') && TYPE.test(lines[i + 1]?.text ?? ''))) {
      return { statements: lines.slice(0, i), types: lines.slice(i) };
    }
  }
  return { statements: lines, types: [] };
}

function block(lines: Line[], file: string): string {
  if (lines.length === 0) return '';
  return [`#line ${lines[0].number} "${file}"`, ...lines.map((line) => line.text), '#line default'].join('\n');
}

function namespaceOf(page: string): string {
  const parts = (page === 'index' ? 'index' : page)
    .split('/')
    .map((part) => part.split('-').map((word) => word.charAt(0).toUpperCase() + word.slice(1)).join(''));
  return ['Docs', ...parts].join('.');
}

// Fragments are code that sits inside a reader's own file, which is assumed to have `using Inngest;`. Whole files and
// programs must bring their own using directives. Every block is a file of its own, so that one block's usings never
// help another; the blocks of a page share a namespace, so that their types do.
const FRAGMENTS: Kind[] = ['body', 'statements', 'expression', 'members', 'attributes'];

function generateSnippet(snippet: Snippet): string | undefined {
  const { usings: own, rest } = splitUsings(snippet.lines);
  const { directive, index, file } = snippet;
  const kind = directive.kind === 'auto' ? (hasStatements(rest) ? 'program' : 'file') : directive.kind;
  const usings = new Set(own);
  if (FRAGMENTS.includes(kind)) {
    usings.add('using Inngest;');
    usings.add('using System.Text.Json;');
  }
  const params = directive.params.join(', ');
  let code: string;
  switch (kind) {
    case 'skip':
      return undefined;
    case 'file':
      code = block(rest, file);
      break;
    case 'program': {
      const { statements, types } = splitProgram(rest);
      code = [
        `internal static class Program${index}`,
        '{',
        '    internal static async Task Run(string[] args)',
        '    {',
        block(statements, file),
        '    }',
        '}',
        block(types, file),
      ].join('\n');
      break;
    }
    case 'body':
    case 'statements': {
      const context = kind === 'body' ? [`FunctionContext<${directive.type ?? 'System.Text.Json.JsonElement'}> context`, 'CancellationToken cancellationToken'] : [];
      const all = [...context, ...directive.params].join(', ');
      code = [
        `internal static class Snippet${index}`,
        '{',
        `    internal static async Task<object?> Run(${all})`,
        '    {',
        block(rest, file),
        '        return null;',
        '    }',
        '}',
      ].join('\n');
      break;
    }
    case 'expression': {
      const trimmed = rest.map((line, i) => (i === rest.length - 1 ? { ...line, text: line.text.replace(/[;,]\s*$/, '') } : line));
      code = [
        `internal static class Snippet${index}`,
        '{',
        `    internal static object? Run(${params}) =>`,
        `        (${directive.type ?? 'object'})(`,
        block(trimmed, file),
        '        );',
        '}',
      ].join('\n');
      break;
    }
    case 'members': {
      const text = rest.map((line) => line.text).join('\n');
      const defaults =
        directive.type === 'FunctionOptions'
          ? [!/\bId\s*=/.test(text) ? 'Id = "snippet",' : '', !/\bTriggers\s*=/.test(text) ? 'Triggers = [],' : ''].filter(Boolean)
          : [];
      code = [
        `internal static class Snippet${index}`,
        '{',
        `    internal static object Run(${params}) => new ${directive.type}`,
        '    {',
        ...defaults.map((line) => `        ${line}`),
        block(rest, file),
        '    };',
        '}',
      ].join('\n');
      break;
    }
    case 'attributes':
      code = [
        block(rest, file),
        `internal sealed class Snippet${index} : InngestFunction<JsonElement>`,
        '{',
        '    public override Task<object?> ExecuteAsync(FunctionContext<JsonElement> context, CancellationToken cancellationToken) =>',
        '        Task.FromResult<object?>(null);',
        '}',
      ].join('\n');
      break;
    default:
      throw new Error(`Unknown kind ${kind}`);
  }
  const body = directive.isolated ? `namespace Isolated${index}\n{\n${code}\n}` : code;
  return [...usings].sort().join('\n') + `\n\nnamespace ${namespaceOf(snippet.page)}\n{\n${body}\n}\n`;
}

// The function options builder's cases: its presets and a few that use the corners of the format.
function parityCases(): { name: string; app: AppModel; options: FunctionOptionsModel }[] {
  const app: AppModel = { appId: 'shop', serveUrl: 'https://shop.example.com/api/inngest', failureHandler: false };
  return [
    ...presets.map((preset) => ({ name: preset.id, app: preset.app, options: preset.options })),
    {
      name: 'every-option',
      app: { appId: 'my-app', appVersion: '1.4.0', serveUrl: 'https://example.com/api/inngest?region=eu&deployId=01HZ', failureHandler: true },
      options: {
        id: 'sync-user',
        name: 'Sync user',
        triggers: [
          { kind: 'event', event: 'app/user.created', if: "event.data.plan == 'pro'" },
          { kind: 'cron', schedule: '0 9 * * 1-5', timezone: 'Europe/Prague' },
          { kind: 'cron', schedule: 'TZ=UTC 30 6 * * *', timezone: 'Europe/Prague' },
        ],
        retries: 0,
        concurrency: [
          { limit: 10, scope: 'Function' },
          { limit: 1, key: '"global-key"', scope: 'Account' },
        ],
        throttle: { limit: 5, period: { value: 90, unit: 'm' }, burst: 0, key: 'event.data.user_id' },
        rateLimit: { limit: 1, period: { value: 24, unit: 'h' }, key: 'event.data.user_id' },
        idempotency: 'event.data.user_id',
        debounce: { period: { value: 7, unit: 'd' }, key: 'event.data.user_id', timeout: { value: 8, unit: 'd' } },
        priority: { run: "event.data.plan == 'pro' ? 120 : 0" },
        timeouts: { start: { value: 3600, unit: 's' } },
        singleton: { mode: 'Cancel' },
        cancelOn: [
          { event: 'app/user.deleted', match: 'data.user_id', timeout: { value: 1, unit: 'h' } },
          { event: 'app/user.paused', if: 'async.data.id == event.data.id' },
          { event: 'app/user.archived' },
        ],
      },
    },
    { name: 'invoke-only', app, options: { id: 'score-account', triggers: [] } },
    { name: 'quotes-in-id', app: { ...app, failureHandler: true }, options: { id: "o'brien", triggers: [{ kind: 'event', event: 'a/b' }] } },
    {
      name: 'no-concurrency-limit',
      app,
      options: {
        id: 'import-accounts',
        triggers: [
          { kind: 'event', event: 'import/requested' },
          { kind: 'event', event: 'import/requested', if: 'event.data.rows > 1000' },
          { kind: 'cron', schedule: '0 9 * * *', timezone: 'Europe/Prague' },
          { kind: 'cron', schedule: '0 9 * * *', timezone: 'America/New_York' },
        ],
        concurrency: [
          { limit: 0, scope: 'Function' },
          { limit: 5, key: 'event.data.accountId', scope: 'Account' },
        ],
      },
    },
  ];
}

function indent(code: string, by: string): string {
  return code
    .split('\n')
    .map((line) => (line === '' ? '' : `${by}${line}`))
    .join('\n');
}

// Every case registers twice, with the code first C# and with the attributes C# of the builder, and both
// registrations must sync to the JSON the builder shows.
function generateParityCases(): string {
  const cases = parityCases();
  const entries = cases.map((c, i) => {
    const expected = JSON.stringify(registrationBody(c.options, c.app), null, 2);
    const forms = `Case${i}.CodeFirst.Registration.Register, Case${i}.Attributes.Registration.Register`;
    return `        new(${JSON.stringify(c.name)}, ${JSON.stringify(c.app.serveUrl)}, ${forms}, """\n${expected}\n"""),`;
  });
  const namespaces = cases.map((c, i) => {
    const members = [
      '    public override Task<object?> ExecuteAsync(FunctionContext<JsonElement> context, CancellationToken cancellationToken) =>',
      '        Task.FromResult<object?>(null);',
      ...(c.app.failureHandler
        ? ['', '    public Task OnFailureAsync(FunctionContext<FunctionFailure> context, CancellationToken cancellationToken) => Task.CompletedTask;']
        : []),
    ];
    const bases = c.app.failureHandler ? 'InngestFunction<JsonElement>, IFailureHandler' : 'InngestFunction<JsonElement>';
    const plainClass = [`public sealed class ${className(c.options.id)} : ${bases}`, '{', ...members, '}'].join('\n');
    const form = (name: string, type: string, registration: string) =>
      [
        `namespace Parity.Case${i}.${name}`,
        '{',
        indent(type, '    '),
        '',
        '    internal static class Registration',
        '    {',
        '        public static void Register(WebApplicationBuilder builder)',
        '        {',
        indent(registration, '            '),
        '        }',
        '    }',
        '}',
      ].join('\n');
    return [
      form('CodeFirst', plainClass, registrationCode(c.options, c.app)),
      '',
      // The class as the builder shows it, with members where the builder leaves them out.
      form('Attributes', functionClassCode(c.options, c.app, members), attributeRegistrationCode(c.options, c.app)),
    ].join('\n');
  });
  return [
    'using System.Text.Json;',
    'using Inngest;',
    '',
    'namespace Parity',
    '{',
    '    internal static class Cases',
    '    {',
    '        public static readonly Case[] All =',
    '        [',
    ...entries,
    '        ];',
    '    }',
    '}',
    '',
    ...namespaces,
    '',
  ].join('\n');
}

const props = `<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <!-- Warnings the wrappers cause rather than the snippets: async methods without await, the return after a
         snippet's own return, unused local functions and variables of fragments. -->
    <NoWarn>$(NoWarn);CS1998;CS0162;CS8321;CS0219;CS0168;CS1591;xUnit1051</NoWarn>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
</Project>
`;

const reference = (name: string) => `    <ProjectReference Include="${join(repo, 'src', name, `${name}.csproj`)}" />`;
const references = ['Inngest', 'Inngest.AspNetCore', 'Inngest.Connect'].map(reference).join('\n');

const docsProject = `<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <RootNamespace>Docs</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" Version="4.0.2" />
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" Version="10.11.0" />
    <PackageReference Include="Microsoft.Extensions.Diagnostics.Testing" Version="10.11.0" />
    <PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.20.1" />
    <PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.20.1" />
    <PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.20.1" />
    <PackageReference Include="Sentry.AspNetCore" Version="6.12.1" />
  </ItemGroup>
  <ItemGroup>
${references}
${reference('Inngest.Sentry')}
${reference('Inngest.Testing')}
${reference('Inngest.Encryption')}
  </ItemGroup>
  <ItemGroup>
    <Using Include="Docs.Support" />
  </ItemGroup>
</Project>
`;

const testHostProject = (extra: string) => `<Project Sdk="Microsoft.NET.Sdk.Web">
  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.TestHost" Version="10.0.14" />
${extra}  </ItemGroup>
  <ItemGroup>
${references}
  </ItemGroup>
</Project>
`;

function write(path: string, content: string) {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, content);
}

function run(command: string, args: string[]): { status: number; output: string } {
  const result = spawnSync(command, args, {
    cwd: out,
    encoding: 'utf8',
    env: { ...process.env, INNGEST_BUILD_SLOT: 'site-snippets', DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' },
    maxBuffer: 64 * 1024 * 1024,
  });
  return { status: result.status ?? 1, output: `${result.stdout}${result.stderr}` };
}

// ------------------------------------------------------------------------------------------------------- main

const content = join(site, 'src', 'content', 'docs');
const pages = new Map<string, Snippet[]>();
for (const file of walk(content).sort()) {
  const page = relative(content, file).replace(/\.mdx?$/, '').split(sep).join('/');
  const found = extract(file, page);
  if (found.length > 0) pages.set(page, found);
}
const all = [...pages.values()].flat();
const skipped = all.filter((s) => s.directive.kind === 'skip');

rmSync(out, { recursive: true, force: true });
write(join(out, 'Directory.Build.props'), props);
write(join(out, 'Directory.Packages.props'), '<Project />\n');
write(join(out, 'Docs', 'Docs.csproj'), docsProject);
write(join(out, 'Docs', 'Support.cs'), readFileSync(join(harness, 'Support.cs'), 'utf8'));
for (const snippet of all) {
  const code = generateSnippet(snippet);
  if (code) write(join(out, 'Docs', 'Pages', `${snippet.page.replace(/\//g, '.')}.${snippet.index}.cs`), code);
}
write(join(out, 'Parity', 'Parity.csproj'), testHostProject(''));
write(join(out, 'Parity', 'Program.cs'), readFileSync(join(harness, 'Parity.cs'), 'utf8'));
write(join(out, 'Parity', 'Cases.cs'), generateParityCases());
write(
  join(out, 'Capture', 'Capture.csproj'),
  testHostProject('    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" Version="10.11.0" />\n'),
);
for (const name of readdirSync(join(harness, 'capture'))) {
  write(join(out, 'Capture', name), readFileSync(join(harness, 'capture', name), 'utf8'));
}
write(
  join(out, 'Snippets.slnx'),
  '<Solution>\n  <Project Path="Docs/Docs.csproj" />\n  <Project Path="Parity/Parity.csproj" />\n  <Project Path="Capture/Capture.csproj" />\n</Solution>\n',
);

console.log(`${all.length} C# blocks in ${pages.size} pages (${skipped.length} skipped), harness in ${out}`);

const build = run('dotnet', ['build', 'Snippets.slnx', '-nologo', '-v:q', '-clp:NoSummary']);
// Diagnostics point at the .mdx files through #line directives; paths are shortened to keep them readable.
const diagnostics = [
  ...new Set(
    build.output
      .split('\n')
      .filter((line) => /: (error|warning) /.test(line))
      .map((line) => line.trim().replace(/ \[[^\]]+\.csproj\]$/, '').replaceAll(`${site}${sep}`, '').replaceAll(`${out}${sep}`, '')),
  ),
];
if (build.status !== 0 || diagnostics.length > 0) {
  console.error(diagnostics.length > 0 ? diagnostics.join('\n') : build.output);
  console.error(`\nThe snippets do not compile (${diagnostics.length} diagnostics).`);
  process.exit(1);
}
console.log('Every C# block compiles.');

const parity = run('dotnet', ['run', '--project', 'Parity/Parity.csproj', '--no-build']);
process.stdout.write(parity.output);
if (parity.status !== 0) process.exit(1);

const captured = join(out, 'replay-calls.json');
const capture = run('dotnet', ['run', '--project', 'Capture/Capture.csproj', '--no-build', '--', captured]);
if (capture.status !== 0) {
  console.error(capture.output);
  process.exit(1);
}

const source = readFileSync(join(harness, 'capture', 'FulfillOrder.cs'), 'utf8')
  .split('\n')
  .filter((line) => !/^(using|namespace) /.test(line))
  .join('\n')
  .trim();
const replay = `${JSON.stringify({ source, ...JSON.parse(readFileSync(captured, 'utf8')) }, null, 2)}\n`;
if (writeReplay) {
  write(replayData, replay);
  console.log(`Wrote ${relative(site, replayData)}.`);
} else if (!existsSync(replayData) || readFileSync(replayData, 'utf8') !== replay) {
  console.error(`${relative(site, replayData)} differs from what the SDK answers now. Run with --write-replay to update it.`);
  process.exit(1);
} else {
  console.log(`${relative(site, replayData)} matches what the SDK answers.`);
}
