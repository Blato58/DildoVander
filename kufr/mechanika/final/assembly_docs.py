"""Generate the readable procedure from the viewer's actual assembly contract."""
import json
from pathlib import Path

ROOT=Path(__file__).resolve().parent


def document(guide):
    lines=['# Postup sestavení F2', '',
           'Generováno z `assembly-guide.json`; stejná data používá Workshop.',
           'CAD a prostorové rezervy nejsou fyzickou přejímkou. Před stavbou přečíst',
           '[montáž a přejímku](MONTÁŽ.md) a [potvrzenou kabeláž](KABELAZ.md).', '']
    prior=[]
    for index,step in enumerate(guide['steps'],1):
        lines += [f"## {index}. {step['title']}", '',step['instruction'], '',
                  '**Místo:** '+step['location']+'; '+step['direction']+'.', '',
                  '**Předchůdci:** '+(', '.join(step['depends']) or 'žádní')+'.', '',
                  '**Již připravené kroky:** '+(', '.join(prior) or 'žádné')+'.', '',
                  '**Díly:** '+str(step['partSummary'])+'.', '', '**Materiál:**']
        for material in step['materials']:
            label=guide['materialCatalog'][material['id']]
            if isinstance(label,dict): label=label.get('label',label.get('name',material['id']))
            lines += [f"- {material['quantity']} × {label} (`{material['id']}`)"]
        if not step['materials']: lines += ['- Použít již připravené díly.']
        lines += ['', '**Nástroje:** '+', '.join(step['tools'])+'.', '', '**Přístup:**']
        lines += ['- '+text for text in step['access']]
        if step['installation']['absent']:
            lines += ['- V kufru zatím nesmějí být: '+', '.join(step['installation']['absent'])+'.']
        if step['connectors']:
            lines += ['', '**Konektory:**']+['- '+text for text in step['connectors']]
        lines += ['', '**Kontrola:** '+step['check'], '', '**Servis:** '+step['removal'], '']
        prior.append(step['id'])
    return '\n'.join(lines).rstrip()+'\n'


def main():
    guide=json.loads((ROOT/'assembly-guide.json').read_text(encoding='utf-8'))
    (ROOT/'POSTUP.md').write_text(document(guide),encoding='utf-8')


if __name__=='__main__':
    main()
