// TwinSnapshot.Id: 1–96 ASCII letters, numbers, hyphen, underscore or dot.
// Whole URL dot segments have no stable resource identity and must never route.
export function isTwinAccountPath(path: string): boolean {
  return /^\/v1\/twin\/instances(?:\/[A-Za-z0-9_.-]{1,96}(?:\/(?:recording|commands|settings|media\/(?:start|cancel|input|photo|played|state|output)))?)?$/.test(path)
    && !path.split('/').some(segment => segment === '.' || segment === '..');
}

/** Fixed operator routes. Firmware cue delivery is intentionally absent. */
export function isShowAccountPath(path: string, method = 'GET'): boolean {
  if (path.length > 4096 || /[#\\]/.test(path)) return false;
  const [route, query, extra] = path.split('?');
  if (extra !== undefined || route.includes('%') || route.split('/').some(part => part === '.' || part === '..')) return false;
  const match = /^\/v1\/show\/devices\/([A-Za-z0-9._-]{1,128})(.*)$/.exec(route);
  if (!match) return false;
  const suffix = match[2];
  const uuid = '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}';
  if (method === 'GET') {
    if (['/identities', '/prophecies', '/duels'].includes(suffix)) {
      const values = new URLSearchParams(query);
      const evening = values.get('eveningId') ?? '';
      return [...values].length === 1 && validEvening(evening);
    }
    return query === undefined && (suffix === '' || new RegExp(`^/duels/${uuid}$`).test(suffix));
  }
  return method === 'POST' && query === undefined && (
    ['/commands', '/end-evening', '/offline-outcomes', '/duels'].includes(suffix)
    || /^\/prophecies\/[A-Za-z0-9_-]{1,128}\/reveal$/.test(suffix)
    || new RegExp(`^/duels/${uuid}/(?:start|cancel|grants|grants/revoke)$`).test(suffix));
}

export function isChronicleAdminPath(path: string, method = 'GET'): boolean {
  if (method !== 'GET' || path.length > 4096 || path.includes('#') || path.includes('\\')) return false;
  const [route, query, extra] = path.split('?');
  if (route !== '/v1/admin/show/chronicle' || query === undefined || extra !== undefined) return false;
  const values = new URLSearchParams(query);
  if ([...values].length !== 2 || values.getAll('deviceId').length !== 1 || values.getAll('eveningId').length !== 1) return false;
  const device = values.get('deviceId') ?? '';
  const evening = values.get('eveningId') ?? '';
  return /^[A-Za-z0-9._-]{1,128}$/.test(device) && validEvening(evening);
}

function validEvening(value: string): boolean {
  return value.trim().length > 0 && value.length <= 240 && !/[\x00-\x1f\x7f]/.test(value);
}
