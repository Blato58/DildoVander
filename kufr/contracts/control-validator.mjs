import { readFile } from 'node:fs/promises';
import { createControlValidator, parseStrictJson } from './control-validator-core.mjs';

export { parseStrictJson } from './control-validator-core.mjs';
const schema = parseStrictJson(await readFile(new URL('./control-message-v1.schema.json', import.meta.url)));
export const validateControlMessage = createControlValidator(schema);
