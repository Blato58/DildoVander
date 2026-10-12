// Original project artwork, AGPL-3.0-only. No downloaded image or font is used.
// Run after Capacitor sync and before compiling the native asset catalog.
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { deflateSync } from 'node:zlib';

const mobileRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const palette = await readFile(resolve(mobileRoot, '../design-system/tokens/palette.css'), 'utf8');
const output = resolve(mobileRoot, 'ios/App/App/Assets.xcassets/AppIcon.appiconset/AppIcon-512@2x.png');
const size = 1024;

function color(token) {
  const match = palette.match(new RegExp(`--aw-${token}:\\s*#([0-9a-f]{6})\\s*;`, 'i'));
  if (!match) throw new Error(`Missing Arcane Workshop color: ${token}`);
  return [0, 2, 4].map(index => parseInt(match[1].slice(index, index + 2), 16));
}

const background = color('void');
const surface = color('surface');
const brass = color('brass');

// Signed distance to an axis-aligned rounded rectangle; subpixel coverage
// smooths edges without requiring a graphics package on the signing runner.
function rectangle(x, y, left, top, width, height, radius) {
  const dx = Math.abs(x - left - width / 2) - width / 2 + radius;
  const dy = Math.abs(y - top - height / 2) - height / 2 + radius;
  return Math.hypot(Math.max(dx, 0), Math.max(dy, 0)) + Math.min(Math.max(dx, dy), 0) - radius;
}

const shapes = [
  { bounds: [390, 242, 244, 160, 48], fill: brass },
  { bounds: [420, 272, 184, 160, 20], fill: background },
  { bounds: [180, 354, 664, 432, 64], fill: brass },
  { bounds: [208, 382, 608, 376, 38], fill: surface },
  { bounds: [208, 460, 608, 20, 0], fill: brass },
  { bounds: [292, 436, 44, 68, 10], fill: brass },
  { bounds: [688, 436, 44, 68, 10], fill: brass },
  ...Array.from({ length: 6 }, (_, index) => ({
    bounds: [286 + index * 84, 552, 32, 138, 16], fill: brass,
  })),
];

const rows = Buffer.alloc(size * (1 + size * 3));
for (let y = 0; y < size; y++) {
  const row = y * (1 + size * 3);
  rows[row] = 0; // PNG filter: none, RGB with no alpha channel.
  for (let x = 0; x < size; x++) {
    const pixel = [...background];
    for (const shape of shapes) {
      const coverage = Math.max(0, Math.min(1, 0.5 - rectangle(x + 0.5, y + 0.5, ...shape.bounds)));
      for (let channel = 0; channel < 3; channel++) {
        pixel[channel] += coverage * (shape.fill[channel] - pixel[channel]);
      }
    }
    for (let channel = 0; channel < 3; channel++) {
      rows[row + 1 + x * 3 + channel] = Math.round(pixel[channel]);
    }
  }
}

function crc32(bytes) {
  let crc = 0xffffffff;
  for (const byte of bytes) {
    crc ^= byte;
    for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ ((crc & 1) ? 0xedb88320 : 0);
  }
  return (crc ^ 0xffffffff) >>> 0;
}

function chunk(type, data) {
  const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const length = Buffer.alloc(4);
  length.writeUInt32BE(data.length);
  const checksum = Buffer.alloc(4);
  checksum.writeUInt32BE(crc32(body));
  return Buffer.concat([length, body, checksum]);
}

const header = Buffer.alloc(13);
header.writeUInt32BE(size, 0);
header.writeUInt32BE(size, 4);
header[8] = 8; // Eight bits per RGB channel.
header[9] = 2; // Truecolor; App Store icons must be opaque.
const png = Buffer.concat([
  Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
  chunk('IHDR', header),
  chunk('IDAT', deflateSync(rows, { level: 9 })),
  chunk('IEND', Buffer.alloc(0)),
]);

await mkdir(dirname(output), { recursive: true });
await writeFile(output, png);
console.log('Generated original opaque 1024x1024 iOS AppIcon from Arcane Workshop tokens.');
