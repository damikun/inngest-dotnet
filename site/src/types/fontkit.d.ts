// The parts of fontkit that the social image renderer uses.
declare module 'fontkit' {
  interface Path {
    scale(x: number, y: number): Path;
    translate(x: number, y: number): Path;
    toSVG(): string;
  }

  interface Glyph {
    path: Path;
  }

  interface GlyphPosition {
    xAdvance: number;
    xOffset: number;
    yOffset: number;
  }

  interface GlyphRun {
    glyphs: Glyph[];
    positions: GlyphPosition[];
  }

  interface Font {
    unitsPerEm: number;
    // Features map OpenType feature tags to on or off, such as { liga: false }.
    layout(text: string, features?: Record<string, boolean>): GlyphRun;
  }

  export function openSync(path: string): Font;
  export function create(buffer: Uint8Array): Font;
}
