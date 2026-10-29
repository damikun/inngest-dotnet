// Syntax colors for code blocks, from Inngest's code palette: purple keywords, green strings, blue types and
// amber numbers on the site's warm surfaces. Expressive Code raises any token that falls short of its contrast
// minimum, so these are starting points rather than exact values.

interface Palette {
  name: string;
  type: 'dark' | 'light';
  background: string;
  foreground: string;
  selection: string;
  comment: string;
  keyword: string;
  string: string;
  number: string;
  type_: string;
  func: string;
  property: string;
  punctuation: string;
  tag: string;
  inserted: string;
  deleted: string;
}

function theme(p: Palette) {
  return {
    name: p.name,
    type: p.type,
    colors: {
      'editor.background': p.background,
      'editor.foreground': p.foreground,
      'editor.selectionBackground': p.selection,
      'terminal.background': p.background,
      'terminal.foreground': p.foreground,
      'diffEditor.insertedTextBackground': p.inserted,
      'diffEditor.removedTextBackground': p.deleted,
    },
    tokenColors: [
      { scope: ['comment', 'punctuation.definition.comment'], settings: { foreground: p.comment, fontStyle: 'italic' } },
      {
        scope: ['keyword', 'storage', 'storage.modifier', 'storage.type', 'keyword.type', 'keyword.operator.new', 'variable.language'],
        settings: { foreground: p.keyword },
      },
      { scope: ['keyword.operator', 'punctuation', 'meta.brace'], settings: { foreground: p.punctuation } },
      { scope: ['string', 'string.quoted', 'string.template', 'markup.inline.raw'], settings: { foreground: p.string } },
      { scope: ['constant.numeric', 'constant.language', 'constant.character', 'constant.other'], settings: { foreground: p.number } },
      {
        scope: ['entity.name.type', 'entity.name.class', 'entity.name.namespace', 'support.type', 'support.class', 'entity.other.inherited-class'],
        settings: { foreground: p.type_ },
      },
      { scope: ['entity.name.function', 'support.function', 'meta.function-call', 'entity.name.command'], settings: { foreground: p.func } },
      {
        scope: [
          'support.type.property-name',
          'entity.name.variable.property',
          'variable.other.property',
          'variable.other.object.property',
          'entity.name.tag.yaml',
          'entity.other.attribute-name',
        ],
        settings: { foreground: p.property },
      },
      { scope: ['entity.name.tag', 'meta.tag'], settings: { foreground: p.tag } },
      { scope: ['variable', 'variable.parameter', 'variable.other'], settings: { foreground: p.foreground } },
      { scope: ['markup.inserted'], settings: { foreground: p.string } },
      { scope: ['markup.deleted'], settings: { foreground: p.func } },
      { scope: ['markup.bold'], settings: { fontStyle: 'bold' } },
      { scope: ['markup.italic'], settings: { fontStyle: 'italic' } },
    ],
  };
}

export const darkCodeTheme = theme({
  name: 'inngest-dotnet-dark',
  type: 'dark',
  background: '#141312',
  foreground: '#e8e5e2',
  selection: '#3a3735',
  comment: '#a8a29d',
  keyword: '#ad9ffc',
  string: '#66bd8b',
  number: '#fcc43f',
  type_: '#9cd2ff',
  func: '#fd8a72',
  property: '#cec6fd',
  punctuation: '#b5afaa',
  tag: '#9cd2ff',
  inserted: '#09301f',
  deleted: '#451c16',
});

export const lightCodeTheme = theme({
  name: 'inngest-dotnet-light',
  type: 'light',
  background: '#f2efeb',
  foreground: '#1b1a19',
  selection: '#f9e3df',
  comment: '#625c57',
  keyword: '#6222df',
  string: '#016239',
  number: '#95360f',
  type_: '#1450b1',
  func: '#a52015',
  property: '#4d1fc4',
  punctuation: '#4b4744',
  tag: '#1450b1',
  inserted: '#dff5e6',
  deleted: '#feebe9',
});
