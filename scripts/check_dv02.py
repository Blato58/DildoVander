#!/usr/bin/env python3
"""Validate the DV02 coverage inventory and project skill references offline."""
import json
import argparse
import hashlib
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]


def require(condition, message):
    if not condition:
        raise ValueError(message)


def validate_check(check):
    path = (ROOT / check['path']).resolve()
    require(path.is_relative_to(ROOT) and path.is_file(), f'Missing check: {path}')
    if check.get('symbol'):
        require(check['symbol'] in path.read_text(encoding='utf-8'),
                f'Unknown test symbol: {check["path"]}: {check["symbol"]}')


def validate_links(path):
    content = path.read_text(encoding='utf-8')
    for link in re.findall(r'\[[^\]]*\]\(([^)]+)\)', content):
        if link.startswith(('https://', 'http://', '#')):
            continue
        target = (path.parent / link.split('#')[0]).resolve()
        require(target.is_relative_to(ROOT) and target.exists(),
                f'Broken local reference: {path.relative_to(ROOT)} → {link}')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--verify-source', action='store_true', help='Verify the recorded tested source bundle against this checkout')
    args = parser.parse_args()
    coverage = json.loads((ROOT / 'kufr/docs/dv02-coverage.json').read_text(encoding='utf-8'))
    specification = (ROOT / coverage['specification']).read_text(encoding='utf-8')
    for category, prefix, count in [('requirements', 'R', 48), ('tests', 'T', 80)]:
        expected = {f'DV02-{prefix}{index:02d}' for index in range(1, count + 1)}
        rows = coverage[category]
        require(len(rows) == count and {row['id'] for row in rows} == expected,
                f'{category}: exact unique {count}-item inventory required')
        require(set(re.findall(r'^\| (DV02-' + prefix + r'\d{2}) \|', specification, re.M)) == expected,
                f'{category}: specification inventory mismatch')
        for row in rows:
            status = row['status']
            require(status in {'covered', 'blocked/not_run', 'not_applicable'}, f'{row["id"]}: invalid status')
            require(bool(row.get('expected')), f'{row["id"]}: missing expected invariant')
            if status == 'covered':
                require(bool(row.get('checks') or row.get('tests')), f'{row["id"]}: coverage without checks')
                if prefix == 'T':
                    require(all(row.get(key) for key in ('inputs', 'forbidden', 'layer', 'command', 'requirements')),
                            f'{row["id"]}: incomplete scenario evidence mapping')
                for check in row.get('checks', []):
                    validate_check(check)
            elif status == 'blocked/not_run':
                require(bool(row.get('gate')), f'{row["id"]}: missing concrete gate')
            else:
                require(bool(row.get('reason')), f'{row["id"]}: missing applicability reason')
    names = ('dildovander-show-authoring', 'dildovander-show-debug')
    for name in names:
        folder = ROOT / '.agents/skills' / name
        skill = folder / 'SKILL.md'
        text = skill.read_text(encoding='utf-8')
        require(text.startswith('---\n'), f'{name}: missing frontmatter')
        metadata = text.split('---', 2)[1]
        require(f'\nname: {name}\n' in metadata and '\ndescription: ' in metadata,
                f'{name}: invalid name/description')
        require('$' + name in (folder / 'agents/openai.yaml').read_text(encoding='utf-8'),
                f'{name}: default prompt targets another skill')
        validate_links(skill)
    validate_links(ROOT / 'kufr/docs/dv02.md')
    if args.verify_source:
        item = json.loads((ROOT / 'docs/project/work-items/DV02.json').read_text(encoding='utf-8'))
        expected_subject = item['implementedSubject']['artifactDigest']
        bundles = [json.loads(path.read_text(encoding='utf-8'))
                   for path in (ROOT / 'docs/project/artifacts').glob('DV02-source-bundle*.json')]
        matching = [bundle for bundle in bundles if bundle['artifactDigest'] == expected_subject]
        require(len(matching) == 1, 'Expected exactly one recorded source bundle for the current DV02 subject')
        bundle = matching[0]
        fingerprint = hashlib.sha256()
        for name, expected in sorted(bundle['inputDigests'].items()):
            path = (ROOT / name).resolve()
            require(path.is_relative_to(ROOT) and path.is_file(), f'Missing source input: {name}')
            data = path.read_bytes()
            require(hashlib.sha256(data).hexdigest() == expected, f'Stale tested source: {name}')
            for part in (name.encode('utf-8'), data):
                fingerprint.update(len(part).to_bytes(8, 'big'))
                fingerprint.update(part)
        require(fingerprint.hexdigest() == bundle['artifactDigest'], 'Source bundle digest mismatch')
        print('PASS tested source digest: ' + bundle['artifactDigest'])
    print('PASS DV02: 48 requirements, 80 scenario families, 2 skill metadata/link workflows')
    print('Coverage is an inventory; actual receipts identify executed checks. Physical G and live skill reload remain separate.')


if __name__ == '__main__':
    main()
