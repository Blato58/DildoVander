import { LineParser } from "./controller.mjs";

export async function flushStopBeforeClose(stop, wait = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds))) {
  // Let the serialized emergency write reach USB, but never block disconnect
  // indefinitely when a previous write or device has stalled.
  await Promise.race([Promise.resolve(stop()).catch(() => {}), wait(250)]);
}

export class SerialTransport {
  constructor(serial, { onLine, onDisconnect, onOverflow }) {
    Object.assign(this, { serial, onLine, onDisconnect, onOverflow });
    this.writeChain = Promise.resolve();
    this.session = 0;
    this.writeEpoch = 0;
    this.writesPending = 0;
  }

  async connect({ authorizedOnly = false } = {}) {
    if (!this.serial) throw new Error("Web Serial není dostupné");
    let port;
    if (authorizedOnly) {
      const ports = (await this.serial.getPorts()).filter(candidate => {
        const info = candidate.getInfo();
        return info.usbVendorId === 0x1A86 && info.usbProductId === 0x55D3;
      });
      if (ports.length !== 1) {
        throw new Error("Připojení nástrojem vyžaduje právě jeden povolený CH343. Vyberte USB port tlačítkem Připojit USB v panelu.");
      }
      [port] = ports;
    } else {
      port = await this.serial.requestPort({ filters: [{ usbVendorId: 0x1A86, usbProductId: 0x55D3 }] });
    }
    this.port = port;
    try {
      await port.open({ baudRate: 115200, dataBits: 8, stopBits: 1, parity: "none",
        flowControl: "none", bufferSize: 4096 });
      await port.setSignals({ dataTerminalReady: false, requestToSend: false });
      this.reader = port.readable.getReader();
      this.writer = port.writable.getWriter();
      const session = ++this.session;
      this.writeChain = Promise.resolve();
      this.writesPending = 0;
      this.readTask = this.read(session);
    } catch (error) {
      await this.close();
      throw error;
    }
  }

  async read(session) {
    const reader = this.reader;
    const decoder = new TextDecoder();
    const parser = new LineParser(
      line => { if (session === this.session) this.onLine(line); },
      () => { if (session === this.session) this.onOverflow(); },
    );
    let reason = "USB odpojeno · stav PWM není potvrzen";
    try {
      while (session === this.session) {
        const { value, done } = await reader.read();
        if (done) break;
        parser.push(decoder.decode(value, { stream: true }));
      }
    } catch (error) {
      reason = `Čtení selhalo: ${error.message}`;
    } finally {
      reader.releaseLock();
      if (session === this.session) {
        // Notify first; cleanup must not await the read task from inside itself.
        try { this.onDisconnect(reason); }
        finally { void this.close(); }
      }
    }
  }

  write(text, stop = false) {
    if (!this.writer) return Promise.reject(new Error("USB není připojeno"));
    if (!stop && this.writesPending) return Promise.reject(new Error("Zápis USB ještě probíhá; nový povel nebyl zařazen"));
    if (stop) this.writeEpoch++;
    const epoch = this.writeEpoch;
    const session = this.session;
    const writer = this.writer;
    this.writesPending++;
    // At most one command can be writing. Stop invalidates queued old commands;
    // an already submitted UART write finishes before the emergency byte.
    const task = this.writeChain.catch(() => {}).then(() => {
      if (epoch !== this.writeEpoch || session !== this.session) throw new Error("Povel byl zrušen");
      return writer.write(new TextEncoder().encode(text));
    }).finally(() => {
      if (session === this.session) this.writesPending--;
    });
    this.writeChain = task;
    return task;
  }

  close() {
    if (this.closeTask) return this.closeTask;
    this.session++;
    this.writeEpoch++;
    const port = this.port;
    const reader = this.reader;
    const writer = this.writer;
    this.port = this.reader = this.writer = null;
    this.closeTask = (async () => {
      await Promise.allSettled([
        reader?.cancel().catch(() => {}),
        writer?.abort().catch(() => {}),
      ]);
      try {
        await this.readTask?.catch(() => {});
        await this.writeChain.catch(() => {});
        writer?.releaseLock();
      } finally {
        if (port) await port.close().catch(() => {});
      }
    })().finally(() => { this.closeTask = null; });
    return this.closeTask;
  }
}
