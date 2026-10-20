export const PROFILE_STORAGE_KEY = "dildovander.servo-bench.manual-profiles.v1";
const POINTS = ["min", "center", "max"];
const emptyChannels = () => Array.from({ length: 6 }, () => ({ label: "", min: null, center: null, max: null }));
const validWidth = value => value === null || (Number.isInteger(value) && value >= 1 && value <= 9999);

export function validProfile(profile) {
  if (!profile || typeof profile.label !== "string" || profile.label.length > 80 ||
    !POINTS.every(key => validWidth(profile[key]))) return false;
  return [["min", "center"], ["center", "max"], ["min", "max"]].every(([lower, upper]) =>
    profile[lower] === null || profile[upper] === null || profile[lower] < profile[upper]);
}

export function canSaveTarget(state, channel, width) {
  const completed = state.lastCompleted;
  return !!completed && completed.calibration === true && state.calibrationSupported === true &&
    state.connected && state.baseline && !state.fault && !state.pending && !state.writing &&
    completed.channel === channel && completed.pulseWidthUs === width;
}

export class ManualProfiles {
  constructor(storage) {
    this.storage = storage;
    this.channels = emptyChannels();
    this.error = "";
    try {
      const serialized = storage?.getItem(PROFILE_STORAGE_KEY);
      if (serialized) {
        const parsed = JSON.parse(serialized);
        if (parsed.version !== 1 || !Array.isArray(parsed.channels) || parsed.channels.length !== 6 ||
          !parsed.channels.every(validProfile)) throw new Error("Neplatný formát ručních profilů");
        this.channels = parsed.channels.map(profile => ({ label: profile.label, min: profile.min,
          center: profile.center, max: profile.max }));
      }
    } catch {
      this.error = "Uložené profily nelze načíst. Zobrazeny prázdné ruční profily; neplatná data nebyla použita.";
    }
  }

  update(channel, change) {
    if (!Number.isInteger(channel) || channel < 0 || channel > 5) throw new Error("Neplatný kanál");
    const next = structuredClone(this.channels);
    Object.assign(next[channel], change);
    if (!validProfile(next[channel])) throw new Error("Hodnoty musí splnit min < střed < max; zadané body se nesmí rovnat ani křížit.");
    if (!this.storage) throw new Error("Místní úložiště není dostupné; profil nebyl uložen.");
    try { this.storage.setItem(PROFILE_STORAGE_KEY, JSON.stringify({ version: 1, channels: next })); }
    catch { throw new Error("Místní úložiště zápis odmítlo; profil nebyl uložen."); }
    this.channels = next;
    this.error = "";
  }

  savePoint(channel, point, width, state) {
    if (!POINTS.includes(point) || !canSaveTarget(state, channel, width)) {
      throw new Error("Uložit lze pouze poslední dokončený pulz pro aktuální kanál a hodnotu; Stop, chyba či restart pokus zneplatní.");
    }
    this.update(channel, { [point]: width });
  }

  saveLabel(channel, label) {
    if (typeof label !== "string" || label.trim().length > 80) throw new Error("Název má nejvýše 80 znaků.");
    this.update(channel, { label: label.trim() });
  }

  clear(channel) {
    this.update(channel, { label: "", min: null, center: null, max: null });
  }

  canSaveCandidate(channel, proposal, state) {
    const profile = this.channels[channel];
    const evidence = proposal?.candidateEvidence;
    const candidate = proposal?.candidateUs;
    const direction = proposal?.direction;
    const minimumUs = Number.isInteger(state.calibrationMinimumUs) ? state.calibrationMinimumUs : 1000;
    const maximumUs = Number.isInteger(state.calibrationMaximumUs) ? state.calibrationMaximumUs : 3000;
    const idle = state.connected && state.baseline && !state.fault && !state.error && !state.pending &&
      !state.writing && !state.armedUntil && state.calibrationSupported === true;
    if (!profile || !proposal || !Number.isInteger(profile.center) || !idle || proposal.active || proposal.stopConfirmed !== true || proposal.channel !== channel ||
      !Number.isInteger(candidate) || candidate < minimumUs ||
      candidate > maximumUs || !["min", "max"].includes(direction) ||
      proposal.centerUs !== profile.center || proposal.profileSignature !== JSON.stringify(profile) ||
      !evidence || evidence.completed !== true || evidence.channel !== channel || evidence.pulseWidthUs !== candidate ||
      evidence.centerUs !== profile.center || evidence.direction !== direction ||
      !["noise", "bound"].includes(proposal.reason) ||
      (proposal.reason === "noise" && (candidate !== proposal.previousUs || candidate === proposal.currentUs)) ||
      (proposal.reason === "bound" && (candidate !== proposal.currentUs ||
        candidate !== (direction === "min" ? minimumUs : maximumUs))) ||
      (direction === "min" ? candidate >= profile.center : candidate <= profile.center)) {
      return false;
    }
    return true;
  }

  saveCandidate(channel, proposal, state) {
    if (!this.canSaveCandidate(channel, proposal, state)) {
      throw new Error("Návrh nelze uložit: vyžaduje dokončený předchozí krok, nové potvrzení Stop a stejný kanál i nezměněný profil se středem.");
    }
    this.update(channel, { [proposal.direction]: proposal.candidateUs });
  }

  exportJson() {
    return JSON.stringify({ version: 1, kind: "manual-servo-bench", positionMeasured: false,
      productionCalibration: false, channels: this.channels }, null, 2);
  }
}
