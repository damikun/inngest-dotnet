// A small tokenizer for the C# and JSON that the interactive explainers generate in the browser. Static code
// blocks use Expressive Code at build time; this only has to color generated output, and it never drops or
// reorders text, so the tokens always join back into the input.

export type TokenKind = 'keyword' | 'type' | 'string' | 'number' | 'comment' | 'property' | 'punctuation' | 'plain';

export interface Token {
  kind: TokenKind;
  text: string;
}

const CS_KEYWORDS = new Set([
  'new', 'var', 'await', 'async', 'return', 'public', 'private', 'internal', 'sealed', 'class', 'record', 'override',
  'static', 'readonly', 'using', 'namespace', 'null', 'true', 'false', 'if', 'else', 'foreach', 'in', 'is', 'not',
  'string', 'int', 'long', 'bool', 'object', 'void', 'decimal', 'double', 'this', 'base', 'throw', 'catch', 'try',
  'when', 'default',
]);

const CS_PATTERN =
  /(\/\/[^\n]*)|(@?"(?:[^"\\\n]|\\.)*")|(\b\d+(?:\.\d+)?\b)|(@?[A-Za-z_][A-Za-z0-9_]*)|([{}()[\];,.<>=:?!&|+\-*/]+)|(\s+)|(.)/g;

export function tokenizeCSharp(code: string): Token[] {
  const tokens: Token[] = [];
  for (const match of code.matchAll(CS_PATTERN)) {
    const [text, comment, string, number, word, punctuation] = match;
    if (comment) tokens.push({ kind: 'comment', text });
    else if (string) tokens.push({ kind: 'string', text });
    else if (number) tokens.push({ kind: 'number', text });
    else if (word) {
      const bare = word.startsWith('@') ? word.slice(1) : word;
      if (CS_KEYWORDS.has(word)) tokens.push({ kind: 'keyword', text });
      else if (word.startsWith('@')) tokens.push({ kind: 'plain', text });
      else if (/^[A-Z]/.test(bare)) tokens.push({ kind: 'type', text });
      else tokens.push({ kind: 'plain', text });
    } else if (punctuation) tokens.push({ kind: 'punctuation', text });
    else tokens.push({ kind: 'plain', text });
  }
  return tokens;
}

const JSON_PATTERN = /("(?:[^"\\]|\\.)*")(\s*:)?|(-?\b\d+(?:\.\d+)?(?:[eE][+-]?\d+)?\b)|(\btrue\b|\bfalse\b|\bnull\b)|([{}[\],:])|(\s+)|(.)/g;

export function tokenizeJson(code: string): Token[] {
  const tokens: Token[] = [];
  for (const match of code.matchAll(JSON_PATTERN)) {
    const [text, string, colon, number, literal, punctuation] = match;
    if (string) {
      tokens.push({ kind: colon ? 'property' : 'string', text: string });
      if (colon) tokens.push({ kind: 'punctuation', text: colon });
    } else if (number) tokens.push({ kind: 'number', text });
    else if (literal) tokens.push({ kind: 'keyword', text });
    else if (punctuation) tokens.push({ kind: 'punctuation', text });
    else tokens.push({ kind: 'plain', text });
  }
  return tokens;
}

export function tokenize(code: string, language: 'csharp' | 'json'): Token[] {
  return language === 'csharp' ? tokenizeCSharp(code) : tokenizeJson(code);
}
