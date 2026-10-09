#!/usr/bin/env python3
"""Check preserved N16R8 partitions and package an ECDSA P-256 OTA release."""
import argparse
import base64
import csv
import hashlib
import json
from pathlib import Path
import re
import struct
import subprocess
import tempfile

SLOT_SIZE = 0x640000


def check_partitions(path):
    rows = []
    for row in csv.reader(path.read_text(encoding='utf-8').splitlines()):
        if not row or row[0].lstrip().startswith('#'):
            continue
        name, kind, subtype, offset, size = [value.strip() for value in row[:5]]
        rows.append((name, kind, subtype, int(offset, 0), int(size, 0)))
    required = {'nvs': ('data', 'nvs', 0x9000, 0x5000),
                'otadata': ('data', 'ota', 0xe000, 0x2000),
                'app0': ('app', 'ota_0', 0x10000, SLOT_SIZE),
                'app1': ('app', 'ota_1', 0x650000, SLOT_SIZE),
                'spiffs': ('data', 'spiffs', 0xc90000, 0x360000),
                'coredump': ('data', 'coredump', 0xff0000, 0x10000)}
    if len(rows) != len(required) or {row[0]: row[1:] for row in rows} != required:
        raise ValueError('Partition layout changed; initial USB migration must be reviewed')
    ordered = sorted(rows, key=lambda row: row[3])
    for left, right in zip(ordered, ordered[1:]):
        if left[3] + left[4] > right[3]:
            raise ValueError('Partitions overlap')


def check_version(version):
    if not re.fullmatch(r'(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})', version):
        raise ValueError('Version must be three canonical integers')
    if any(int(value) > 65535 for value in version.split('.')):
        raise ValueError('Version component exceeds 65535')


def check_image(path, version=None):
    if not 288 <= path.stat().st_size <= SLOT_SIZE:
        raise ValueError('Firmware does not fit an OTA slot')
    data = path.read_bytes()
    if data[0] != 0xe9 or not 1 <= data[1] <= 16 or struct.unpack_from('<H', data, 12)[0] != 9:
        raise ValueError('Image is not an ESP32-S3 application')
    if data[3] >> 4 != 4:
        raise ValueError('Image must target 16MB flash')
    if struct.unpack_from('<I', data, 32)[0] != 0xabcd5432:
        raise ValueError('Application description missing')
    actual = data[48:80].split(b'\0', 1)[0].decode('ascii')
    if version is not None and actual != version:
        raise ValueError('Signed version must equal embedded ESP-IDF version')
    return data


def openssl(arguments, executable='openssl'):
    return subprocess.run([executable, *map(str, arguments)], check=True, capture_output=True).stdout


def package(binary, partitions, version, build_id, channel, key, output, executable='openssl'):
    check_version(version)
    if not re.fullmatch(r'[a-f0-9]{40}', build_id) or channel not in {'stable', 'development'}:
        raise ValueError('Invalid build identity/channel')
    check_partitions(partitions)
    data = check_image(binary, version)
    if output.exists():
        raise ValueError('Output must be new; never overwrite a release')
    checksum = hashlib.sha256(data).hexdigest()
    metadata = {'schemaVersion': 1, 'version': version, 'buildId': build_id,
                'hardware': 'esp32s3-n16r8', 'profile': 'B-effects', 'channel': channel,
                'updaterVersion': 1, 'minUpdaterVersion': 1,
                'url': f'/v1/firmware/artifacts/{checksum}.bin', 'sizeBytes': len(data),
                'sha256': checksum, 'signatureAlgorithm': 'ecdsa-p256-sha256'}
    payload = json.dumps(metadata, separators=(',', ':')).encode('ascii')
    with tempfile.TemporaryDirectory(prefix='kufr-ota-sign-') as temporary:
        root = Path(temporary)
        (root / 'payload').write_bytes(payload)
        openssl(['pkey', '-in', key, '-pubout', '-out', root / 'public.pem'], executable)
        info = openssl(['pkey', '-pubin', '-in', root / 'public.pem', '-text', '-noout'], executable)
        if b'prime256v1' not in info and b'NIST CURVE: P-256' not in info:
            raise ValueError('Signing key must be ECDSA P-256')
        openssl(['dgst', '-sha256', '-sign', key, '-out', root / 'signature', root / 'payload'], executable)
        openssl(['dgst', '-sha256', '-verify', root / 'public.pem', '-signature', root / 'signature', root / 'payload'], executable)
        signature, public = (root / 'signature').read_bytes(), (root / 'public.pem').read_bytes()
    envelope = {'schemaVersion': 1, 'payload': base64.b64encode(payload).decode('ascii'),
                'signature': base64.b64encode(signature).decode('ascii')}
    output.mkdir(parents=True)
    (output / 'artifacts').mkdir()
    (output / 'manifests').mkdir()
    (output / 'artifacts' / (checksum + '.bin')).write_bytes(data)
    (output / 'trust.pem').write_bytes(public)
    (output / 'manifests' / (channel + '.json')).write_text(json.dumps(envelope, separators=(',', ':')) + '\n', encoding='ascii')
    (output / 'release.json').write_text(json.dumps(metadata, indent=2) + '\n', encoding='ascii')
    return metadata


def verify(output, executable='openssl'):
    metadata = json.loads((output / 'release.json').read_text())
    if metadata['channel'] not in {'stable', 'development'}:
        raise ValueError('Invalid channel')
    envelope = json.loads((output / 'manifests' / (metadata['channel'] + '.json')).read_text())
    payload = base64.b64decode(envelope['payload'], validate=True)
    if envelope['schemaVersion'] != 1 or json.loads(payload) != metadata:
        raise ValueError('Release metadata mismatch')
    with tempfile.TemporaryDirectory(prefix='kufr-ota-verify-') as temporary:
        root = Path(temporary)
        (root / 'payload').write_bytes(payload)
        (root / 'signature').write_bytes(base64.b64decode(envelope['signature'], validate=True))
        openssl(['dgst', '-sha256', '-verify', output / 'trust.pem', '-signature', root / 'signature', root / 'payload'], executable)
    check_version(metadata['version'])
    checksum = metadata['sha256']
    if not re.fullmatch(r'[a-f0-9]{64}', checksum):
        raise ValueError('Invalid artifact checksum')
    image = check_image(output / 'artifacts' / (checksum + '.bin'), metadata['version'])
    if len(image) != metadata['sizeBytes'] or hashlib.sha256(image).hexdigest() != checksum:
        raise ValueError('Artifact integrity mismatch')
    return metadata


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=['check', 'package', 'verify'])
    parser.add_argument('--binary', type=Path)
    parser.add_argument('--partitions', type=Path, default=Path(__file__).resolve().parents[1] / 'partitions_16mb.csv')
    parser.add_argument('--version')
    parser.add_argument('--build-id')
    parser.add_argument('--channel', choices=['stable', 'development'], default='development')
    parser.add_argument('--key', type=Path)
    parser.add_argument('--out', type=Path)
    parser.add_argument('--openssl', default='openssl')
    args = parser.parse_args()
    if args.command == 'check':
        check_partitions(args.partitions)
        check_image(args.binary, args.version)
        print('N16R8 partitions and image passed')
    elif args.command == 'package':
        print(json.dumps(package(args.binary, args.partitions, args.version, args.build_id,
                                 args.channel, args.key, args.out, args.openssl)))
    else:
        print(json.dumps(verify(args.out, args.openssl)))
