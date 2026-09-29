import { readFile } from 'node:fs/promises';

import Ajv2020 from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';

import { parseStrictJson } from './control-validator.mjs';

const schema = parseStrictJson(
  await readFile(new URL('./media-control-v1.schema.json', import.meta.url)),
);
const ajv = new Ajv2020({ allErrors: true, strict: true });
addFormats(ajv, ['uuid']);
const validateSchema = ajv.compile(schema);

export function validateMediaControlMessage(bytes) {
  const message = parseStrictJson(bytes, 2048);
  if (!validateSchema(message)) {
    throw new Error(ajv.errorsText(validateSchema.errors));
  }
  return message;
}
