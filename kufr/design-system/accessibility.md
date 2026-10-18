# Přístupnost Vapour Chrome

- Běžný text má kontrast nejméně 4,5 : 1, velký text 3 : 1. Aktivní hranice
  ovládání a focus mají nejméně 3 : 1 vůči sousední ploše. Ověřujte konkrétní
  kombinaci, nikoli jen samotnou paletu. Disabled prvky nejsou potvrzení stavu.
- Focus je 3px informační obrys s odstupem 4 px. Neodstraňujte jej; nesmí být
  zakryt sticky navigací. Ověřte Tab, Shift+Tab, Enter a případně Escape.
- Interaktivní cíl má nejméně 44 × 44 px; sousední mobilní akce odděluje 8 px.
  Bezpečnostní Stop musí zůstat snadno dosažitelný a slovně označený.
- Připojení a běžné aktualizace oznamuje `role="status"`; chybu `role="alert"`.
  Události používají `aria-live="polite"` s přidáváním. Automatické oznámení
  nezahrnuje tokeny, osobní obsah rozhovoru ani celý neomezený log.
- Nadpisy mají logické pořadí a sekce `aria-labelledby`. Label je viditelný;
  placeholder ho nenahrazuje. Barevný stav vždy doprovází text.
- Zachovejte 200% zoom a 320px šířku bez horizontálního scrollu celé stránky;
  výjimkou je lokálně posouvatelná technická tabulka nebo prostorový viewport.
- `prefers-reduced-motion` vypíná animace a smooth scroll. Význam akce nesmí
  záviset na pohybu. Změny bezpečnostního stavu se zobrazí okamžitě.
- Na iOS respektujte horní/dolní safe area a dynamické zvětšení textu.
  Webový screenshot neprokazuje VoiceOver ani akceptaci skutečného iPhonu.

Ověřujte tmavé i světlé téma. Steel Grey slouží jako primitivum; čitelný
pomocný text používá sémantický `--aw-muted`. Hlavní tlačítko používá
`--aw-on-accent`, nikoli předpoklad tmavého nebo bílého textu.

Před předáním ověřte široký desktop, úzký desktop, 390px mobil a 320px šířku,
klávesnici, čitelnost všech skutečně použitých stavů a reduced motion.
Zaznamenejte přesně, co bylo ověřeno a co zůstává bez zařízení.
