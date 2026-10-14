#!/usr/bin/env python3
"""Compile real historical/current cores and compare captured offline port outputs."""
import argparse
import hashlib
import json
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
import subprocess
import tempfile

from run_show_tests import compiler_environment

ROOT = Path(__file__).resolve().parents[3]
FIRMWARE = ROOT / 'kufr/firmware'
BASELINE = '74f215fc371e19780a3521adf0f7b79dcdb068a5'
CORE = ['safety/SafetyController', 'wand_selection/SelectionSequence',
        'device_control/DeviceControl', 'device_control/DeviceControlRuntime',
        'conversation/ConversationRuntimeState', 'conversation/PendingProposal',
        'wand_presentation/WandPresentation', 'physical_effects/PhysicalEffects',
        'physical_effects/PhysicalPresentation']


def historical_checkout(destination):
    names = subprocess.check_output(['git', 'ls-tree', '-r', '--name-only', BASELINE,
                                     'kufr/firmware/src'], cwd=ROOT, text=True).splitlines()
    compiled = {'kufr/firmware/src/features/' + name + '.cpp' for name in CORE}
    names = sorted(name for name in names if name.endswith('.h') or name in compiled)

    def read_blob(name):
        return name, subprocess.check_output(['git', 'show', BASELINE + ':' + name], cwd=ROOT)

    # Only four immutable object reads at a time; no checkout, branch or index writes.
    with ThreadPoolExecutor(max_workers=4) as readers:
        for name, content in readers.map(read_blob, names):
            target = destination / name
            if not target.resolve().is_relative_to(destination.resolve()):
                raise RuntimeError('Historical source escaped its temporary root')
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(content)
    tests = destination / 'kufr/firmware/tests'
    tests.mkdir(parents=True)
    for name in ('physical_effects_test_support.h', 'legacy_show_probe.cpp'):
        (tests / name).write_bytes((FIRMWARE / 'tests' / name).read_bytes())
    return destination / 'kufr/firmware'


def current_checkout(destination):
    """Compile a stable snapshot, so concurrent edits cannot falsify its digest."""
    paths = sorted(path for path in (FIRMWARE / 'src').rglob('*')
                   if path.is_file() and path.suffix in ('.h', '.cpp'))
    paths += [FIRMWARE / 'tests' / name for name in ('physical_effects_test_support.h', 'legacy_show_probe.cpp')]
    contents = {path: path.read_bytes() for path in paths}
    for path, content in contents.items():
        target = destination / path.relative_to(FIRMWARE)
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(content)
    if any(path.read_bytes() != content for path, content in contents.items()):
        raise RuntimeError('Current core changed while taking its verification snapshot; rerun when stable')
    return destination


def source_identity(firmware, sources):
    digest = hashlib.sha256()
    files = sorted((firmware / 'src').rglob('*.h'))
    files += [firmware / 'src/features' / (name + '.cpp') for name in sources]
    files += [firmware / 'tests' / name for name in ('physical_effects_test_support.h', 'legacy_show_probe.cpp')]
    for path in files:
        digest.update(path.relative_to(firmware).as_posix().encode('utf-8') + b'\0')
        digest.update(hashlib.sha256(path.read_bytes()).digest())
    return digest.hexdigest()


def run_probe(firmware, build, compiler, environment, msvc, current):
    build.mkdir()
    sources = CORE + (['physical_effects/PresentationDiagnostics'] if current else [])
    executable = build / ('probe.exe' if msvc else 'probe')
    files = [str(firmware / 'src/features' / (name + '.cpp')) for name in sources]
    files.append(str(firmware / 'tests/legacy_show_probe.cpp'))
    flags = (['/nologo', '/std:c++14', '/utf-8', '/EHsc', '/W4', '/WX', '/wd4244', '/Fe:' + str(executable)]
             if msvc else ['-std=c++14', '-Wall', '-Wextra', '-Werror', '-pthread', '-o', str(executable)])
    subprocess.run([compiler, *flags, *files], cwd=build, env=environment, check=True,
                   stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    output = subprocess.check_output([str(executable)], cwd=build, env=environment, text=True, encoding='utf-8')
    cases = [json.loads(line) for line in output.splitlines() if line]
    if len(cases) != 12:
        raise RuntimeError('Probe must emit main/short for all six slots')
    if {(case['slot'], case['variant']) for case in cases} != {(slot, variant) for slot in range(1, 7) for variant in ('main', 'short')}:
        raise RuntimeError('Probe did not cover every required slot/variant')
    return cases, source_identity(firmware, sources)


def compare(baseline, candidate):
    results = []
    for old, new in zip(baseline, candidate, strict=True):
        if (old['slot'], old['variant']) != (new['slot'], new['variant']):
            raise RuntimeError('Probe case ordering differs')
        differences = []
        old_commands = [{key: value for key, value in event.items() if key != 'atMs'} for event in old['events']]
        new_commands = [{key: value for key, value in event.items() if key != 'atMs'} for event in new['events']]
        commands_equal = old_commands == new_commands
        errors = []
        if not commands_equal:
            errors.append('Significant output command sequence differs')
        if commands_equal:
            for index, (before, after) in enumerate(zip(old['events'], new['events'], strict=True)):
                delta = after['atMs'] - before['atMs']
                if delta:
                    differences.append({'eventIndex': index, 'type': before['type'],
                                        'baselineMs': before['atMs'], 'candidateMs': after['atMs'], 'deltaMs': delta})
            if differences:
                errors.append('Nominal output timing differs; no timing exceptions are permitted')
        for name, case in (('baseline', old), ('candidate', new)):
            servos = [event for event in case['events'] if event['type'] == 'servo']
            pulses = [event for event in case['events'] if event['type'] == 'vibration']
            durations = [event['durationMs'] for event in case['events'] if event['type'] == 'actuator-off']
            if [event['pulseUs'] for event in servos] != [1900, 1100] or any(event['channel'] != case['slot'] - 1 for event in servos):
                errors.append(name + ': expected one raise and return on the same slot')
            pulse_count = 3 if case['variant'] == 'main' else 1
            if len(pulses) != pulse_count or any(event['channel'] != case['slot'] + 5 for event in pulses):
                errors.append(name + ': vibration pulse count/target differs')
            if durations != [150] * pulse_count + [500, 500]:
                errors.append(name + ': physical command duration differs from approved pulse/calibration fixture')
            if case['hardDeadlineMs'] != 15000 or case['durationMs'] >= case['hardDeadlineMs']:
                errors.append(name + ': hard deadline changed or exceeded')
        results.append({'slot': old['slot'], 'variant': old['variant'], 'commandSequenceEqual': commands_equal,
                        'baselineDurationMs': old['durationMs'], 'candidateDurationMs': new['durationMs'],
                        'durationDeltaMs': new['durationMs'] - old['durationMs'],
                        'unexpectedDifferences': errors,
                        'timingDifferences': differences,
                        'baselineEvents': old['events'], 'candidateEvents': new['events']})
    return results


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, help='Save the complete reviewable JSON report')
    args = parser.parse_args()
    compiler, environment, msvc = compiler_environment()
    with tempfile.TemporaryDirectory(prefix='dv02-legacy-parity-') as directory:
        temporary = Path(directory)
        baseline_root = historical_checkout(temporary / 'baseline')
        current_root = current_checkout(temporary / 'candidate')
        baseline, old_hash = run_probe(baseline_root, temporary / 'old-build', compiler, environment, msvc, False)
        current, new_hash = run_probe(current_root, temporary / 'new-build', compiler, environment, msvc, True)
    cases = compare(baseline, current)
    report = {'baselineCommit': BASELINE, 'baselineSourceDigest': old_hash, 'candidateSourceDigest': new_hash,
              'evidence': 'host fake-port commands; no physical or acoustic measurement',
              'calibratedTravelMs': 500, 'tickPeriodMs': 10, 'deadlineMs': 15000,
              'timingParity': all(not case['timingDifferences'] and case['commandSequenceEqual'] for case in cases),
              'requirements': ['DV02-R03'], 'testFamilies': ['DV02-T01', 'DV02-T02'], 'cases': cases}
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_bytes((json.dumps(report, ensure_ascii=False, indent=2) + '\n').encode('utf-8'))
    summary = {'cases': len(cases), 'commandSequencesEqual': all(case['commandSequenceEqual'] for case in cases),
               'timingDeltasMs': sorted({case['durationDeltaMs'] for case in cases}),
               'timingParity': report['timingParity'],
               'unexpectedDifferences': [dict(slot=case['slot'], variant=case['variant'], errors=case['unexpectedDifferences'])
                                         for case in cases if case['unexpectedDifferences']],
               'representativeCases': [{key: value for key, value in case.items() if key not in ('baselineEvents', 'candidateEvents')}
                                       for case in cases if case['slot'] == 1]}
    print(json.dumps(summary, ensure_ascii=False, indent=2))
    if not summary['commandSequencesEqual'] or summary['unexpectedDifferences']:
        raise SystemExit(1)


if __name__ == '__main__':
    try:
        main()
    except subprocess.CalledProcessError as error:
        if error.stdout:
            print(error.stdout.decode('utf-8', errors='replace') if isinstance(error.stdout, bytes) else error.stdout)
        if error.stderr:
            print(error.stderr.decode('utf-8', errors='replace') if isinstance(error.stderr, bytes) else error.stderr)
        raise
