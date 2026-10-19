// TwinSnapshot.Id: 1–96 ASCII letters, numbers, hyphen, underscore or dot.
// Whole URL dot segments have no stable resource identity and must never route.
export function isTwinAccountPath(path: string): boolean {
  return /^\/v1\/twin\/instances(?:\/[A-Za-z0-9_.-]{1,96}(?:\/(?:recording|commands|settings|media\/(?:start|cancel|input|photo|played|state|output)))?)?$/.test(path)
    && !path.split('/').some(segment => segment === '.' || segment === '..');
}
