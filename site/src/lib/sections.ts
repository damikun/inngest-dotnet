// The section a docs page belongs to, from the first segment of its id. Used for social cards and structured data.
const sections: Record<string, string> = {
  'how-it-works': 'Concepts',
  guides: 'Guide',
  examples: 'Example',
  explainers: 'Interactive',
  reference: 'Reference',
  internals: 'Internals',
};

// Pages for users about the project as a whole, wherever their files live.
const project = new Set(['roadmap', 'internals/readiness', 'internals/benchmarks']);

export function sectionOf(id: string): string | undefined {
  return project.has(id) ? 'Project' : sections[id.split('/')[0]];
}

// The path of a page's social card, relative to the site's base path.
export function cardPath(id: string): string {
  return `og/${id === '' || id === 'index' ? 'index' : id}.png`;
}

export const CARD_SIZE = { width: 1200, height: 630 };
