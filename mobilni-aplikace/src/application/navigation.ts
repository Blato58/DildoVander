/** Return paths are application-local and never accept a protocol-relative URL. */
export function safeReturnPath(value: unknown): string {
  if (typeof value !== 'string' || !value.startsWith('/') || value.startsWith('//') || /[\\\u0000-\u0020]/.test(value)) return '/';
  const pathname = value.split(/[?#]/)[0];
  const allowed = ['/', '/suitcase', '/connections', '/workshop', '/archive', '/settings', '/persona-lab', '/testing-sessions', '/me', '/admin'];
  return allowed.some(path => pathname === path || (path !== '/' && pathname.startsWith(`${path}/`))) ? value : '/';
}
