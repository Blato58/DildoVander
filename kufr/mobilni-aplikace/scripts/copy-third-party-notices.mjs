import { copyFile, mkdir, unlink } from 'node:fs/promises';

// Vite copies public/ into dist, including builds with a custom --base.
// Keep the original notices authoritative instead of maintaining duplicate text.
const notices = [
  ['../../../LICENSE', 'project/LICENSE.txt'],
  ['../../design-system/fonts/cinzel-OFL.txt', 'fonts/cinzel-OFL.txt'],
  ['../../design-system/fonts/inter-OFL.txt', 'fonts/inter-OFL.txt'],
  ['../../design-system/fonts/ibmplexmono-OFL.txt', 'fonts/ibmplexmono-OFL.txt'],
  ['../../design-system/icons/LICENSE.txt', 'icons/LICENSE.txt'],
  ['../node_modules/@capacitor/core/LICENSE', 'capacitor/core-LICENSE.txt'],
  ['../node_modules/@capacitor/ios/LICENSE', 'capacitor/ios-LICENSE.txt'],
  ['../node_modules/vue/LICENSE', 'vue/LICENSE.txt'],
  ['../node_modules/vue-router/LICENSE', 'vue-router/LICENSE.txt'],
  ['../node_modules/pinia/LICENSE', 'pinia/LICENSE.txt'],
  ['../../contracts/node_modules/ajv/LICENSE', 'ajv/LICENSE.txt'],
  ['../../contracts/node_modules/jsonc-parser/LICENSE.md', 'jsonc-parser/LICENSE.txt'],
];

for (const [source, name] of notices) {
  const destination = new URL(`../public/third-party-notices/${name}`, import.meta.url);
  await mkdir(new URL('.', destination), { recursive: true });
  await copyFile(new URL(source, import.meta.url), destination);
}

console.log('Copied original third-party notices for the mobile bundle.');

// These two generated files belonged to the removed individual QR panel.
for (const name of ['LICENSE.txt', 'dijkstrajs-LICENSE.txt']) {
  await unlink(new URL(`../public/third-party-notices/qrcode/${name}`, import.meta.url))
    .catch(error => { if (error.code !== 'ENOENT') { throw error; } });
}
