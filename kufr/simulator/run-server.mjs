import { readFile } from 'node:fs/promises';

import { createSimulatorServer } from './server.mjs';

const certificatePath = process.env.KUFR_SIMULATOR_CERT;
const privateKeyPath = process.env.KUFR_SIMULATOR_KEY;
const token = process.env.KUFR_SIMULATOR_TOKEN;
const bridgeExecutable = process.env.KUFR_SIMULATOR_BRIDGE;

if (!certificatePath || !privateKeyPath || !token || !bridgeExecutable) {
  throw new Error('Set KUFR_SIMULATOR_CERT, KUFR_SIMULATOR_KEY, KUFR_SIMULATOR_TOKEN and KUFR_SIMULATOR_BRIDGE');
}

const simulator = await createSimulatorServer({
  certificate: await readFile(certificatePath),
  privateKey: await readFile(privateKeyPath),
  token,
  bridgeExecutable,
  port: 8443,
});

console.log(`SIMULACE WSS ready at wss://127.0.0.1:${simulator.port}/v1/control`);

for (const signal of ['SIGINT', 'SIGTERM']) {
  process.once(signal, async () => {
    await simulator.close();
    process.exit(0);
  });
}
