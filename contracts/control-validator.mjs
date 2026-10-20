import { createControlValidator } from './control-validator-core.mjs';
import validateSchema from './generated/control-message-v1.validator.mjs';

export { parseStrictJson } from './control-validator-core.mjs';
export const validateControlMessage = createControlValidator(validateSchema);
