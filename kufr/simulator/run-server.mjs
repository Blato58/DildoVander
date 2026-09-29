import { readFile } from 'node:fs/promises';
import { createInterface } from 'node:readline';

import { createSimulatorServer } from './server.mjs';

const certificatePath = process.env.KUFR_SIMULATOR_CERT;
const privateKeyPath = process.env.KUFR_SIMULATOR_KEY;
const token = process.env.KUFR_SIMULATOR_TOKEN;
const bridgeExecutable = process.env.KUFR_SIMULATOR_BRIDGE;
const journalPath = process.env.KUFR_SIMULATOR_JOURNAL;

if (!certificatePath || !privateKeyPath || !token || !bridgeExecutable || !journalPath) {
  throw new Error('Set KUFR_SIMULATOR_CERT, KUFR_SIMULATOR_KEY, KUFR_SIMULATOR_TOKEN, KUFR_SIMULATOR_BRIDGE and KUFR_SIMULATOR_JOURNAL');
}

const simulator = await createSimulatorServer({
  certificate: await readFile(certificatePath),
  privateKey: await readFile(privateKeyPath),
  token,
  bridgeExecutable,
  journalPath,
  port: 8443,
});

console.log(`SIMULACE WSS ready at wss://127.0.0.1:${simulator.port}/v1/control`);
console.log('SIMULACE: po kontrole přihrádky zadejte do konzole inspected pro servisní zotavení.');

const consoleInput = createInterface({ input: process.stdin });
consoleInput.on('line', async (line) => {
  if (line.trim() === 'inspected') {
    const recovered = await simulator.recoverAfterInspection();
    console.log(recovered ? 'SIMULACE: zotavení provedeno.' : 'SIMULACE: zotavení odmítnuto.');
  }
});

for (const signal of ['SIGINT', 'SIGTERM']) {
  process.once(signal, async () => {
    consoleInput.close();
    await simulator.close();
    process.exit(0);
  });
}
