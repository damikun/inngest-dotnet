import { useState } from 'react';
import { tokenize } from '../../lib/highlight/highlight';

interface Props {
  code: string;
  language: 'csharp' | 'json';
  label: string;
  // Copying needs JavaScript; without it the button is hidden by the site CSS.
  canCopy: boolean;
}

export default function CodeOutput({ code, language, label, canCopy }: Props) {
  const [copied, setCopied] = useState(false);

  async function copy() {
    try {
      await navigator.clipboard.writeText(code);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } catch {
      setCopied(false);
    }
  }

  return (
    <div className="idn-code">
      {canCopy && (
        <div className="idn-js-only mb-1.5 flex justify-end">
          <button type="button" className="idn-copy" onClick={copy}>
            {copied ? 'Copied' : 'Copy'}
            <span className="sr-only"> {label}</span>
          </button>
        </div>
      )}
      <span className="sr-only" aria-live="polite">
        {copied ? `${label} copied to the clipboard` : ''}
      </span>
      {/* A scrollable region has to be reachable by keyboard. */}
      <pre tabIndex={0} aria-label={label}>
        <code>
          {tokenize(code, language).map((token, i) =>
            token.kind === 'plain' ? token.text : (
              <span key={i} className={`tok-${token.kind}`}>
                {token.text}
              </span>
            ),
          )}
        </code>
      </pre>
    </div>
  );
}
