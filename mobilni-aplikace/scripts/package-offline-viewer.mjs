/** Compose a selected immutable model and current viewer source; never run CAD. */
import { build } from 'vite';
import vue from '@vitejs/plugin-vue';
import { readFile, readdir, mkdir, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { readModelBundle } from './validate-model-bundle.mjs';
import { themeBootstrap } from '../vite.config.mjs';

export async function viewerSourceHash(appRoot) {
  const paths = [];
  async function collect(directory) {
    for (const entry of await readdir(directory, { withFileTypes: true })) {
      const absolute = path.join(directory, entry.name);
      if (entry.isDirectory() && entry.name !== 'fixture') await collect(absolute);
      else if (entry.isFile() && !entry.name.endsWith('.spec.ts')) paths.push(absolute);
    }
  }
  await collect(path.join(appRoot, 'src'));
  await collect(path.resolve(appRoot, '../design-system/tokens'));
  await collect(path.resolve(appRoot, '../design-system/components'));
  await collect(path.resolve(appRoot, '../design-system/fonts'));
  paths.push(path.resolve(appRoot, '../design-system/arcane-workshop.css'));
  for (const name of ['theme.js', 'theme.mjs']) paths.push(path.resolve(appRoot, '../design-system', name));
  paths.push(path.join(appRoot, 'vite.config.mjs'));
  paths.push(path.join(appRoot, 'package-lock.json'), path.join(appRoot, 'scripts/package-offline-viewer.mjs'));
  for (const name of ['viewer_engine_core.js', 'twin_core.js', 'timeline_core.js', 'twin_media_core.js', 'twin_media_controller.js']) paths.push(path.resolve(appRoot, '../mechanika/final', name));
  const digest = createHash('sha256');
  for (const absolute of paths.sort()) { digest.update(path.relative(appRoot, absolute).replaceAll('\\', '/') + '\0'); digest.update(await readFile(absolute)); }
  return digest.digest('hex');
}

async function viewerProvenance(appRoot) {
  const sourceHash = await viewerSourceHash(appRoot);
  const revision = execFileSync('git', ['rev-parse', 'HEAD'], { cwd: appRoot, encoding: 'utf8' }).trim();
  return { viewerSourceRevision: revision, viewerSourceHash: sourceHash };
}

export async function packageOffline({ bundleDirectory, output, allowFixture = false }) {
  const bundle = await readModelBundle(bundleDirectory, { allowFixture });
  const appRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
  const virtualId = 'virtual:workshop-offline-bundle';
  const result = await build({
    configFile: false, root: appRoot, base: './', publicDir: false, logLevel: 'warn',
    plugins: [themeBootstrap(), vue(), {
      name: 'selected-offline-model',
      resolveId(id) { if (id === virtualId) return '\0' + virtualId; },
      load(id) { if (id === '\0' + virtualId) return `export default ${JSON.stringify(bundle)};`; },
    }],
    build: {
      write: false, sourcemap: false, assetsInlineLimit: Infinity, cssCodeSplit: false, modulePreload: false,
      rollupOptions: { input: path.join(appRoot, 'src/features/workshop/offline.html'), output: { format: 'iife' } },
    },
  });
  const outputs = (Array.isArray(result) ? result : [result]).flatMap(item => item.output);
  const htmlAsset = outputs.find(item => item.fileName.endsWith('.html'));
  const scripts = outputs.filter(item => item.type === 'chunk');
  if (!htmlAsset || scripts.length !== 1 || scripts[0].imports.length || scripts[0].dynamicImports.length) throw new Error('Offline bundle must contain one closed JavaScript bundle');
  let html = String(htmlAsset.source);
  // Vite owns dependency bundling. Only its final entry/style tags are inlined.
  html = html.replace(/<script\b[^>]*\bsrc="[^"]+"[^>]*><\/script>/g, '');
  html = html.replace('</body>', () => `<script>${scripts[0].code.replace(/<\/script/gi, '<\\/script')}</script></body>`);
  for (const css of outputs.filter(item => item.type === 'asset' && item.fileName.endsWith('.css'))) {
    if (/url\((?!["']?data:)/i.test(String(css.source))) throw new Error('Offline CSS still references external resources');
    html = html.replace(/<link\b[^>]*rel="stylesheet"[^>]*>/g, () => `<style>${String(css.source)}</style>`);
  }
  if (/<script\b[^>]*\bsrc=|<link\b[^>]*rel="(?:stylesheet|modulepreload)"/i.test(html)) throw new Error('Offline output still references external scripts/styles');
  const unexpected = outputs.filter(item => item.type === 'asset' && !item.fileName.endsWith('.html') && !item.fileName.endsWith('.css'));
  if (unexpected.length) throw new Error('Offline output contains non-inlined assets: ' + unexpected.map(item => item.fileName).join(', '));
  const escape = value => value.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;');
  const licenses = [];
  for (const [label, relative] of [
    ['DildoVander', '../LICENSE'], ['Vue', 'node_modules/vue/LICENSE'], ['Pinia', 'node_modules/pinia/LICENSE'],
    ['Capacitor Core', 'node_modules/@capacitor/core/LICENSE'],
    ['Inter', '../design-system/fonts/inter-OFL.txt'], ['IBM Plex Mono', '../design-system/fonts/ibmplexmono-OFL.txt'],
  ]) licenses.push(`<h3>${label}</h3><pre>${escape(await readFile(path.resolve(appRoot, relative), 'utf8'))}</pre>`);
  html = html.replace('</body>', () => `<footer class="application-main"><details><summary>Licence a autorská práva</summary>${licenses.join('')}</details></footer></body>`);
  const target = path.resolve(output); await mkdir(path.dirname(target), { recursive: true });
  await writeFile(target, html, 'utf8');
  const appManifest = JSON.parse(await readFile(path.join(appRoot, 'package.json'), 'utf8'));
  const receipt = { schemaVersion: 1, modelId: bundle.manifest.modelId, bundleHash: bundle.manifest.bundleHash, modelSourceRevision: bundle.manifest.provenance.sourceRevision, viewerPackageVersion: appManifest.version, ...await viewerProvenance(appRoot), offline: true, fixture: bundle.manifest.provenance.kind === 'synthetic-fixture', network: 'disabled-by-csp' };
  await writeFile(target + '.manifest.json', JSON.stringify(receipt, null, 2) + '\n', 'utf8');
  return receipt;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const arg = name => { const index = process.argv.indexOf(name); return index < 0 ? undefined : process.argv[index + 1]; };
  try {
    const bundleDirectory = arg('--bundle'), output = arg('--output');
    if (!bundleDirectory || !output) throw new Error('Usage: node scripts/package-offline-viewer.mjs --bundle DIRECTORY --output FILE.html [--allow-fixture]');
    process.stdout.write(JSON.stringify(await packageOffline({ bundleDirectory, output, allowFixture: process.argv.includes('--allow-fixture') })) + '\n');
  } catch (error) { process.stderr.write(error.stack + '\n'); process.exitCode = 1; }
}
