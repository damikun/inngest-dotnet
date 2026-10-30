import { useEffect, useState } from 'react';

// The form the docs show a function's options in: the label of the tab the reader picked last in the synced
// <Tabs syncKey="function-style"> groups, which Starlight keeps in localStorage and restores on every page. It is read
// once the island has hydrated, so the first render matches the server's, and written when the reader picks a form
// in the island, so that the tabs of the other pages follow.
export type FunctionStyle = 'Attributes' | 'Code first';

const STORAGE_KEY = 'starlight-synced-tabs__function-style';

export function useFunctionStyle(): [FunctionStyle, (style: FunctionStyle) => void] {
  const [style, setStyle] = useState<FunctionStyle>('Attributes');

  useEffect(() => {
    try {
      if (localStorage.getItem(STORAGE_KEY) === 'Code first') setStyle('Code first');
    } catch {
      // Storage can be blocked, which leaves the docs on their first tab too.
    }
  }, []);

  function choose(next: FunctionStyle) {
    setStyle(next);
    try {
      localStorage.setItem(STORAGE_KEY, next);
    } catch {
      // The choice then lasts until the page is left.
    }
  }

  return [style, choose];
}
