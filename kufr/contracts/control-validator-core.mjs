import { getNodeValue, parseTree } from 'jsonc-parser';

const decoder = new TextDecoder('utf-8', { fatal: true });
const maximumControlMessageBytes = 2048;
const maximumConversationTextBytes = 8192;

function assertStrictJsonTokens(node, source) {
  if (node.type === 'number') {
    const token = source.slice(node.offset, node.offset + node.length);
    if (!/^(0|[1-9][0-9]*|-[1-9][0-9]*)$/.test(token)) {
      throw new Error(`non-integer JSON token: ${token}`);
    }
  }

  if (node.type === 'object') {
    const keys = new Set();

    for (const property of node.children ?? []) {
      const [keyNode, valueNode] = property.children;
      const key = keyNode.value;
      if (keys.has(key)) {
        throw new Error(`duplicate JSON key: ${key}`);
      }

      keys.add(key);
      assertStrictJsonTokens(valueNode, source);
    }
  } else if (node.type === 'array') {
    for (const child of node.children ?? []) {
      assertStrictJsonTokens(child, source);
    }
  }
}

export function parseStrictJson(bytes, maximumBytes = Infinity) {
  if (bytes.length > maximumBytes) {
    throw new Error(`message exceeds ${maximumBytes} UTF-8 bytes`);
  }

  const source = decoder.decode(bytes);
  const errors = [];
  const root = parseTree(source, errors, {
    allowTrailingComma: false,
    disallowComments: true,
  });

  if (!root || errors.length > 0) {
    throw new Error('invalid JSON syntax');
  }

  assertStrictJsonTokens(root, source);
  return getNodeValue(root);
}

export function createControlValidator(validateSchema) {
  return function validateControlMessage(bytes) {
    const message = parseStrictJson(bytes, maximumConversationTextBytes);
    if (bytes.length > maximumControlMessageBytes &&
        message.type !== 'conversation.text' &&
        message.type !== 'conversation.result') {
      throw new Error(`message exceeds ${maximumControlMessageBytes} UTF-8 bytes`);
    }
    if (!validateSchema(message)) {
      throw new Error(validateSchema.errors.map(error => `data${error.instancePath} ${error.message}`).join(', '));
    }

    return message;
  };
}
