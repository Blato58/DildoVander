import { BenchController } from "./controller.mjs";
import { SerialTransport, flushStopBeforeClose } from "./serial.mjs";
import { registerBenchTools } from "./mcp.mjs";
import { ManualProfiles, canSaveTarget } from "./profiles.mjs";
import { AutoCalibration } from "./auto-calibration.mjs";
import { mountBenchUi, benchGuidance, statusBadge, notice, emptyState } from "./panel-ui.mjs";

mountBenchUi(document);

const elements = Object.fromEntries([
  "connect", "disconnect", "connection", "support", "channel", "pulse-width", "arm", "pulse",
  "stop", "state", "error", "readback", "log", "log-empty", "mcp", "bench-readiness",
  "calibration-support", "cal-width", "cal-duration", "cal-range", "cal-minus", "cal-plus",
  "cal-1200", "cal-1500", "cal-1800", "cal-arm", "cal-move", "cal-validation", "profile-title",
  "profile-label", "profile-save-label", "profile-values", "profile-min", "profile-center", "profile-max",
  "profile-status", "profile-error", "profile-export", "profile-json", "profile-clear", "cal-state",
  "auto-context", "auto-min", "auto-max", "auto-state", "auto-candidate", "auto-save", "noise", "noise-warning",
].map(id => [id, document.getElementById(id)]));
const supported = "serial" in navigator && window.isSecureContext;
let connecting = false;
let closing = false;
const logLines = [];
const stateListeners = new Set();
const rawStateListeners = new Set();
let autoCalibration = null;
let autoContext = null;
let visibilityStopped = false;
let storage;
try { storage = window.localStorage; } catch { storage = null; }
const profiles = new ManualProfiles(storage);
let selectedProfileChannel = null;

function getRawSnapshot() {
  return { ...structuredClone(controller.state), capabilities: { ...controller.capabilities },
    connecting, closing, powerState: "unknown", positionMeasured: false };
}

function getSnapshot() {
  const snapshot = getRawSnapshot();
  if (autoCalibration?.state.active) {
    Object.assign(snapshot.capabilities, { arm: false, pulse: false, calibrate: false });
  }
  snapshot.autoCalibration = autoCalibration ? structuredClone(autoCalibration.state) : { active: false };
  return snapshot;
}

function publishObservers() {
  for (const listener of stateListeners) {
    try { listener(getSnapshot()); }
    catch (error) { console.error("WebMCP state observer failed", error); }
  }
}

function publishState(state, capabilities) {
  for (const listener of [...rawStateListeners]) listener(getRawSnapshot());
  if (autoCalibration?.state.candidateUs !== null && autoCalibration?.state.candidateUs !== undefined &&
    (!state.connected || state.fault || state.error || !state.calibrationSupported ||
      state.message.startsWith("Firmware připraven po startu"))) discardProposal();
  render(state, capabilities);
  publishObservers();
}

function log(line) {
  logLines.push(`${new Date().toLocaleTimeString("cs-CZ")} ${line}`);
  if (logLines.length > 200) logLines.shift();
  // Do not change scroll position or focus while the operator reads history.
  elements.log.textContent = logLines.join("\n");
  elements["log-empty"].hidden = logLines.length > 0;
}

function render(state, capabilities) {
  const automatic = !!autoCalibration?.state.active;
  elements["bench-readiness"].innerHTML = notice({
    ...benchGuidance(state, capabilities, { supported, connecting, closing, automatic }),
    live: false,
  });
  elements.connect.disabled = !supported || connecting || closing || state.connected;
  elements.connect.textContent = connecting ? "Připojuji…" : "Připojit USB";
  elements.disconnect.disabled = !state.connected || closing;
  elements.connection.innerHTML = statusBadge({
    label: state.connected ? "USB otevřeno · CH343 · 115200 baud" :
      connecting ? "Výběr a otevření USB…" : "Odpojeno · log a registry jsou historické",
    tone: state.connected || connecting ? "info" : "neutral",
  });
  elements.arm.disabled = automatic || !capabilities.arm || closing;
  elements.pulse.disabled = automatic || !capabilities.pulse || closing;
  elements.stop.disabled = !capabilities.stop || closing;
  elements.noise.disabled = !capabilities.stop || closing;
  elements.channel.disabled = automatic || !!state.pending || state.writing || !!state.armedUntil || closing;
  elements["pulse-width"].disabled = elements.channel.disabled;
  elements.state.innerHTML = statusBadge({ label: state.message, tone: state.fault ? "danger" : state.pending ? "info" : "neutral" });
  elements.error.innerHTML = state.error
    ? notice({ title: "Pokus nelze dokončit", message: state.error, tone: "danger" }) : "";
  elements.error.hidden = !state.error;
  const readback = state.readback;
  if (readback) {
    elements.readback.textContent =
      `${state.connected ? "Poslední přijaté čtení" : "Historické čtení"}: kanál ${readback.channel}, MODE1 ${readback.mode1}, MODE2 ${readback.mode2}, PRE_SCALE ${readback.prescale}, ON ${readback.on}, OFF ${readback.off}, FULL_ON ${Number(readback.fullOn)}, FULL_OFF ${Number(readback.fullOff)}, očekávané OFF ${readback.expectedOff}; ${readback.match ? "registry odpovídají zápisu" : "NESHODA registrů"}. Poloha serva není měřena.`;
  } else {
    elements.readback.innerHTML = emptyState({ title: "Zatím žádné čtení", message: "Připojte USB a vyčkejte na čtení. Registr není měření polohy serva." });
  }
  renderCalibration(state, capabilities);
  renderAuto();
}

function calibrationValues() {
  const width = Number(elements["cal-width"].value);
  const durationMs = Number(elements["cal-duration"].value);
  return { width, durationMs, valid: Number.isInteger(width) && width >= 1000 && width <= 2000 &&
    Number.isInteger(durationMs) && durationMs >= 500 && durationMs <= 3000 };
}

function renderCalibration(state, capabilities) {
  const channel = Number(elements.channel.value);
  const values = calibrationValues();
  const automatic = !!autoCalibration?.state.active;
  const busy = automatic || !!state.pending || state.writing || !!state.armedUntil || closing;
  for (const id of ["cal-width", "cal-duration", "cal-range", "cal-minus", "cal-plus", "cal-1200", "cal-1500", "cal-1800"]) {
    elements[id].disabled = busy;
  }
  elements["calibration-support"].textContent = state.calibrationSupported ?
    `Firmware potvrdil calibration=v1 · vybraný kanál ${channel}` :
    "Kalibrace není dostupná: čeká na READY calibration=v1 z firmwaru. Běžný pulz 500 ms zůstává samostatný.";
  elements["cal-arm"].disabled = automatic || !state.calibrationSupported || !capabilities.arm || !values.valid || closing;
  elements["cal-move"].disabled = automatic || !capabilities.calibrate || !values.valid || closing;
  elements["cal-state"].textContent = state.message;
  elements["cal-validation"].textContent = values.valid ?
    `Další ruční pokus: kanál ${channel}, ${values.width} µs, ${values.durationMs} ms. Vyžaduje samostatné Arm.` :
    "Zadejte celé hodnoty: 1000–2000 µs a 500–3000 ms. Žádný povel nebyl odeslán.";
  const profile = profiles.channels[channel];
  if (selectedProfileChannel !== channel) {
    elements["profile-label"].value = profile.label;
    selectedProfileChannel = channel;
  }
  elements["profile-title"].textContent = `Místní ruční profil · kanál ${channel}`;
  elements["profile-values"].textContent = `Min ${profile.min ?? "—"} · střed ${profile.center ?? "—"} · max ${profile.max ?? "—"} µs`;
  const saveAllowed = !automatic && canSaveTarget(state, channel, values.width);
  for (const point of ["min", "center", "max"]) elements[`profile-${point}`].disabled = !saveAllowed;
  for (const id of ["profile-label", "profile-save-label", "profile-clear"]) elements[id].disabled = automatic;
  if (profiles.error) profileError(profiles.error);
}

function profileError(message) {
  elements["profile-error"].textContent = message;
  elements["profile-error"].hidden = !message;
}

function updateProfile(action, message = "Ruční profil uložen v tomto prohlížeči. Poloha nebyla měřena.", candidate = false) {
  if (autoCalibration?.state.active) return false;
  if (!candidate) discardProposal();
  try {
    action();
    profileError("");
    elements["profile-status"].textContent = message;
    elements["profile-json"].value = "";
    renderCalibration(controller.state, controller.capabilities);
    renderAuto();
  } catch (error) { profileError(error.message); }
}

const transport = new SerialTransport(navigator.serial, {
  onLine: line => controller.receive(line),
  onDisconnect: reason => controller.disconnected(reason),
  onOverflow: () => controller.fail("Příliš dlouhý řádek ze zařízení. Vyžaduje nové potvrzení Stop."),
});
const controller = new BenchController({
  write: (text, stop) => transport.write(text, stop), onChange: publishState, onLog: log,
});
autoCalibration = new AutoCalibration({
  getState: getRawSnapshot,
  subscribe: listener => { rawStateListeners.add(listener); return () => rawStateListeners.delete(listener); },
  arm: () => controller.arm(),
  calibrate: (channel, width, durationMs) => controller.calibrate(channel, width, durationMs),
  stop: () => controller.stop(),
  onChange: () => {
    if (!autoCalibration) return;
    render(controller.state, controller.capabilities);
    publishObservers();
  },
});

function discardProposal(message) {
  autoContext = null;
  if (autoCalibration?.state.candidateUs !== null && autoCalibration?.state.candidateUs !== undefined) {
    autoCalibration.discardCandidate(message);
  }
}

function canStartAuto() {
  const channel = Number(elements.channel.value);
  const center = profiles.channels[channel]?.center;
  const state = controller.state;
  return autoCalibration && !autoCalibration.state.active && !connecting && !closing &&
    document.visibilityState !== "hidden" && Number.isInteger(center) && center >= 1000 && center <= 2000 &&
    state.calibrationSupported && !state.error && controller.capabilities.arm &&
    (autoCalibration.state.candidateUs === null || autoCalibration.state.stopConfirmed);
}

function candidateProposal() {
  return { ...structuredClone(autoCalibration.state), profileSignature: autoContext?.profileSignature ?? null };
}

function renderAuto() {
  const channel = Number(elements.channel.value);
  const center = profiles.channels[channel]?.center;
  elements["auto-context"].textContent = `Kanál ${channel} · uložený ruční střed ${center ?? "—"} µs · 25 µs / 1500 ms / nejméně 3 s`;
  const available = canStartAuto();
  elements["auto-min"].disabled = !available || center <= 1000;
  elements["auto-max"].disabled = !available || center >= 2000;
  const state = autoCalibration?.state;
  const activeTarget = state?.active && Number.isInteger(state.currentUs) ? ` Poslední zkoušený cíl: ${state.currentUs} µs.` : "";
  elements["auto-state"].textContent = (state?.message || "Automatické kroky nejsou spuštěny.") + activeTarget;
  elements["auto-candidate"].textContent = state?.candidateUs !== null && state?.candidateUs !== undefined ?
    `Navržená ${state.direction === "min" ? "dolní" : "horní"} mez: ${state.candidateUs} µs. ${state.stopConfirmed ? "Nové potvrzení Stop přijato; vyžaduje váš úsudek a samostatné uložení." : "Čeká na nové potvrzení Stop; zatím nelze uložit."}` :
    "Žádná navržená mez. Nic se neukládá automaticky.";
  elements["auto-save"].disabled = !state || !profiles.canSaveCandidate(channel, candidateProposal(), controller.state);
}

function startAuto(direction) {
  if (!canStartAuto()) return false;
  const channel = Number(elements.channel.value);
  const profile = profiles.channels[channel];
  discardProposal();
  autoContext = { channel, profileSignature: JSON.stringify(profile) };
  elements["noise-warning"].hidden = true;
  return autoCalibration.start({ channel, direction, centerUs: profile.center });
}

function manualAction(action) {
  if (autoCalibration?.state.active) return false;
  discardProposal();
  return action();
}

function stopAll(reason = "Stop obsluhy") {
  autoCalibration?.cancel(reason, { sendStop: false });
  discardProposal();
  return controller.stop();
}

function noiseStop() {
  elements["noise-warning"].hidden = false;
  if (autoCalibration?.state.active) return autoCalibration.markNoise();
  return stopAll("Drhne / vrčí · vypněte V+");
}

async function connectUsb(authorizedOnly = false) {
  if (autoCalibration?.state.active || !supported || connecting || closing || controller.state.connected) return false;
  discardProposal();
  connecting = true;
  controller.changed();
  try {
    await transport.connect({ authorizedOnly });
    if (!transport.writer) throw new Error("USB bylo odpojeno při otevírání");
    log("USB otevřeno, DTR/RTS vypnuto. Odesílám pouze Stop pro ověření výchozího stavu.");
    await controller.connected();
    return true;
  } catch (error) {
    await transport.close();
    controller.disconnected();
    controller.fail(`Připojení se nezdařilo: ${error.message}`);
    return false;
  } finally {
    connecting = false;
    controller.changed();
  }
}

async function disconnectUsb() {
  if (closing || connecting || !controller.state.connected) return false;
  autoCalibration?.cancel("Odpojení USB", { sendStop: false });
  discardProposal();
  // Submit the emergency byte before releasing the port. Close aborts stalled IO;
  // disconnect itself never claims the byte or PWM-off was acknowledged.
  closing = true;
  await flushStopBeforeClose(() => controller.stop());
  controller.disconnected("Odpojuji USB · stav PWM není potvrzen");
  await transport.close();
  closing = false;
  controller.disconnected();
  return true;
}
elements.connect.addEventListener("click", () => connectUsb());
elements.disconnect.addEventListener("click", disconnectUsb);
elements.arm.addEventListener("click", () => { void manualAction(() => controller.arm()); });
elements.pulse.addEventListener("click", () => {
  void manualAction(() => controller.pulse(Number(elements.channel.value), Number(elements["pulse-width"].value)));
});
elements.stop.addEventListener("click", () => { void stopAll(); });
elements.noise.addEventListener("click", noiseStop);
elements.channel.addEventListener("change", () => {
  if (autoCalibration?.state.active) { elements.channel.value = String(autoCalibration.state.channel); return; }
  discardProposal();
  renderCalibration(controller.state, controller.capabilities);
  renderAuto();
});
elements["pulse-width"].addEventListener("change", () => {
  if (!autoCalibration?.state.active) discardProposal();
});
const editWidth = value => {
  if (autoCalibration?.state.active) return false;
  discardProposal();
  elements["cal-width"].value = String(value);
  if (Number.isInteger(value) && value >= 1000 && value <= 2000) elements["cal-range"].value = String(value);
  renderCalibration(controller.state, controller.capabilities);
};
elements["cal-width"].addEventListener("input", () => {
  if (autoCalibration?.state.active) return;
  discardProposal();
  const width = calibrationValues().width;
  if (Number.isInteger(width) && width >= 1000 && width <= 2000) elements["cal-range"].value = String(width);
  renderCalibration(controller.state, controller.capabilities);
});
elements["cal-range"].addEventListener("input", () => editWidth(Number(elements["cal-range"].value)));
elements["cal-duration"].addEventListener("input", () => {
  if (autoCalibration?.state.active) return;
  discardProposal();
  renderCalibration(controller.state, controller.capabilities);
});
for (const value of [1200, 1500, 1800]) elements[`cal-${value}`].addEventListener("click", () => editWidth(value));
for (const [id, step] of [["cal-minus", -25], ["cal-plus", 25]]) {
  elements[id].addEventListener("click", () => editWidth(Math.max(1000, Math.min(2000, calibrationValues().width + step))));
}
elements["cal-arm"].addEventListener("click", () => { void manualAction(() => controller.arm()); });
elements["cal-move"].addEventListener("click", () => {
  const values = calibrationValues();
  if (values.valid) void manualAction(() => controller.calibrate(Number(elements.channel.value), values.width, values.durationMs));
});
for (const point of ["min", "center", "max"]) {
  elements[`profile-${point}`].addEventListener("click", () => updateProfile(() =>
    profiles.savePoint(Number(elements.channel.value), point, calibrationValues().width, controller.state)));
}
elements["profile-save-label"].addEventListener("click", () => updateProfile(() =>
  profiles.saveLabel(Number(elements.channel.value), elements["profile-label"].value)));
elements["profile-clear"].addEventListener("click", () => updateProfile(() => {
  profiles.clear(Number(elements.channel.value));
  selectedProfileChannel = null;
}, "Ruční profil vybraného kanálu vymazán. Ostatní kanály zůstaly zachovány. Žádný povel nebyl odeslán."));
elements["profile-export"].addEventListener("click", () => { elements["profile-json"].value = profiles.exportJson(); });
elements["profile-label"].addEventListener("input", () => { if (!autoCalibration?.state.active) discardProposal(); });
elements["auto-min"].addEventListener("click", () => startAuto("min"));
elements["auto-max"].addEventListener("click", () => startAuto("max"));
elements["auto-save"].addEventListener("click", () => updateProfile(() => {
  profiles.saveCandidate(Number(elements.channel.value), candidateProposal(), controller.state);
  discardProposal("Návrh uložen po potvrzení obsluhou; další směr se sám nespustí.");
}, "Navržená mez uložena po potvrzení obsluhou. Střed zůstal zachován; poloha není měřena.", true));
function stopForVisibility() {
  if (visibilityStopped) return;
  visibilityStopped = true;
  autoCalibration?.cancel("Stránka byla skryta; kroky se samy neobnoví", { sendStop: false });
  discardProposal();
  // Hidden-page cleanup is automatic: do not retry failed firmware I2C or an
  // emergency write already pending. Explicit operator Stop remains available.
  if (controller.state.connected && !controller.state.fault && controller.state.pending !== "stop") {
    void controller.stop();
  }
}
document.addEventListener?.("visibilitychange", () => {
  if (document.visibilityState === "hidden") stopForVisibility();
  else visibilityStopped = false;
});
window.addEventListener?.("pagehide", stopForVisibility);
if (!supported) {
  elements.support.textContent = "Web Serial není dostupné. Otevřete tuto localhost adresu v desktopovém Chrome nebo Edge s podporou Web Serial. Panel nemůže ovládat USB v tomto prohlížeči.";
}
controller.changed();

void registerBenchTools({
  modelContext: document.modelContext,
  getState: getSnapshot,
  getLog: () => [...logLines],
  subscribe: listener => { stateListeners.add(listener); return () => stateListeners.delete(listener); },
  connect: () => connectUsb(true), disconnect: disconnectUsb,
  arm: () => manualAction(() => controller.arm()), pulse: (channel, width) => manualAction(() => controller.pulse(channel, width)),
  calibrate: (channel, width, durationMs) => manualAction(() => controller.calibrate(channel, width, durationMs)),
  stop: () => stopAll("Stop přes WebMCP"),
}).then(result => {
  elements.mcp.textContent = result.registered ?
    `WebMCP: dostupných nástrojů ${result.names.length}. Povely používají stejné ovládání a firmware jako tlačítka.` :
    result.supported ? `WebMCP se nepodařilo připravit: ${result.error}` :
      "WebMCP není v tomto prohlížeči dostupné. Ruční ovládání zůstává dostupné.";
}).catch(error => { elements.mcp.textContent = `WebMCP není dostupné: ${error.message}`; });
