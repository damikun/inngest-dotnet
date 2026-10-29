import { type KeyboardEvent, type ReactNode, useId, useRef, useState } from 'react';

export interface TabDefinition {
  id: string;
  label: string;
  content: ReactNode;
}

interface Props {
  tabs: TabDefinition[];
  label: string;
}

// WAI-ARIA tabs with automatic activation: arrow keys, Home and End move between tabs. The server renders the same
// markup the island keeps after hydrating; site CSS hides the inactive panels when scripts run and hides the tab list
// when they do not, so without JavaScript every panel shows under its own label.
export default function Tabs({ tabs, label }: Props) {
  const [selected, setSelected] = useState(0);
  const buttons = useRef<(HTMLButtonElement | null)[]>([]);
  const prefix = useId();

  function onKeyDown(event: KeyboardEvent<HTMLButtonElement>) {
    const last = tabs.length - 1;
    const next =
      event.key === 'ArrowRight' ? (selected === last ? 0 : selected + 1)
      : event.key === 'ArrowLeft' ? (selected === 0 ? last : selected - 1)
      : event.key === 'Home' ? 0
      : event.key === 'End' ? last
      : undefined;
    if (next === undefined) return;
    event.preventDefault();
    setSelected(next);
    buttons.current[next]?.focus();
  }

  return (
    <div>
      <div role="tablist" aria-label={label} className="idn-tablist idn-js-only">
        {tabs.map((tab, i) => (
          <button
            key={tab.id}
            ref={(element) => {
              buttons.current[i] = element;
            }}
            type="button"
            role="tab"
            id={`${prefix}-tab-${tab.id}`}
            aria-selected={i === selected}
            aria-controls={`${prefix}-panel-${tab.id}`}
            tabIndex={i === selected ? 0 : -1}
            onClick={() => setSelected(i)}
            onKeyDown={onKeyDown}
          >
            {tab.label}
          </button>
        ))}
      </div>
      {tabs.map((tab, i) => (
        <div
          key={tab.id}
          role="tabpanel"
          id={`${prefix}-panel-${tab.id}`}
          aria-labelledby={`${prefix}-tab-${tab.id}`}
          data-idn-inactive={i === selected ? undefined : ''}
          className="idn-tabpanel"
        >
          <p className="eyebrow idn-nojs-only mb-2">{tab.label}</p>
          {tab.content}
        </div>
      ))}
    </div>
  );
}
