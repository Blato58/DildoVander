export type CombatArchetype = 'offensive' | 'defensive' | 'deceptive' | 'balanced';
export const combatArchetypes: CombatArchetype[] = ['offensive', 'defensive', 'deceptive', 'balanced'];
export function combatSpecialty(value: unknown, en = false): string {
  const labels = en ? { offensive: 'Offensive · force with a price', defensive: 'Defensive · patient protection', deceptive: 'Deceptive · cunning and misdirection', balanced: 'Balanced · adaptable magic' }
    : { offensive: 'Útočná · síla za cenu jistoty', defensive: 'Obranná · trpělivá ochrana', deceptive: 'Klamavá · lest a přesměrování', balanced: 'Vyvážená · přizpůsobivá magie' };
  return labels[combatArchetypes.includes(value as CombatArchetype) ? value as CombatArchetype : 'balanced'];
}
export type WandAction = 'strike' | 'trick' | 'shield';
export interface WandSpell { action: WandAction; name: string; description: string }
/** Public fiction only. Ownership, profile revisions and selection evidence are deliberately absent. */
export interface GeneratedWand {
  wandId: string; physicalSlot: number; name: string; material: string; core: string;
  temperament: string; lore: string; designDescription: string; affinity: string;
  weakness: string; spells: WandSpell[]; combatArchetype?: CombatArchetype; combatRulesVersion?: string;
}
const fields = ['wandId', 'name', 'material', 'core', 'temperament', 'lore', 'designDescription', 'affinity', 'weakness'] as const;
const actions: WandAction[] = ['strike', 'trick', 'shield'];
const text = (value: unknown, limit: number): value is string => typeof value === 'string'
  && value.trim().length > 0 && value.length <= limit && !/[\u0000-\u001f\u007f]/.test(value);
export function projectSpells(value: unknown): WandSpell[] | null {
  if (!Array.isArray(value) || value.length !== 3) return null;
  const spells: WandSpell[] = [];
  for (const action of actions) {
    const spell = value.find(item => item && item.action === action);
    if (!spell || !text(spell.name, 160) || !text(spell.description, 1000)) return null;
    spells.push({ action, name: spell.name, description: spell.description });
  }
  return new Set(spells.map(spell => spell.name.toLocaleLowerCase())).size === 3 ? spells : null;
}
export function projectGeneratedWand(value: unknown): GeneratedWand | null {
  if (!value || typeof value !== 'object') return null;
  const raw = value as Record<string, unknown>;
  if (!Number.isInteger(raw.physicalSlot) || Number(raw.physicalSlot) < 1 || Number(raw.physicalSlot) > 6) return null;
  const result: Record<string, unknown> = { physicalSlot: raw.physicalSlot };
  for (const field of fields) {
    if (!text(raw[field], field === 'wandId' ? 160 : 2000)) return null;
    result[field] = raw[field];
  }
  if (combatArchetypes.includes(raw.combatArchetype as CombatArchetype)) result.combatArchetype = raw.combatArchetype;
  if (text(raw.combatRulesVersion, 80)) result.combatRulesVersion = raw.combatRulesVersion;
  const spells = projectSpells(raw.spells);
  return spells ? { ...result, spells } as unknown as GeneratedWand : null;
}
