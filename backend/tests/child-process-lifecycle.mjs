import { once } from 'node:events';

export async function stopChildProcess(child) {
  // Signal exits keep exitCode null on POSIX, even after 'exit' was emitted.
  if (!child || child.exitCode !== null || child.signalCode !== null) { return; }
  const exited = once(child, 'exit');
  child.kill();
  await exited;
}
