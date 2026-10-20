/** Deliberately bounded commands, never a general language intent classifier. */
export function isSpokenEndCommand(value: string): boolean {
  if (value.length > 160 || /["„“”«»'‘’]/u.test(value)) return false;
  const command = value.normalize('NFD').replace(/\p{M}/gu, '').toLowerCase()
    .replace(/[.,!?]/g, ' ').replace(/\s+/g, ' ').trim()
    .replace(/^(?:prosim|please) /, '').replace(/ (?:prosim|please)$/, '');
  const commands = [
    /^(?:stop|koncime|vypni se)$/,
    /^ukonci (?:tento )?(?:test|rozhovor)$/,
    /^muzes (?:prosim )?(?:ukoncit (?:test|rozhovor)|se vypnout)$/,
    /^end (?:this |the )?(?:test|conversation)$/,
    /^turn (?:yourself )?off$/,
    /^can you (?:please )?(?:end (?:this |the )?(?:test|conversation)|turn (?:yourself )?off)$/,
  ];
  return commands.some(pattern => pattern.test(command));
}

/** Live repeats the initial turn chunk in transcript; Realtime sends a final user turn. */
export class SpokenEndInput {
  private text = '';
  private overflow = false;

  reset(): void { this.text = ''; this.overflow = false; }

  append(delta: string): void {
    if (this.text.length + delta.length > 160) this.overflow = true;
    this.text = (this.text + delta).slice(0, 160);
  }

  matches(): boolean { return !this.overflow && isSpokenEndCommand(this.text); }
}
