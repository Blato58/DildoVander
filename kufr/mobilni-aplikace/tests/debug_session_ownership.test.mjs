import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { stripTypeScriptTypes } from 'node:module';
import { test } from 'node:test';

async function moduleUrl(name) {
  const source = await readFile(new URL(`../src/features/debug/${name}.ts`, import.meta.url), 'utf8');
  return `data:text/javascript;base64,${Buffer.from(stripTypeScriptTypes(source, { mode: 'transform' })).toString('base64')}`;
}
const source = await readFile(new URL('../src/features/debug/DebugPanel.ts', import.meta.url), 'utf8');
const code = stripTypeScriptTypes(source, { mode: 'transform' })
  .replace(/'\.\.\/\.\.\/\.\.\/\.\.\/design-system\/ui\.js'/, JSON.stringify(new URL('../../design-system/ui.js', import.meta.url).href))
  .replace(/import \{ nativeDebugBridge \} from '\.\/NativeDebugBridge';/, 'const nativeDebugBridge = () => ({});')
  .replace(/'\.\/DebugJournal'/, JSON.stringify(await moduleUrl('DebugJournal')))
  .replace(/'\.\/ServerLogs'/, JSON.stringify(await moduleUrl('ServerLogs')));
const { DebugPanel, bridge } = await import(`data:text/javascript;base64,${Buffer.from(code + '\nexport { bridge };').toString('base64')}`);

function deferred() {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
function setup(context) {
  const previousDocument = globalThis.document;
  globalThis.document = { hidden: false };
  context.after(() => { globalThis.document = previousDocument; });
  const elements = new Map();
  const find = selector => {
    if (!elements.has(selector)) elements.set(selector, { value: '', textContent: '', hidden: true });
    return elements.get(selector);
  };
  const panel = Object.create(DebugPanel.prototype);
  Object.assign(panel, { native: true, pending: false, cancellationPending: false,
    actionGeneration: 0, statusSequence: 0, photoSequence: 0,
    voice: { configured: true, running: false, session: true, phase: 'awaiting', mode: 'fake' },
    element: { querySelector: find, querySelectorAll: () => [] },
    journal: { record() {} }, renderVoice() {}, renderConversation() {}, renderLog() {},
    async refreshVoiceAsync() {} });
  bridge.stop = async () => {};
  bridge.status = async () => ({ phase: 'completed' });
  return { panel, find };
}

test('late answer from previous guest cannot clear a new draft or unlock a newer operation', async context => {
  const { panel, find } = setup(context);
  const oldAnswer = deferred();
  const newOpening = deferred();
  bridge.answerConversation = () => oldAnswer.promise;
  bridge.startConversation = () => newOpening.promise;
  find('[data-conversation-answer]').value = 'První host';
  const oldAction = panel.actionAsync('conversation-send');
  await panel.actionAsync('conversation-stop');
  panel.voice = { configured: true, running: false, session: false, phase: 'idle', mode: 'fake' };
  find('[data-conversation-answer]').value = 'Nový host – rozepsaná odpověď';
  find('[data-conversation-voice]').value = 'cedar';
  find('[data-conversation-speed]').value = '1';
  find('[data-conversation-engine]').value = 'chained';
  const nextAction = panel.actionAsync('conversation-fake');
  assert.equal(panel.pending, true);
  oldAnswer.resolve();
  await oldAction;
  assert.equal(panel.pending, true);
  assert.equal(find('[data-conversation-answer]').value, 'Nový host – rozepsaná odpověď');
  newOpening.resolve();
  await nextAction;
  assert.equal(panel.pending, false);
});

test('cancel remains serialized while an old answer finishes', async context => {
  const { panel, find } = setup(context);
  const answer = deferred(), stop = deferred();
  bridge.answerConversation = () => answer.promise;
  let stops = 0;
  bridge.stop = () => { stops++; return stop.promise; };
  find('[data-conversation-answer]').value = 'Host';
  const oldAction = panel.actionAsync('conversation-send');
  const cancellation = panel.actionAsync('conversation-stop');
  await panel.actionAsync('conversation-stop');
  answer.resolve();
  await oldAction;
  assert.equal(panel.pending, true);
  assert.equal(panel.cancellationPending, true);
  assert.equal(stops, 1);
  stop.resolve();
  await cancellation;
  assert.equal(panel.pending, false);
  assert.equal(panel.cancellationPending, false);
});

test('a stale failure status cannot replace next guest feedback after cancellation', async context => {
  const { panel, find } = setup(context);
  const status = deferred(), current = deferred();
  bridge.speakSystem = async () => { throw new Error('old operation'); };
  bridge.status = () => status.promise;
  const oldAction = panel.actionAsync('system');
  await Promise.resolve();
  await panel.actionAsync('conversation-stop');
  bridge.speakSystem = () => current.promise;
  const nextAction = panel.actionAsync('system');
  status.resolve({ phase: 'failed' });
  await oldAction;
  assert.equal(find('[data-voice-error]').hidden, true);
  assert.equal(panel.pending, true);
  current.resolve();
  // Normal successful system tests read their final status for the journal.
  bridge.status = async () => ({ phase: 'completed' });
  await nextAction;
});

test('background invalidation discards a captured photo and keeps newer work guarded', async context => {
  const { panel } = setup(context);
  const photo = deferred();
  bridge.captureCostumePhoto = () => photo.promise;
  const oldAction = panel.actionAsync('costume-photo');
  panel.actionGeneration++;
  panel.photoSequence++;
  panel.pending = true; // A newer foreground action owns the busy state.
  globalThis.document.hidden = true;
  photo.resolve({ draftId: 'old-guest-photo' });
  await oldAction;
  assert.equal(panel.photoDraftId, undefined);
  assert.equal(panel.pending, true);
});
