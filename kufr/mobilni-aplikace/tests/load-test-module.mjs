import { readFile } from 'node:fs/promises';
import ts from 'typescript';

export async function loadDeviceModule(name) {
  const directory = new URL('../src/features/device_connection/', import.meta.url);
  async function compile(moduleName) {
    const source = await readFile(new URL(`${moduleName}.ts`, directory), 'utf8');
    let compiled = ts.transpileModule(source, {
      compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 },
    }).outputText;
    if (moduleName === 'SimulatorDeviceConnection') {
      const conversation = await compile('LocalConversationClient');
      compiled = compiled.replace(/(['"])\.\/LocalConversationClient\1/g, JSON.stringify(conversation));
    }
    return `data:text/javascript;base64,${Buffer.from(compiled).toString('base64')}`;
  }
  return import(await compile(name));
}
