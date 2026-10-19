import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';

const localDirectory = relative => fileURLToPath(new URL(relative, import.meta.url));

// Vite rewrites asset URLs before this hook. Hash the actual early claim script,
// retaining a strict policy without an unsafe-inline script exception.
function applicationCsp() {
  return {
    name: 'application-csp', apply: 'build',
    transformIndexHtml: {
      order: 'post',
      handler(html) {
        const hashes = [...html.matchAll(/<script\b(?![^>]*\bsrc\s*=)[^>]*>([\s\S]*?)<\/script>/gi)]
          .map(match => `'sha256-${createHash('sha256').update(match[1]).digest('base64')}'`);
        const policy = ["default-src 'none'", `script-src 'self' ${hashes.join(' ')}`,
          "style-src 'self'", "img-src 'self' blob: data:", "font-src 'self'",
          "connect-src 'self' https: wss:", "media-src 'self' blob:", "worker-src 'self' blob:",
          "object-src 'none'", "base-uri 'none'", "form-action 'self'", "frame-src 'none'"].join('; ');
        return [{ tag: 'meta', attrs: { 'http-equiv': 'Content-Security-Policy', content: policy }, injectTo: 'head-prepend' }];
      },
    },
  };
}

export default defineConfig({
  plugins: [vue(), applicationCsp()],
  base: '/',
  // The offline entry has a virtual bundle supplied only by its packager.
  optimizeDeps: { entries: ['index.html'] },
  server: {
    fs: {
      // Shared CSS imports reference fonts outside the application root. Keep
      // the dev server confined to the app and its public source dependencies.
      allow: [
        localDirectory('./'), localDirectory('../design-system/'), localDirectory('../contracts/'),
        localDirectory('../firmware/diagnostics/servo-bench/web/'),
        ...['twin_core.js', 'timeline_core.js', 'twin_media_core.js', 'twin_media_controller.js', 'viewer_engine_core.js']
          .map(name => localDirectory(`../mechanika/final/${name}`)),
      ],
    },
  },
});
