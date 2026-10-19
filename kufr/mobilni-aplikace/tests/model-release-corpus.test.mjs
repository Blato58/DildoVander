import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { validateModel } from '../src/features/workshop/model-bundle.mjs';
const fixture = new URL('../src/features/workshop/fixture/', import.meta.url);
const model = JSON.parse(await readFile(new URL('model.json', fixture), 'utf8'));
const manifest = JSON.parse(await readFile(new URL('model-bundle.json', fixture), 'utf8'));
const cases = JSON.parse(await readFile(new URL('./fixtures/model-bundle-rejections.json', import.meta.url), 'utf8'));
for (const item of cases) {
  test(`shared .NET/browser model rejection: ${item.name}`, () => {
    const candidate = structuredClone(model);
    for (const operation of item.operations) {
      let parent = candidate;
      for (const part of operation.path.slice(0, -1)) parent = parent[part];
      parent[operation.path.at(-1)] = structuredClone(operation.value);
    }
    assert.throws(() => validateModel(candidate, manifest), /ModelBundle:/);
  });
}
