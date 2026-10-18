import { BenchController } from "./controller.mjs";
import { SerialTransport, flushStopBeforeClose } from "./serial.mjs";

const elements = Object.fromEntries([
  "connect", "disconnect", "connection", "support", "channel", "pulse-width", "arm", "pulse",
  "stop", "state", "error", "readback", "log",
].map(id => [id, document.getElementById(id)]));
const supported = "serial" in navigator && window.isSecureContext;
let connecting = false;
let closing = false;
const logLines = [];

function log(line) {
  logLines.push(`${new Date().toLocaleTimeString("cs-CZ")} ${line}`);
  if (logLines.length > 200) logLines.shift();
  // Do not change scroll position or focus while the operator reads history.
  elements.log.textContent = logLines.join("\n");
}

function render(state, capabilities) {
  elements.connect.disabled = !supported || connecting || closing || state.connected;
  elements.connect.textContent = connecting ? "Připojuji…" : "Připojit USB";
  elements.disconnect.disabled = !state.connected || closing;
  elements.connection.textContent = state.connected ? "USB připojeno · CH343 · 115200 baud" :
    connecting ? "Výběr a otevření USB…" : "Odpojeno · log a registry jsou historické";
  elements.arm.disabled = !capabilities.arm || closing;
  elements.pulse.disabled = !capabilities.pulse || closing;
  elements.stop.disabled = !capabilities.stop || closing;
  elements.channel.disabled = !!state.pending || state.writing || !!state.armedUntil || closing;
  elements["pulse-width"].disabled = elements.channel.disabled;
  elements.state.textContent = state.message;
  elements.error.textContent = state.error;
  elements.error.hidden = !state.error;
  const readback = state.readback;
  elements.readback.textContent = readback ?
    `${state.connected ? "Poslední přijaté čtení" : "Historické čtení"}: kanál ${readback.channel}, MODE1 ${readback.mode1}, MODE2 ${readback.mode2}, PRE_SCALE ${readback.prescale}, ON ${readback.on}, OFF ${readback.off}, FULL_ON ${Number(readback.fullOn)}, FULL_OFF ${Number(readback.fullOff)}, očekávané OFF ${readback.expectedOff}; ${readback.match ? "registry odpovídají zápisu" : "NESHODA registrů"}. Poloha serva není měřena.` :
    "Zatím žádné čtení. Registr není měření polohy serva.";
}

const transport = new SerialTransport(navigator.serial, {
  onLine: line => controller.receive(line),
  onDisconnect: reason => controller.disconnected(reason),
  onOverflow: () => controller.fail("Příliš dlouhý řádek ze zařízení. Vyžaduje nové potvrzení Stop."),
});
const controller = new BenchController({
  write: (text, stop) => transport.write(text, stop), onChange: render, onLog: log,
});

elements.connect.addEventListener("click", async () => {
  if (connecting || controller.state.connected) return;
  connecting = true;
  controller.changed();
  try {
    await transport.connect();
    if (!transport.writer) throw new Error("USB bylo odpojeno při otevírání");
    log("USB otevřeno, DTR/RTS vypnuto. Odesílám pouze Stop pro ověření výchozího stavu.");
    await controller.connected();
  } catch (error) {
    await transport.close();
    controller.disconnected();
    controller.fail(`Připojení se nezdařilo: ${error.message}`);
  } finally {
    connecting = false;
    controller.changed();
  }
});

elements.disconnect.addEventListener("click", async () => {
  if (closing) return;
  // Submit the emergency byte before releasing the port. Close aborts stalled IO;
  // disconnect itself never claims the byte or PWM-off was acknowledged.
  closing = true;
  await flushStopBeforeClose(() => controller.stop());
  controller.disconnected("Odpojuji USB · stav PWM není potvrzen");
  await transport.close();
  closing = false;
  controller.disconnected();
});
elements.arm.addEventListener("click", () => { void controller.arm(); });
elements.pulse.addEventListener("click", () => {
  void controller.pulse(Number(elements.channel.value), Number(elements["pulse-width"].value));
});
elements.stop.addEventListener("click", () => { void controller.stop(); });
if (!supported) {
  elements.support.textContent = "Web Serial není dostupné. Otevřete tuto localhost adresu v desktopovém Chrome nebo Edge s podporou Web Serial. Panel nemůže ovládat USB v tomto prohlížeči.";
}
controller.changed();
