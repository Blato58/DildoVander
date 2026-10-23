/** A fictional, local-only walkthrough. These values never represent account or device state. */
export type CustomerDemoStepId = 'character' | 'conversation' | 'wand-result' | 'keepsake';

export interface CustomerDemoStep {
  readonly id: CustomerDemoStepId;
  readonly title: string;
  readonly label: string;
  readonly description: string;
  readonly lines?: readonly { readonly speaker: string; readonly text: string }[];
  readonly result?: string;
  readonly wandDesign?: {
    readonly shape: string;
    readonly handle: string;
    readonly ornament: string;
    readonly material: string;
    readonly magicalTheme: string;
  };
  readonly keepsake?: string;
}

export const customerDemoSteps: readonly CustomerDemoStep[] = [
  {
    id: 'character',
    title: 'Poznejte postavu',
    label: 'Postava',
    description: 'Amálie žije v Měsíčním lese a sbírá příběhy o zapomenutých cestách.',
  },
  {
    id: 'conversation',
    title: 'Ukázka rozhovoru',
    label: 'Rozhovor',
    description: 'Následující krátký dialog je předem napsaný a slouží jen jako ukázka.',
    lines: [
      { speaker: 'Průvodce', text: 'Kterou cestu bys dnes chtěla prozkoumat?' },
      { speaker: 'Amálie', text: 'Tu, na které se i staré stromy učí nová jména.' },
    ],
  },
  {
    id: 'wand-result',
    title: 'Ilustrativní návrh hůlky',
    label: 'Výsledek',
    description: 'Toto je smyšlený textový návrh inspirovaný příběhem. Žádný předmět nebyl vytvořen ani vyroben.',
    result: 'Amálie objevila pěšinu, která se ukáže jen tomu, kdo chvíli naslouchá.',
    wandDesign: {
      shape: 'Štíhlá, mírně zahnutá',
      handle: 'Bronzový odstín',
      ornament: 'Spirálový ornament',
      material: 'Tmavé dřevo (pouze popis)',
      magicalTheme: 'Jiskry suchého humoru',
    },
  },
  {
    id: 'keepsake',
    title: 'Náhled památky',
    label: 'Památka',
    description: 'Toto je smyšlený náhled památky z ukázkového příběhu, nikoli uložená ani skutečně vytvořená památka.',
    keepsake: 'Měsíční pěšina — příběh Amálie',
  },
];

const customerDemoStepsEn: readonly CustomerDemoStep[] = [
  {
    id: 'character',
    title: 'Meet the character',
    label: 'Character',
    description: 'Amalia lives in the Moonlit Forest and collects stories about forgotten paths.',
  },
  {
    id: 'conversation',
    title: 'Conversation sample',
    label: 'Conversation',
    description: 'This short dialogue is prewritten and shown only as an example.',
    lines: [
      { speaker: 'Guide', text: 'Which path would you like to explore today?' },
      { speaker: 'Amalia', text: 'The one where even old trees learn new names.' },
    ],
  },
  {
    id: 'wand-result',
    title: 'Illustrative wand design',
    label: 'Result',
    description: 'This is a fictional text design inspired by the story. No object was created or manufactured.',
    result: 'Amalia discovered a path that appears only to those who listen for a while.',
    wandDesign: {
      shape: 'Slender, gently curved',
      handle: 'Bronze tone',
      ornament: 'Spiral ornament',
      material: 'Dark wood (description only)',
      magicalTheme: 'Sparks of dry humor',
    },
  },
  {
    id: 'keepsake',
    title: 'Keepsake preview',
    label: 'Keepsake',
    description: 'This is a fictional keepsake preview from the sample story, not a saved or actually created keepsake.',
    keepsake: 'The Moonlit Path — Amalia’s story',
  },
];

export const initialCustomerDemoStep: CustomerDemoStepId = 'character';

export function getCustomerDemoSteps(language: 'cs' | 'en' = 'cs'): readonly CustomerDemoStep[] {
  return language === 'en' ? customerDemoStepsEn : customerDemoSteps;
}

export function getCustomerDemoStep(id: CustomerDemoStepId, language: 'cs' | 'en' = 'cs'): CustomerDemoStep {
  const steps = getCustomerDemoSteps(language);
  return steps.find((step) => step.id === id) ?? steps[0];
}

export function nextCustomerDemoStep(id: CustomerDemoStepId): CustomerDemoStepId {
  const index = customerDemoSteps.findIndex((step) => step.id === id);
  return customerDemoSteps[Math.min(index + 1, customerDemoSteps.length - 1)].id;
}

export function previousCustomerDemoStep(id: CustomerDemoStepId): CustomerDemoStepId {
  const index = customerDemoSteps.findIndex((step) => step.id === id);
  return customerDemoSteps[Math.max(index - 1, 0)].id;
}
