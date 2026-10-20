import assert from "node:assert/strict";
import test from "node:test";
import { ManualProfiles, PROFILE_STORAGE_KEY, canSaveTarget } from "../profiles.mjs";

function storage(initial) {
  const entries = new Map(initial ? [[PROFILE_STORAGE_KEY, initial]] : []);
  return { getItem: key => entries.get(key) ?? null, setItem: (key, value) => entries.set(key, value) };
}
const completed = (channel, pulseWidthUs) => ({ connected: true, baseline: true, fault: false, pending: null, calibrationSupported: true,
  writing: false, lastCompleted: { channel, pulseWidthUs, durationMs: 1500, calibration: true } });

test("manual profiles begin unknown, persist by channel and export explicit evidence limits", () => {
  const store = storage();
  const profiles = new ManualProfiles(store);
  assert.deepEqual(profiles.channels[0], { label: "", min: null, center: null, max: null });
  profiles.saveLabel(0, "  Servo levé  ");
  for (const [point, width] of [["min", 1200], ["center", 1500], ["max", 1800]]) {
    profiles.savePoint(0, point, width, completed(0, width));
  }
  const restored = new ManualProfiles(store);
  assert.deepEqual(restored.channels[0], { label: "Servo levé", min: 1200, center: 1500, max: 1800 });
  assert.equal(restored.channels[1].min, null);
  const exported = JSON.parse(restored.exportJson());
  assert.equal(exported.positionMeasured, false);
  assert.equal(exported.productionCalibration, false);
  assert.equal(exported.kind, "manual-servo-bench");
});

test("partial profiles are allowed but all known points must keep their order", () => {
  const profiles = new ManualProfiles(storage());
  profiles.savePoint(1, "max", 1800, completed(1, 1800));
  assert.equal(profiles.channels[1].center, null);
  assert.throws(() => profiles.savePoint(1, "min", 1900, completed(1, 1900)), /min < střed < max/);
  profiles.savePoint(1, "min", 1200, completed(1, 1200));
  assert.throws(() => profiles.savePoint(1, "center", 1200, completed(1, 1200)), /min < střed < max/);
  assert.equal(profiles.channels[1].center, null);
});

test("untested, changed-channel, interrupted and stale values cannot be saved", () => {
  const profiles = new ManualProfiles(storage());
  const state = completed(0, 1500);
  assert.equal(canSaveTarget(state, 0, 1500), true);
  for (const [channel, width, current] of [
    [1, 1500, state], [0, 1525, state], [0, 1500, { ...state, lastCompleted: null }],
    [0, 1500, { ...state, pending: "pulse" }], [0, 1500, { ...state, writing: true }],
    [0, 1500, { ...state, fault: true }], [0, 1500, { ...state, baseline: false }],
    [0, 1500, { ...state, connected: false }],
    [0, 1500, { ...state, calibrationSupported: false }],
    [0, 1500, { ...state, lastCompleted: { ...state.lastCompleted, calibration: false } }],
  ]) assert.throws(() => profiles.savePoint(channel, "center", width, current), /poslední dokončený pulz/);
});

test("malformed, out-of-range or unordered stored profiles never become known calibration", () => {
  const valid = JSON.parse(new ManualProfiles(storage()).exportJson());
  const invalidProfiles = ["{", "null", "{}", JSON.stringify({ ...valid, version: 2 }),
    JSON.stringify({ ...valid, channels: [] }),
    JSON.stringify({ ...valid, channels: valid.channels.map(profile => ({ ...profile, center: "1500" })) }),
    JSON.stringify({ ...valid, channels: valid.channels.map(profile => ({ ...profile, min: 1800, max: 1200 })) }),
  ];
  for (const value of invalidProfiles) {
    const profiles = new ManualProfiles(storage(value));
    assert.equal(profiles.channels.every(profile => profile.min === null && profile.center === null && profile.max === null), true);
    assert.notEqual(profiles.error, "");
  }
});

test("storage failure is visible and leaves the saved profile unchanged", () => {
  const profiles = new ManualProfiles({ getItem: () => null, setItem: () => { throw new Error("quota"); } });
  assert.throws(() => profiles.savePoint(0, "center", 1500, completed(0, 1500)), /zápis odmítlo/);
  assert.equal(profiles.channels[0].center, null);
  assert.throws(() => new ManualProfiles(null).saveLabel(0, "Servo"), /úložiště není dostupné/);
});

test("clearing one channel resets its manual label and all points while preserving other channels", () => {
  const store = storage();
  const profiles = new ManualProfiles(store);
  for (const channel of [0, 1]) {
    profiles.saveLabel(channel, `Servo ${channel}`);
    profiles.savePoint(channel, "center", 1500, completed(channel, 1500));
  }
  profiles.clear(0);
  const restored = new ManualProfiles(store);
  assert.deepEqual(restored.channels[0], { label: "", min: null, center: null, max: null });
  assert.deepEqual(restored.channels[1], { label: "Servo 1", min: null, center: 1500, max: null });
});

function assistedProposal(profiles) {
  return { active: false, channel: 0, direction: "min", centerUs: 1500, currentUs: 1400,
    previousUs: 1425, candidateUs: 1425, stopConfirmed: true, reason: "noise",
    profileSignature: JSON.stringify(profiles.channels[0]),
    candidateEvidence: { channel: 0, direction: "min", centerUs: 1500, pulseWidthUs: 1425, completed: true } };
}

test("assisted proposal is saved only after fresh Stop proof and explicit approval, preserving center", () => {
  const profiles = new ManualProfiles(storage());
  profiles.savePoint(0, "center", 1500, completed(0, 1500));
  const proposal = assistedProposal(profiles);
  const state = { ...completed(0, 1500), armedUntil: 0, error: "", lastCompleted: null };
  assert.equal(profiles.channels[0].min, null);
  assert.equal(profiles.canSaveCandidate(0, proposal, state), true);
  assert.equal(profiles.channels[0].min, null);
  profiles.saveCandidate(0, proposal, state);
  assert.equal(profiles.channels[0].min, 1425);
  assert.equal(profiles.channels[0].center, 1500);
  assert.equal(profiles.channels[0].max, null);
});

test("assisted proposal rejects missing fresh ACK, incomplete or wrong proof and changed profile/context", () => {
  const profiles = new ManualProfiles(storage());
  profiles.savePoint(0, "center", 1500, completed(0, 1500));
  const proposal = assistedProposal(profiles);
  const state = { ...completed(0, 1500), armedUntil: 0, error: "", lastCompleted: null };
  for (const invalid of [null, { ...proposal, active: true }, { ...proposal, stopConfirmed: false },
    { ...proposal, centerUs: 1525 }, { ...proposal, channel: 1 }, { ...proposal, direction: "max" },
    { ...proposal, profileSignature: "old" }, { ...proposal, candidateUs: 2001 },
    { ...proposal, previousUs: 1400 }, { ...proposal, currentUs: 1425 }, { ...proposal, reason: "manual" },
    { ...proposal, candidateEvidence: null },
    { ...proposal, candidateEvidence: { ...proposal.candidateEvidence, completed: false } },
    { ...proposal, candidateEvidence: { ...proposal.candidateEvidence, pulseWidthUs: 1400 } },
  ]) assert.throws(() => profiles.saveCandidate(0, invalid, state), /Návrh nelze uložit/);
  for (const change of [{ connected: false }, { baseline: false }, { fault: true }, { error: "fault" },
    { pending: "stop" }, { writing: true }, { armedUntil: 999999 }, { calibrationSupported: false }]) {
    assert.throws(() => profiles.saveCandidate(0, proposal, { ...state, ...change }), /Návrh nelze uložit/);
  }
  profiles.saveLabel(0, "Vyměněné servo");
  assert.throws(() => profiles.saveCandidate(0, proposal, state), /Návrh nelze uložit/);
  assert.equal(profiles.channels[0].min, null);
});

test("normal soft-bound proposal requires the actually completed endpoint and preserves manual center", () => {
  const profiles = new ManualProfiles(storage());
  profiles.savePoint(0, "center", 1500, completed(0, 1500));
  const proposal = { ...assistedProposal(profiles), reason: "bound", currentUs: 1000, previousUs: 1025, candidateUs: 1000 };
  proposal.candidateEvidence = { ...proposal.candidateEvidence, pulseWidthUs: 1000 };
  const state = { ...completed(0, 1500), armedUntil: 0, error: "", lastCompleted: null };
  assert.equal(profiles.canSaveCandidate(0, proposal, state), true);
  assert.equal(profiles.canSaveCandidate(0, { ...proposal, currentUs: 1025 }, state), false);
  profiles.saveCandidate(0, proposal, state);
  assert.equal(profiles.channels[0].min, 1000);
  assert.equal(profiles.channels[0].center, 1500);
});
