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

export const initialCustomerDemoStep: CustomerDemoStepId = 'character';

export function getCustomerDemoStep(id: CustomerDemoStepId): CustomerDemoStep {
  return customerDemoSteps.find((step) => step.id === id) ?? customerDemoSteps[0];
}

export function nextCustomerDemoStep(id: CustomerDemoStepId): CustomerDemoStepId {
  const index = customerDemoSteps.findIndex((step) => step.id === id);
  return customerDemoSteps[Math.min(index + 1, customerDemoSteps.length - 1)].id;
}

export function previousCustomerDemoStep(id: CustomerDemoStepId): CustomerDemoStepId {
  const index = customerDemoSteps.findIndex((step) => step.id === id);
  return customerDemoSteps[Math.max(index - 1, 0)].id;
}
