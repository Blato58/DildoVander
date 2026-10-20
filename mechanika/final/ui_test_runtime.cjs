const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

// Unit harnesses keep their lightweight DOM. Production bundling is exercised
// separately by test_viewer_document.py through the real offline assembly path.
module.exports = function loadUiRuntime(context) {
  const canonical = path.join(__dirname, '../../design-system/ui.js');
  const shared = fs.readFileSync(fs.existsSync(canonical) ? canonical : path.join(__dirname, 'workshop-ui-runtime.js'), 'utf8');
  const viewer = fs.readFileSync(path.join(__dirname, 'viewer_ui.js'), 'utf8');
  for (const [source, namespace] of [[shared, 'VapourUI'], [viewer, 'VapourViewerUi']]) {
    const exports = [...source.matchAll(/^export function (\w+)\(/gm)].map(match => match[1]);
    const code = source.replace(/^export /gm, '').replace(/^import [^\n]+/m,
      'const { button, notice, statusBadge } = globalThis.VapourUI;');
    vm.runInNewContext(`globalThis.${namespace} = (() => {${code}\nreturn {${exports.join(',')}};})();`, context);
  }
};
