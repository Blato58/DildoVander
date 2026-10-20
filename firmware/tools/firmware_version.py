#!/usr/bin/env python3
"""Resolve an immutable firmware version policy using the CI run counter."""
import argparse
import re
from pathlib import Path


def resolve_version(policy, run_number=None):
    if not isinstance(policy, str):
        raise ValueError('Invalid firmware version policy')
    automatic = policy.endswith('+ci')
    baseline = policy[:-3] if automatic else policy
    if not re.fullmatch(r'(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})', baseline):
        raise ValueError('Invalid firmware version policy')
    components = [int(part) for part in baseline.split('.')]
    if any(part > 65535 for part in components):
        raise ValueError('Firmware version component overflow')
    if not automatic:
        return baseline
    if type(run_number) is not int or not 1 <= run_number <= 9007199254740991:
        raise ValueError('A positive authoritative CI run number is required')
    ordinal = (components[0] << 32) + (components[1] << 16) + components[2] + run_number
    if ordinal >= 1 << 48:
        raise ValueError('Firmware version overflow')
    return f'{ordinal >> 32}.{(ordinal >> 16) & 65535}.{ordinal & 65535}'


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--policy', required=True, type=Path)
    source = parser.add_mutually_exclusive_group()
    source.add_argument('--run-number', type=int)
    source.add_argument('--local', action='store_true', help='Use the baseline for an unpublished local build')
    args = parser.parse_args()
    policy = args.policy.read_text(encoding='ascii').rstrip('\r\n')
    if args.local and policy.endswith('+ci'):
        policy = policy[:-3]
    print(resolve_version(policy, args.run_number))
