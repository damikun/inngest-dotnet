import { useEffect, useState } from 'react';

// False during server rendering and the first client render, true once the island has hydrated. Components render
// their static form until then, so the page reads the same with JavaScript off.
export function useEnhanced(): boolean {
  const [enhanced, setEnhanced] = useState(false);
  useEffect(() => setEnhanced(true), []);
  return enhanced;
}
