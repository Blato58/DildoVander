import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  CustomerDataExportClient,
  CustomerDataExportError,
  customerDataExportLocationMatches,
  customerDataExportMaxDownloadBytes,
  customerDataExportPath,
  exportErrorCopy,
  parseCustomerDataExportJob,
  type CustomerDataExportStorage,
} from './customerDataExport';

const jobId = 'e513a7ce-1f2a-4320-9ca2-463fb179b0f4';
const keyOne = '6d8c7912-48c7-499c-a61a-138d0ca7932d';
const keyTwo = 'b8b228c8-b9d5-43d4-ac5a-8c0c85607d0e';
const requestedAt = '2026-10-23T09:00:00Z';

function dto(overrides: Record<string, unknown> = {}) {
  return {
    schemaVersion: 1,
    id: jobId,
    state: 'requested',
    reason: null,
    requestedAt,
    readyAt: null,
    downloadExpiresAt: null,
    downloadPath: null,
    ...overrides,
  };
}

function readyDto(overrides: Record<string, unknown> = {}) {
  return dto({
    state: 'ready',
    readyAt: '2026-10-23T09:01:00Z',
    downloadExpiresAt: '2026-10-23T10:01:00Z',
    downloadPath: `${customerDataExportPath(jobId)}/download`,
    ...overrides,
  });
}

function jsonResponse(value: unknown, status = 200, headers: Record<string, string> = {}) {
  return new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json', ...headers } });
}

function createStorage(): CustomerDataExportStorage & { values: Map<string, string> } {
  const values = new Map<string, string>();
  return {
    values,
    getItem: key => values.get(key) ?? null,
    setItem: (key, value) => { values.set(key, value); },
  };
}

afterEach(() => vi.useRealTimers());

describe('customer data export contract and client', () => {
  it('preserves pending recovery metadata on runtimes without Object.hasOwn', () => {
    const storage = createStorage();
    storage.setItem('dildovander.customer-data-export.v1.account-1', JSON.stringify({ schemaVersion: 1, jobId, pendingIdempotencyKey: keyOne }));
    const descriptor = Object.getOwnPropertyDescriptor(Object, 'hasOwn')!;
    let restored;
    try {
      Object.defineProperty(Object, 'hasOwn', { configurable: true, value: undefined });
      const client = new CustomerDataExportClient({ currentAccountId: () => 'account-1', transport: vi.fn(), storage });
      client.setAccount('account-1');
      restored = client.recoveryMetadata;
      client.dispose();
    } finally { Object.defineProperty(Object, 'hasOwn', descriptor); }
    expect(restored).toEqual({ jobId, pendingIdempotencyKey: keyOne });
  });
  it('parses the actual camel-case AccountDataExportView DTO and only allowlisted reasons', () => {
    expect(parseCustomerDataExportJob(dto())).toMatchObject({ id: jobId, state: 'requested', requestedAt });
    expect(parseCustomerDataExportJob(readyDto())).toMatchObject({ state: 'ready', downloadPath: `${customerDataExportPath(jobId)}/download` });
    for (const reason of [
      'inventory_unavailable', 'memory_unavailable', 'source_changed', 'payload_too_large',
      'preparation_interrupted', 'download_expired', 'export_failed',
    ]) {
      expect(parseCustomerDataExportJob(dto({ state: 'failed', reason }))).not.toBeNull();
    }
    expect(parseCustomerDataExportJob(dto({ state: 'failed', reason: 'provider-secret=abc' }))).toBeNull();
    expect(parseCustomerDataExportJob(dto({ requestedAtISO: requestedAt }))).toBeNull();
    expect(parseCustomerDataExportJob(dto({ requestedAt: null }))).toBeNull();
  });

  it('rejects malformed, unknown, mismatched, and noncanonical path data', () => {
    expect(parseCustomerDataExportJob(dto({ schemaVersion: 2 }))).toBeNull();
    expect(parseCustomerDataExportJob(dto({ state: 'future' }))).toBeNull();
    expect(parseCustomerDataExportJob(dto({ extra: 'private data' }))).toBeNull();
    expect(parseCustomerDataExportJob(readyDto({ downloadPath: 'https://attacker.invalid/file' }))).toBeNull();
    expect(parseCustomerDataExportJob(readyDto({ readyAt: '2026-10-23T08:00:00Z' }))).toBeNull();
    expect(customerDataExportLocationMatches(`${customerDataExportPath(jobId)}?next=/evil`, jobId)).toBe(false);
    expect(customerDataExportLocationMatches('/app/v1/customer/data/exports/not-a-guid', 'not-a-guid')).toBe(false);
    expect(exportErrorCopy('private-provider-password=top-secret')).not.toContain('top-secret');
  });

  it('stores only account-scoped recovery metadata and explicitly retries the same uncertain key', async () => {
    const storage = createStorage();
    const transport = vi.fn()
      .mockRejectedValueOnce(new TypeError('private network detail'))
      .mockResolvedValueOnce(jsonResponse(dto(), 202, { Location: customerDataExportPath(jobId) }));
    const client = new CustomerDataExportClient({
      currentAccountId: () => 'account/a', transport, storage, createIdempotencyKey: () => keyOne,
    });
    client.setAccount('account/a');

    await expect(client.createOrRetry()).rejects.toThrow();
    expect(client.recoveryMetadata).toEqual({ jobId: null, pendingIdempotencyKey: keyOne });
    expect([...storage.values.values()][0]).not.toContain('private network detail');

    const created = await client.createOrRetry();
    expect(created.id).toBe(jobId);
    expect(transport).toHaveBeenCalledTimes(2);
    for (const [, init] of transport.mock.calls) {
      expect(init?.method).toBe('POST');
      expect(init?.body).toBe('{"schemaVersion":1}');
      expect(new Headers(init?.headers).get('Idempotency-Key')).toBe(keyOne);
    }
    expect(client.recoveryMetadata).toEqual({ jobId, pendingIdempotencyKey: null });
    expect([...storage.values.values()][0]).not.toContain('account/a');
    client.dispose();
  });

  it('does not automatically issue a POST after reloading a pending key, and replaces it only after explicit 409', async () => {
    const storage = createStorage();
    const firstTransport = vi.fn().mockRejectedValue(new TypeError('offline'));
    const first = new CustomerDataExportClient({ currentAccountId: () => 'account-1', transport: firstTransport, storage, createIdempotencyKey: () => keyOne });
    first.setAccount('account-1');
    await expect(first.createOrRetry()).rejects.toThrow();
    first.dispose();

    const transport = vi.fn()
      .mockResolvedValueOnce(jsonResponse({ title: 'conflict', detail: 'private diagnostics' }, 409))
      .mockResolvedValueOnce(jsonResponse(dto(), 202, { Location: customerDataExportPath(jobId) }));
    const restored = new CustomerDataExportClient({
      currentAccountId: () => 'account-1', transport, storage, createIdempotencyKey: () => keyTwo,
    });
    restored.setAccount('account-1');
    expect(restored.recoveryMetadata.pendingIdempotencyKey).toBe(keyOne);
    expect(transport).not.toHaveBeenCalled();

    await expect(restored.createOrRetry()).rejects.toMatchObject({ status: 409 });
    expect(new Headers(transport.mock.calls[0][1]?.headers).get('Idempotency-Key')).toBe(keyOne);
    await restored.createNewAttempt();
    expect(new Headers(transport.mock.calls[1][1]?.headers).get('Idempotency-Key')).toBe(keyTwo);
    expect(restored.recoveryMetadata).toEqual({ jobId, pendingIdempotencyKey: null });
    restored.dispose();
  });

  it('restores only the same account job and never crosses an account boundary', async () => {
    const storage = createStorage();
    const account = { current: 'account-1' };
    const first = new CustomerDataExportClient({
      currentAccountId: () => account.current,
      transport: vi.fn().mockResolvedValue(jsonResponse(dto(), 202, { Location: customerDataExportPath(jobId) })),
      storage,
      createIdempotencyKey: () => keyOne,
    });
    first.setAccount(account.current);
    await first.createOrRetry();
    first.dispose();

    const transport = vi.fn().mockResolvedValue(jsonResponse(dto()));
    const restored = new CustomerDataExportClient({ currentAccountId: () => account.current, transport, storage });
    restored.setAccount('account-2');
    expect(restored.recoveryMetadata.jobId).toBeNull();
    expect(await restored.refresh()).toBeNull();
    restored.setAccount('account-1');
    expect((await restored.refresh())?.id).toBe(jobId);
    expect(transport).toHaveBeenCalledWith(customerDataExportPath(jobId), expect.objectContaining({ method: 'GET' }));
    restored.dispose();
  });

  it('aborts and ignores a late response after account change', async () => {
    const storage = createStorage();
    storage.setItem('dildovander.customer-data-export.v1.account-1', JSON.stringify({ schemaVersion: 1, jobId, pendingIdempotencyKey: null }));
    let finish!: (response: Response) => void;
    let oldSignal!: AbortSignal;
    const transport = vi.fn((_, init?: RequestInit) => {
      oldSignal = init?.signal as AbortSignal;
      return new Promise<Response>(resolve => { finish = resolve; });
    });
    let currentAccountId = 'account-1';
    const client = new CustomerDataExportClient({ currentAccountId: () => currentAccountId, transport, storage });
    client.setAccount(currentAccountId);
    const pending = client.refresh();
    currentAccountId = 'account-2';
    client.setAccount(currentAccountId);
    finish(jsonResponse(dto()));

    await expect(pending).rejects.toMatchObject({ name: 'AbortError' });
    expect(oldSignal.aborted).toBe(true);
    expect(client.recoveryMetadata).toEqual({ jobId: null, pendingIdempotencyKey: null });
    client.dispose();
  });

  it('keeps the download request abortable while the bounded response stream is being read', async () => {
    const storage = createStorage();
    storage.setItem('dildovander.customer-data-export.v1.account-1', JSON.stringify({ schemaVersion: 1, jobId, pendingIdempotencyKey: null }));
    let streamController!: ReadableStreamDefaultController<Uint8Array>;
    let requestSignal!: AbortSignal;
    const transport = vi.fn(async (_path: string, init?: RequestInit) => {
      requestSignal = init?.signal as AbortSignal;
      const stream = new ReadableStream<Uint8Array>({
        start(controller) {
          streamController = controller;
          controller.enqueue(new Uint8Array([123]));
        },
      });
      requestSignal.addEventListener('abort', () => streamController.error(new DOMException('Aborted', 'AbortError')));
      return new Response(stream, { status: 200, headers: { 'Content-Type': 'application/json' } });
    });
    let currentAccountId = 'account-1';
    const client = new CustomerDataExportClient({ currentAccountId: () => currentAccountId, transport, storage });
    client.setAccount(currentAccountId);
    const pending = client.download(parseCustomerDataExportJob(readyDto())!);
    await Promise.resolve();

    currentAccountId = 'account-2';
    client.setAccount(currentAccountId);
    await expect(pending).rejects.toMatchObject({ name: 'AbortError' });
    expect(requestSignal.aborted).toBe(true);
    client.dispose();
  });

  it('deletes only the explicit technical job and clears its account-scoped locator', async () => {
    const storage = createStorage();
    storage.setItem('dildovander.customer-data-export.v1.account-1', JSON.stringify({ schemaVersion: 1, jobId, pendingIdempotencyKey: null }));
    const transport = vi.fn().mockResolvedValue(new Response(null, { status: 204 }));
    const client = new CustomerDataExportClient({ currentAccountId: () => 'account-1', transport, storage });
    client.setAccount('account-1');
    await client.deleteJob();

    expect(transport).toHaveBeenCalledWith(jobPathFor(jobId), expect.objectContaining({ method: 'DELETE' }));
    expect(client.recoveryMetadata).toEqual({ jobId: null, pendingIdempotencyKey: null });
    client.dispose();
  });

  it('blocks expired and arbitrary-path downloads and enforces the 32 MiB cap while streaming', async () => {
    const now = Date.parse('2026-10-23T09:30:00Z');
    const transport = vi.fn();
    const client = new CustomerDataExportClient({ currentAccountId: () => 'account-1', transport, now: () => now });
    client.setAccount('account-1');
    client.setAccount('account-1');
    // A restored ready job is sufficient for a download; no content enters the UI.
    const storage = createStorage();
    storage.setItem('dildovander.customer-data-export.v1.account-1', JSON.stringify({ schemaVersion: 1, jobId, pendingIdempotencyKey: null }));
    const readyClient = new CustomerDataExportClient({
      currentAccountId: () => 'account-1', transport, storage, now: () => now,
    });
    readyClient.setAccount('account-1');
    const ready = parseCustomerDataExportJob(readyDto())!;

    await expect(readyClient.download({ ...ready, downloadPath: 'https://attacker.invalid/secret' })).rejects.toMatchObject({ kind: 'invalid-response' });
    await expect(readyClient.download({ ...ready, downloadExpiresAt: '2026-10-23T09:00:00Z' })).rejects.toMatchObject({ kind: 'expired' });
    expect(transport).not.toHaveBeenCalled();

    transport.mockResolvedValueOnce(new Response(new ReadableStream({
      start(controller) {
        controller.enqueue(new Uint8Array(customerDataExportMaxDownloadBytes));
        controller.enqueue(new Uint8Array(1));
        controller.close();
      },
    }), { status: 200, headers: { 'Content-Type': 'application/json' } }));
    await expect(readyClient.download(ready)).rejects.toMatchObject({ kind: 'too-large' });
    expect(transport.mock.calls[0][0]).toBe(`${customerDataExportPath(jobId)}/download`);
    readyClient.dispose();
  });

  it('validates the download type and refuses a declared oversize body before reading it', async () => {
    const storage = createStorage();
    storage.setItem('dildovander.customer-data-export.v1.account-1', JSON.stringify({ schemaVersion: 1, jobId, pendingIdempotencyKey: null }));
    const transport = vi.fn()
      .mockResolvedValueOnce(new Response('{}', { status: 200, headers: { 'Content-Type': 'text/plain' } }))
      .mockResolvedValueOnce(new Response('{}', { status: 200, headers: { 'Content-Type': 'application/json', 'Content-Length': String(customerDataExportMaxDownloadBytes + 1) } }));
    const client = new CustomerDataExportClient({ currentAccountId: () => 'account-1', transport, storage });
    client.setAccount('account-1');
    const ready = parseCustomerDataExportJob(readyDto())!;

    await expect(client.download(ready)).rejects.toMatchObject({ kind: 'invalid-response' });
    await expect(client.download(ready)).rejects.toMatchObject({ kind: 'too-large' });
    client.dispose();
  });

  it('returns generic safe copy for raw failures', () => {
    const error = new CustomerDataExportError('http', 409);
    expect(exportErrorCopy(error)).toContain('neaktuální požadavek');
    expect(exportErrorCopy(new Error('cookie=private-secret'))).not.toContain('private-secret');
  });

  it('accepts only a complete UTF-8 account-bound export document', async () => {
    const storage = createStorage();
    storage.setItem('dildovander.customer-data-export.v1.account-1', JSON.stringify({ schemaVersion: 1, jobId, pendingIdempotencyKey: null }));
    const document = { schemaVersion: 1, account: { id: 'account-1' }, history: { sessions: [] } };
    const transport = vi.fn().mockResolvedValueOnce(jsonResponse(document))
      .mockResolvedValueOnce(jsonResponse({ ...document, account: { id: 'another-account' } }))
      .mockResolvedValueOnce(jsonResponse([]))
      .mockResolvedValueOnce(new Response('{"schemaVersion":1', { headers: { 'Content-Type': 'application/json' } }))
      .mockResolvedValueOnce(new Response(new Uint8Array([0xff]), { headers: { 'Content-Type': 'application/json' } }));
    const client = new CustomerDataExportClient({ currentAccountId: () => 'account-1', transport, storage, now: () => Date.parse('2026-10-23T09:02:00Z') });
    client.setAccount('account-1');
    const ready = parseCustomerDataExportJob(readyDto())!;
    const file = await client.download(ready);
    expect(file.type).toBe('application/json');
    expect(await file.text()).toBe(JSON.stringify(document));
    for (let index = 0; index < 4; index++) {
      await expect(client.download(ready)).rejects.toMatchObject({ kind: 'invalid-response' });
    }
    client.dispose();
  });

  it('bounds streamed status responses and keeps the uncertain accepted POST key', async () => {
    const storage = createStorage();
    const cancelled = vi.fn();
    const transport = vi.fn().mockResolvedValue(new Response(new ReadableStream({
      start(controller) { controller.enqueue(new Uint8Array(65537)); }, cancel: cancelled,
    }), { status: 202, headers: { 'Content-Type': 'application/json', Location: jobPathFor(jobId) } }));
    const client = new CustomerDataExportClient({ currentAccountId: () => 'account-1', transport, storage, createIdempotencyKey: () => keyOne });
    client.setAccount('account-1');
    await expect(client.createOrRetry()).rejects.toMatchObject({ kind: 'invalid-response' });
    expect(cancelled).toHaveBeenCalledOnce();
    expect(client.recoveryMetadata.pendingIdempotencyKey).toBe(keyOne);
    expect(transport).toHaveBeenCalledOnce();
    client.dispose();
  });
});

function jobPathFor(id: string): string {
  return `/app/v1/customer/data/exports/${id}`;
}
