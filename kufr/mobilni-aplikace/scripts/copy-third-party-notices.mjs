import { copyFile, mkdir } from 'node:fs/promises';

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
];

for (const [source, name] of notices) {
  const destination = new URL(`../public/third-party-notices/${name}`, import.meta.url);
  await mkdir(new URL('.', destination), { recursive: true });
  await copyFile(new URL(source, import.meta.url), destination);
}

console.log('Copied original third-party notices for the mobile bundle.');
