/** In-memory, one-use transfer from the pre-module claim capture. Never persist. */
export function consumeGuestClaim(): string | null {
  const claim = window.__dildovanderGuestClaim;
  delete window.__dildovanderGuestClaim;
  return claim || null;
}

export function consumeEnrollment(): { kind: 'invite' | 'recovery'; token: string } | null {
  const enrollment = window.__dildovanderEnrollment;
  delete window.__dildovanderEnrollment;
  return enrollment ?? null;
}
