// Starlight's own module for its parsed configuration, which its components import. The sidebar in it is what
// llms.txt follows.
declare module 'virtual:starlight/user-config' {
  import type { StarlightConfig } from '@astrojs/starlight/types';
  const config: StarlightConfig;
  export default config;
}
