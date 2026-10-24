import { ref } from 'vue';
import { account, accountTransport } from '../identity/session';
import { secureUuid } from '../../application/identifiers.mjs';
import { GuestError, isUuid } from './client';
export interface WandArtwork { locator: string; assetId: string | null; status: 'ready' | 'queued' | 'running' | 'failed' | 'missing'; canRetry: boolean; version: 1; synthetic: boolean }
/** Artwork references are identifiers, never bearer URLs or prompts. */
export function projectWandArtwork(raw: unknown): WandArtwork | null {
  if (!raw || typeof raw !== 'object') return null;
  const value = raw as Record<string, unknown>;
  if (!isUuid(value.locator) || value.assetId != null && !isUuid(value.assetId)
    || !['ready', 'queued', 'running', 'failed', 'missing'].includes(String(value.status)) || value.version !== 1) return null;
  const assetId = isUuid(value.assetId) ? value.assetId : null;
  if (value.status === 'ready' && !assetId) return null;
  return { locator: value.locator, assetId, status: value.status as WandArtwork['status'], canRetry: !assetId && ['failed', 'missing'].includes(String(value.status)) && value.canRetry === true, version: 1, synthetic: value.synthetic === true };
}
export function useWandArtworkRetry() {
  const busy = ref(false); const failed = ref(false);
  let operation: { accountId: string; locator: string; key: string } | undefined;
  let generation = 0;
  function invalidate() { generation++; operation = undefined; busy.value = false; failed.value = false; }
  async function retry(artwork: WandArtwork | null | undefined, refresh: () => Promise<void>) {
    const accountId = account.value?.id;
    if (!artwork?.canRetry || !accountId || busy.value) return;
    const current = ++generation; busy.value = true; failed.value = false;
    if (operation?.accountId !== accountId || operation.locator !== artwork.locator) operation = { accountId, locator: artwork.locator, key: secureUuid() };
    try {
      const response = await accountTransport(`/app/v1/customer/keepsakes/${artwork.locator}/wand-artwork/retry`, { method: 'POST', headers: { 'Content-Type': 'application/json', 'Idempotency-Key': operation.key }, body: '{}', redirect: 'error' });
      if (account.value?.id !== accountId || current !== generation) return;
      if (!response.ok) throw new GuestError(response.status);
      operation = undefined; await refresh();
    } catch { if (account.value?.id === accountId && current === generation) failed.value = true; }
    finally { if (current === generation) busy.value = false; }
  }
  return { busy, failed, retry, invalidate };
}
