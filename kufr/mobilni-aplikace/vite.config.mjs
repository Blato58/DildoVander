import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vite';

const localDirectory = relative => fileURLToPath(new URL(relative, import.meta.url));

export default defineConfig({
  server: {
    fs: {
      // Shared CSS imports reference fonts outside the application root. Keep
      // the dev server confined to the app and its public source dependencies.
      allow: [localDirectory('./'), localDirectory('../design-system/'), localDirectory('../contracts/')],
    },
  },
});
