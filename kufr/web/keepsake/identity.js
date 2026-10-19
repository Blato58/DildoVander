import { element, notice } from '/design-system/ui.js';

const limits = { wizardAlias: 60, houseOrGuild: 160, dubiousSpecialization: 160,
  signatureAbility: 160, weakness: 160, ministryAssessment: 160, wandBond: 160,
  motto: 120, microStory: 900, omen: 160 };

// Explicit public projection: private phases and provenance are never retained here.
export function publicIdentity(raw) {
  if (!raw || raw.visibility !== 'public_fiction') return null;
  const card = { visibility: 'public_fiction' };
  for (const [key, maximum] of Object.entries(limits)) {
    if (typeof raw[key] !== 'string' || raw[key].length > maximum) return null;
    card[key] = raw[key];
  }
  return card;
}

export function renderWizardIdentity(root, raw) {
  root.replaceChildren();
  const identity = publicIdentity(raw);
  if (!identity) {
    root.append(element(notice({ message: 'Magický profil zatím nevytvořen.', live: false })));
    return;
  }
  const heading = document.createElement('h2'); heading.textContent = identity.wizardAlias;
  const fiction = document.createElement('p'); fiction.className = 'fiction-label';
  fiction.textContent = 'Kouzelnická fikce · nejde o osobní posudek';
  const details = document.createElement('dl'); details.className = 'wizard-traits';
  for (const [label, value] of [['Instituce', identity.houseOrGuild], ['Schopnost', identity.signatureAbility],
    ['Slabina', identity.weakness], ['Pochybná specializace', identity.dubiousSpecialization],
    ['Ministerské hodnocení', identity.ministryAssessment], ['Pouto s hůlkou', identity.wandBond]]) {
    const term = document.createElement('dt'); term.textContent = label;
    const description = document.createElement('dd'); description.textContent = value;
    details.append(term, description);
  }
  const motto = document.createElement('blockquote'); motto.textContent = identity.motto;
  const story = document.createElement('p'); story.textContent = identity.microStory;
  const omen = document.createElement('p'); omen.className = 'wizard-omen';
  omen.textContent = 'Veřejná indicie: ' + identity.omen;
  root.append(heading, fiction, details, motto, story, omen);
}

export function renderProphecy(root, prophecy) {
  root.replaceChildren(); root.hidden = !prophecy;
  if (!prophecy) return;
  const title = document.createElement('h2'); title.textContent = 'Soukromé proroctví';
  const explanation = document.createElement('p'); explanation.className = 'fiction-label';
  explanation.textContent = 'Komediální fikce · soukromá část památky';
  root.append(title, explanation);
  if (prophecy.state === 'revealed' && typeof prophecy.revelation === 'string') {
    for (const value of [prophecy.revelation, prophecy.afterword]) {
      if (!value) continue;
      const paragraph = document.createElement('p'); paragraph.textContent = value; root.append(paragraph);
    }
  } else {
    const when = new Date(prophecy.unlockAt);
    const date = Number.isNaN(when.valueOf()) ? 'za 15 minut od vzniku identity'
      : new Intl.DateTimeFormat('cs-CZ', { timeZone: 'Europe/Prague', dateStyle: 'medium', timeStyle: 'short' }).format(when);
    root.append(element(notice({ message: 'Plné znění se odemkne ' + date
      + ' (Praha), při ukončení tohoto večera nebo povelem obsluhy. Čas ověřuje server.', live: false })));
  }
}
