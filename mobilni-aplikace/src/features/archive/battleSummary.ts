import type { Duel } from './showContracts';
import { duelSpell, duelWandName } from './showContracts';
/** Local, explicit export of the visible fictional result. No route, account identifier or grant enters the card. */
export async function createBattleSummary(view: Duel, artwork: { left: string; right: string }, colors: { background: string; surface: string; text: string; muted: string; accent: string }): Promise<Blob> {
  if (!view.result || view.state !== 'Completed') throw new Error('Battle is not complete');
  const canvas = document.createElement('canvas'); canvas.width = 1000; canvas.height = 1400;
  const ctx = canvas.getContext('2d'); if (!ctx) throw new Error('Image export unavailable');
  const lines = (text: string, x: number, y: number, width: number, lineHeight: number, maximum = 3) => {
    if (maximum === 1) { ctx.fillText(text, x, y, width); return; }
    const words = text.split(/\s+/); let line = ''; let row = 0;
    for (const [index, word] of words.entries()) {
      const next = line ? `${line} ${word}` : word;
      if (ctx.measureText(next).width > width && line) {
        ctx.fillText(line, x, y + row * lineHeight, width); row++;
        if (row === maximum - 1) { ctx.fillText(words.slice(index).join(' '), x, y + row * lineHeight, width); return; }
        line = word;
      } else line = next;
    }
    if (line) ctx.fillText(line, x, y + row * lineHeight, width);
  };
  ctx.fillStyle = colors.background; ctx.fillRect(0, 0, canvas.width, canvas.height);
  ctx.textAlign = 'center'; ctx.fillStyle = colors.muted; ctx.font = '600 22px Inter, sans-serif'; ctx.fillText('DildoVander · Souboj postav', 500, 65);
  const winner = view.result.leftPoints === view.result.rightPoints ? 'Remíza' : `${view[view.result.leftPoints > view.result.rightPoints ? 'left' : 'right'].displayName} vítězí!`;
  ctx.fillStyle = colors.text; ctx.font = '700 42px Inter, sans-serif'; lines(winner, 500, 132, 880, 48, 2);
  ctx.fillStyle = colors.accent; ctx.font = '700 78px Inter, sans-serif'; ctx.fillText(`${view.result.leftPoints} : ${view.result.rightPoints}`, 500, 250);
  for (const [index, side] of (['left', 'right'] as const).entries()) {
    const x = 65 + index * 470;
    ctx.fillStyle = colors.surface; ctx.fillRect(x, 295, 400, 540);
    if (artwork[side]) {
      try {
        const image = new Image(); image.src = artwork[side]; await image.decode();
        const scale = Math.min(370 / image.naturalWidth, 410 / image.naturalHeight);
        const width = image.naturalWidth * scale; const height = image.naturalHeight * scale;
        ctx.drawImage(image, x + (400 - width) / 2, 310 + (410 - height) / 2, width, height);
      } catch { /* Missing artwork never substitutes another wand. The named identity remains useful. */ }
    }
    ctx.fillStyle = colors.text; ctx.font = '600 26px Inter, sans-serif'; lines(view[side].displayName, x + 200, 757, 360, 30, 1);
    ctx.fillStyle = colors.muted; ctx.font = '500 22px Inter, sans-serif'; lines(duelWandName(view[side]), x + 200, 795, 360, 28, 1);
  }
  ctx.textAlign = 'left';
  for (const [index, round] of view.rounds.entries()) {
    const y = 890 + index * 135;
    ctx.fillStyle = colors.accent; ctx.font = '600 21px Inter, sans-serif'; ctx.fillText(`Kolo ${round.number} · ${round.leftScore} : ${round.rightScore}`, 65, y);
    ctx.fillStyle = colors.text; ctx.font = '600 22px Inter, sans-serif'; lines(`${duelSpell(view.left, round.leftAction)} × ${duelSpell(view.right, round.rightAction)}`, 65, y + 34, 870, 28, 1);
    ctx.fillStyle = colors.muted; ctx.font = '400 20px Inter, sans-serif'; lines(view.commentary?.rounds.find(item => item.round === round.number)?.quip ?? round.quip, 65, y + 65, 870, 26, 2);
  }
  ctx.textAlign = 'center'; ctx.fillStyle = colors.muted; ctx.font = '400 18px Inter, sans-serif'; ctx.fillText(view.left.artworkSynthetic || view.right.artworkSynthetic ? 'Syntetická testovací ilustrace · kouzelnická fikce · bez placeného generování' : 'Kouzelnická fikce · původní ilustrace hůlek · žádné fyzické šermování', 500, 1360);
  return new Promise((resolve, reject) => canvas.toBlob(blob => blob ? resolve(blob) : reject(new Error('Image export failed')), 'image/png'));
}
