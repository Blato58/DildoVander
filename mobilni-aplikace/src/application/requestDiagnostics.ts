import { readonly, shallowRef } from 'vue';

export interface RequestDiagnostic {
  timestamp: string;
  action: string;
  method: string;
  outcome: 'success' | 'http_error' | 'transport_error' | 'cancelled';
  status?: number;
  durationMs: number;
  requestId?: string;
}

const entries = shallowRef<readonly RequestDiagnostic[]>([]);
export const requestDiagnostics = readonly(entries);
let generation = 0;

// Keep only route vocabulary. Resource identifiers, query strings and arbitrary
// path segments must never enter a support export, even on failed requests.
const routeWords = new Set(('app v1 account session sessions testing-sessions characters character enrich mode select generate fault cancel turns catalog keepsakes assets portrait-uploads duels start move persona-lab passkeys request-options sign-in options complete reauthentication-options reauthenticate enrollment customer admin invitations users roles grants recovery archive owned associations devices memory data exports download deletion collections twin evenings active show chronicle').split(' '));
function actionName(path: string): string {
  return path.split(/[?#]/, 1)[0].split('/').slice(0, 10)
    .map(segment => routeWords.has(segment) ? segment : segment ? ':id' : '').join('/');
}

export function diagnosticRequestId(value: unknown): string | undefined {
  // Server middleware emits a canonical GUID; tracing and ASP.NET can also
  // supply a 32-hex trace or a bounded native request ID.
  return typeof value === 'string' && /^(?:[a-fA-F0-9]{32}|[a-fA-F0-9]{8}(?:-[a-fA-F0-9]{4}){3}-[a-fA-F0-9]{12}|[A-Z0-9]{10,20}:[A-F0-9]{8})$/.test(value) ? value : undefined;
}

export function clearRequestDiagnostics(): void {
  ++generation;
  entries.value = [];
}

export function beginRequestDiagnostic(path: string, method: string) {
  const owner = generation;
  const timestamp = new Date().toISOString();
  const started = performance.now();
  let finished = false;
  const append = (outcome: RequestDiagnostic['outcome'], status?: number, requestId?: string) => {
    if (finished || owner !== generation) return;
    finished = true;
    entries.value = [...entries.value.slice(-199), {
      timestamp, action: actionName(path),
      method: ['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD', 'OPTIONS'].includes(method) ? method : 'OTHER',
      outcome, status, durationMs: Math.max(0, Math.round(performance.now() - started)),
      requestId: diagnosticRequestId(requestId),
    }];
  };
  return {
    complete: (status: number, requestId?: string) => append(status >= 200 && status < 300 ? 'success' : 'http_error', status, requestId),
    fail: (cause: unknown) => append(cause instanceof DOMException && cause.name === 'AbortError' ? 'cancelled' : 'transport_error'),
  };
}
