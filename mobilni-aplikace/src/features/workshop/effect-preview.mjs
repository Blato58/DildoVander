/** Local illustrative timing only; this module has no transport or device command. */
export function validateEffect(phases) {
  if (!Array.isArray(phases) || !phases.length || phases.length > 128) throw new Error('Efekt nemá platné fáze.');
  let duration = 0;
  for (const phase of phases) {
    if (!phase || typeof phase.phase !== 'string' || !Number.isFinite(phase.duration_ms) || phase.duration_ms < 1 || phase.duration_ms > 15000) throw new Error('Neplatné časování efektu.');
    duration += phase.duration_ms;
    if (duration > 15000 || phase.motion && !['raise', 'lower'].includes(phase.motion)) throw new Error('Efekt překračuje technické meze.');
    if (phase.light && (!Array.isArray(phase.light) || phase.light.length !== 3 || !phase.light.every(value => Number.isFinite(value) && value >= 0 && value <= 255))) throw new Error('Neplatná barva efektu.');
    if (phase.tone_hz !== undefined && (!Number.isFinite(phase.tone_hz) || phase.tone_hz < 0 || phase.tone_hz > 24000)) throw new Error('Neplatný tón efektu.');
  }
  return phases;
}
export function targetIndexes(target, selected, fraction, count) {
  if (typeof target === 'number') return [target - 1];
  if (target === 'all') return Array.from({ length: count }, (_, index) => index);
  if (target === 'next') return [(selected + 1) % count];
  if (target === 'previous') return [(selected + count - 1) % count];
  if (target === 'scan') return [Math.min(count - 1, Math.floor(fraction * count))];
  return [selected];
}
/** @returns {{angles:number[],ledColors:Record<string,number[]>,outputs:Record<string,string>}} */
export function previewFrame(model, phase, initial, selected, fraction) {
  const angles = [...initial], ledColors = {}, outputs = {};
  for (const index of targetIndexes(phase.target, selected, fraction, model.mechanism.count)) {
    if (index < 0 || index >= angles.length) continue;
    ledColors[`led_${index + 1}`] = (phase.light || [0, 0, 0]).map(value => value * Math.min(64, model.effects.max_brightness || 64) / (255 * 255));
    if (phase.vibration) outputs[`u059_${index + 1}`] = 'vibration-commanded';
    if (phase.motion) angles[index] += ((phase.motion === 'raise' ? model.mechanism.travel_degrees : 0) - initial[index]) * fraction;
  }
  return { angles, ledColors, outputs };
}
