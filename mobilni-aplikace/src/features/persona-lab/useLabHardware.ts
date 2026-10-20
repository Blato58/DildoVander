import { computed, ref, watch, type Ref, type ShallowRef } from 'vue';
import { deviceSession } from '../suitcase/deviceSession';
import { deviceAssociation } from '../suitcase/deviceAssociation';
import { LabError, PersonaLabClient } from './client';
import { hardwareContextId, hardwareEligibility, sameHardwareConnection, type LabHardwareTarget } from './hardware';
import type { Catalog, LabSession } from './contracts';

interface Options {
  selected: ShallowRef<LabSession | null>; catalog: ShallowRef<Catalog | null>; busy: Ref<boolean>;
  identity(): string; ownerId(): string; isolatedBusy(): boolean; remember(session: LabSession): void;
  failure(cause: unknown): void; notice(message: string): void;
}
interface OwnedHardwareLease { id: string; owner: string; ownerAccount: string; target: LabHardwareTarget; testContextId: string | null }
export function useLabHardware(options: Options) {
  const mode = ref(false); const confirmed = ref(false); const pending = ref(false); const uncertain = ref(false);
  const client = new PersonaLabClient(options.identity);
  if (deviceSession.native && options.ownerId()) void deviceAssociation.refresh().catch(() => {});
  let epoch = 0; let timer: ReturnType<typeof setTimeout> | null = null; let polls = 0;
  let lease: OwnedHardwareLease | null = null;
  const bound = computed(() => !!options.selected.value?.hardware || uncertain.value);
  const active = computed(() => pending.value || ['prepared', 'active'].includes(options.selected.value?.hardware?.status ?? '')
    || uncertain.value && options.selected.value?.status === 'open' && !['ended', 'failed'].includes(options.selected.value.hardware?.status ?? ''));
  const inspect = () => hardwareEligibility(options.selected.value, deviceSession.snapshot.value,
    deviceSession.native, deviceSession.savedDevice.value.paired ? deviceSession.savedDevice.value.deviceId : undefined,
    deviceAssociation.canOperate.value, options.catalog.value?.capabilities.hardwareContext === true, deviceSession.labConnectionEpoch());
  const eligibility = computed(inspect);
  const targetKey = () => {
    const snapshot = deviceSession.snapshot.value;
    return `${snapshot?.source}:${snapshot?.diagnostics?.deviceId}:${snapshot?.diagnostics?.bootId}:${snapshot?.revision}:${deviceAssociation.canOperate.value}:${deviceSession.labConnectionEpoch()}`;
  };
  function stopTimer(): void { if (timer !== null) clearTimeout(timer); timer = null; }
  function stopLocal(): boolean {
    const owned = lease;
    if (!owned?.testContextId || deviceSession.snapshot.value?.voice?.testContextId !== owned.testContextId) return false;
    const target = owned.target;
    return deviceSession.stopLabVoice({ deviceId: target.deviceId, bootId: target.bootId,
      connectionEpoch: target.connectionEpoch, testContextId: owned.testContextId });
  }
  // This cleanup has its own transport lifetime. Cancelling the screen's pending
  // request must not immediately cancel the compensating lease revocation too.
  function detachedEnd(previous: OwnedHardwareLease): void {
    if (previous.ownerAccount !== options.ownerId()) return;
    const cleanup = new PersonaLabClient(options.identity);
    void cleanup.end(previous.id).catch(() => {}).finally(() => cleanup.invalidate());
  }
  function clear(revoke = true, stop = true): void {
    epoch++; stopTimer(); client.invalidate();
    const previous = lease;
    if (previous) { if (stop) stopLocal(); lease = null; if (revoke) detachedEnd(previous); }
    pending.value = false; confirmed.value = false; mode.value = false; uncertain.value = false;
  }
  async function revoke(): Promise<void> {
    epoch++; stopTimer(); client.invalidate(); pending.value = false; confirmed.value = false; stopLocal();
    const previous = lease; lease = null;
    if (!previous || previous.ownerAccount !== options.ownerId()) return;
    const cleanup = new PersonaLabClient(options.identity);
    try { const ended = await cleanup.end(previous.id); if (previous.owner === options.identity()) options.remember(ended); }
    finally { cleanup.invalidate(); }
  }
  function schedule(current: number): void {
    stopTimer(); timer = setTimeout(() => { timer = null; void poll(current); }, 1000);
  }
  async function poll(current: number): Promise<void> {
    const previous = lease;
    if (!previous || current !== epoch || previous.owner !== options.identity() || options.selected.value?.id !== previous.id) return;
    const expires = Date.parse(options.selected.value.hardware?.expiresAt ?? '');
    if (++polls > 300 || !sameHardwareConnection(deviceSession.snapshot.value, previous.target, deviceSession.labConnectionEpoch())
      || !Number.isFinite(expires) || Date.now() >= expires) {
      try { await revoke(); } catch (cause) { options.failure(cause); }
      options.notice('Pozorování testu bylo ukončené. Požadavek na ukončení backendu ani místní Stop neprokazují fyzické zastavení kufru.'); return;
    }
    try {
      const session = await client.session(previous.id);
      if (current !== epoch || previous.owner !== options.identity() || options.selected.value?.id !== previous.id) return;
      if (!sameHardwareConnection(deviceSession.snapshot.value, previous.target, deviceSession.labConnectionEpoch())) { await revoke(); return; }
      if (session.id !== previous.id || session.ownerId !== options.ownerId() || !session.hardware
        || session.hardware.deviceId !== previous.target.deviceId || session.hardware.bootId !== previous.target.bootId
        || session.hardware.expectedRevision !== previous.target.revision) throw new LabError('lab_hardware_target_changed', 409);
      options.remember(session);
      if (['prepared', 'active'].includes(session.hardware?.status ?? '')) schedule(current);
      else { stopLocal(); lease = null; stopTimer(); }
    } catch (cause) {
      if (current !== epoch) return;
      options.failure(cause);
      try { await revoke(); } catch { /* Revocation is unconfirmed; no automatic restart. */ }
      options.notice('Stav skutečného testu není potvrzený. Nový pokus se nespustí automaticky; fyzický stav ověřte na kufru.');
    }
  }
  async function start(): Promise<void> {
    const session = options.selected.value; const admitted = inspect();
    if (!mode.value || !confirmed.value || !session || !admitted.ready || !admitted.target || bound.value
      || pending.value || options.busy.value || options.isolatedBusy()) return;
    const current = ++epoch; const target = { ...admitted.target }; const owner = options.identity();
    confirmed.value = false; pending.value = true; uncertain.value = true; lease = { id: session.id, target, owner, ownerAccount: options.ownerId(), testContextId: null }; polls = 0;
    try {
      // The capability exists only in this local variable and the carrier call.
      const ticket = await client.hardwareTicket(session.id, { deviceId: target.deviceId, bootId: target.bootId, expectedRevision: target.revision });
      if (current !== epoch || owner !== options.identity() || options.selected.value?.id !== session.id) return;
      const fresh = inspect();
      const expires = Date.parse(ticket.expiresAt);
      if (!fresh.ready || !fresh.target || JSON.stringify(fresh.target) !== JSON.stringify(target)
        || !/^[A-Za-z0-9_-]{43}$/.test(ticket.testContext) || !Number.isFinite(expires) || Date.now() >= expires)
        throw new LabError('lab_hardware_target_changed', 409);
      const testContextId = await hardwareContextId(ticket.testContext);
      if (current !== epoch || owner !== options.identity() || options.selected.value?.id !== session.id) return;
      const afterHash = inspect();
      if (!afterHash.ready || !afterHash.target || JSON.stringify(afterHash.target) !== JSON.stringify(target) || Date.now() >= expires)
        throw new LabError('lab_hardware_target_changed', 409);
      const detail = await client.session(session.id);
      if (current !== epoch || owner !== options.identity() || options.selected.value?.id !== session.id) return;
      const beforeSubmit = inspect();
      if (!beforeSubmit.ready || !beforeSubmit.target || JSON.stringify(beforeSubmit.target) !== JSON.stringify(target) || Date.now() >= expires)
        throw new LabError('lab_hardware_target_changed', 409);
      if (detail.id !== session.id || detail.ownerId !== session.ownerId || !detail.hardware || detail.hardware.deviceId !== target.deviceId || detail.hardware.bootId !== target.bootId
        || detail.hardware.expectedRevision !== target.revision || detail.hardware.status !== 'prepared')
        throw new LabError('lab_hardware_start_unconfirmed', 409);
      options.remember(detail);
      if (!deviceSession.startLabVoice(false, ticket.testContext, target)) throw new LabError('lab_hardware_start_unconfirmed', 409);
      if (lease?.id === session.id && current === epoch) lease.testContextId = testContextId;
      pending.value = false;
      options.notice('Testovací hlas byl předán původnímu kufru. Čekáme na pozorovaný stav backendu; odeslání není potvrzení fyzického pohybu.');
      schedule(current);
    } catch (cause) {
      if (current !== epoch) return;
      options.failure(cause);
      try { await revoke(); } catch { /* The lease may remain until server expiry. */ }
      options.notice('Start skutečného testu není potvrzený a nebude se opakovat. Bylo vyžádáno ukončení vazby; fyzický stav ověřte na kufru.');
    } finally { if (current === epoch) pending.value = false; }
  }
  function configure(): void {
    const current = inspect();
    if (!mode.value || !confirmed.value || pending.value || bound.value || options.busy.value || options.isolatedBusy()
      || !current.canConfigure || !current.target) return;
    confirmed.value = false;
    const submitted = deviceSession.configureVoice(current.target.mode, current.target.voice);
    options.notice(submitted ? 'Nastavení hlasu bylo odesláno. Vyčkejte na nový snapshot a znovu potvrďte přesný cíl.'
      : 'Změna hlasu nebyla potvrzena. Test se nespustil.');
  }
  watch(targetKey, () => {
    confirmed.value = false;
    if (lease && (!sameHardwareConnection(deviceSession.snapshot.value, lease.target, deviceSession.labConnectionEpoch()) || !deviceAssociation.canOperate.value)) {
      void revoke().catch(options.failure);
      options.notice('Cíl nebo jeho oprávnění se změnily. Test nebude přenesen na nový kufr ani nový boot. Fyzické zastavení není potvrzené.');
    }
  }, { flush: 'sync' });
  watch(() => options.selected.value?.id, () => { clear(); mode.value = !!options.selected.value?.hardware; }, { flush: 'sync' });
  return { mode, confirmed, pending, uncertain, bound, active, eligibility, start, configure, stopLocal, clear, revoke };
}
