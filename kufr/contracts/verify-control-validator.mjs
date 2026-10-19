import assert from 'node:assert/strict';
import { readFile, readdir } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import Ajv2020 from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';
import { parseStrictJson } from './control-validator-core.mjs';
import { validateControlMessage } from './control-validator.mjs';

// The compiler belongs only in generation and this differential test, never in
// the runtime graph consumed by NativeDeviceConnection or the browser shell.
const ajv = new Ajv2020({ allErrors: true, strict: true });
addFormats(ajv, ['uuid']);
const referenceSchema = ajv.compile(parseStrictJson(await readFile(new URL('./control-message-v1.schema.json', import.meta.url))));
function reference(bytes) {
  const message = parseStrictJson(bytes, 8192);
  if (bytes.length > 2048 && message.type !== 'conversation.text' && message.type !== 'conversation.result') throw new Error('message exceeds 2048 UTF-8 bytes');
  if (!referenceSchema(message)) throw new Error(ajv.errorsText(referenceSchema.errors));
  return message;
}
function outcome(validator, bytes) {
  try { return { message: validator(bytes) }; }
  catch (error) { return { error: error.message }; }
}
let compared = 0;
function compare(bytes, label) {
  assert.deepEqual(outcome(validateControlMessage, bytes), outcome(reference, bytes), label);
  compared++;
}
for (const validity of ['valid', 'invalid']) {
  const directory = new URL(`./fixtures/${validity}/`, import.meta.url);
  for (const name of (await readdir(directory)).filter(name => name.endsWith('.json')).sort()) {
    const bytes = await readFile(new URL(name, directory));
    compare(bytes, `${validity}/${name}`);
    if (validity !== 'valid') continue;
    const message = JSON.parse(bytes.toString('utf8'));
    for (const key of Object.keys(message)) {
      const missing = structuredClone(message); delete missing[key];
      compare(Buffer.from(JSON.stringify(missing)), `${name}: missing ${key}`);
      for (const value of [null, -1, 0, 1.5, Number.MAX_SAFE_INTEGER + 1, 'invalid', [], {}]) {
        compare(Buffer.from(JSON.stringify({ ...message, [key]: value })), `${name}: mutated ${key}`);
      }
    }
  }
}
// Exercise lexical and UTF-8 safety independently of schema validity.
for (const bytes of [Buffer.from('{"x":1,"x":2}'), Buffer.from('{"x":1e0}'), Buffer.from('{"x":1.0}'),
  Buffer.from('{/* comment */"x":1}'), Buffer.from([0xc3, 0x28]), Buffer.alloc(8193, 0x20)]) compare(bytes, 'strict raw bytes');

const runtime = new URL('./control-validator.mjs', import.meta.url).href;
const fixture = new URL('./fixtures/valid/selection-start.json', import.meta.url).href;
const withoutCodeGeneration = spawnSync(process.execPath, ['--disallow-code-generation-from-strings', '--input-type=module', '-e', `
  globalThis.Function = function () { throw new Error('Runtime attempted dynamic compilation'); };
  const { readFile } = await import('node:fs/promises');
  const { validateControlMessage } = await import(${JSON.stringify(runtime)});
  validateControlMessage(await readFile(new URL(${JSON.stringify(fixture)})));
`], { encoding: 'utf8' });
assert.equal(withoutCodeGeneration.status, 0, withoutCodeGeneration.stderr);
console.log(`Standalone validator matches AJV for ${compared} fixture/mutation/raw cases; runtime import and validation pass with Function blocked and V8 string code generation disabled.`);
