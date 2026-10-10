"""Validate the shared viewer score and generate every firmware phase."""
import argparse
import json
from fractions import Fraction
from pathlib import Path

ROOT = Path(__file__).resolve().parent
HEADER = ROOT.parents[1] / 'firmware/src/features/physical_effects/PresentationScore.h'
TARGETS = {'selected': 0, 'next': -2, 'previous': -3}
STAGES = ('prepare', 'raise', 'show', 'lower', 'rest')
# PhysicalPresentation advances one preparation step per nominal 10 ms control
# pass. Cap rounding overhead at 100 * 10 ms, its existing 1000 ms allowance.
# Slow/stalled control passes still fail safely through the runtime watchdogs.
MAX_PREPARATION_STEPS = 100
CONTROL_LOOP_PERIOD_MS = 10
MAX_CALIBRATED_TRAVEL_MS = 3000
MAX_PRESENTATION_MS = 15000
VIEWER_FRAMES_PER_SECOND = 60
# With effects.Tick -> WandPresentation.Tick -> PhysicalPresentation.Tick,
# Showing sees the completed raise one pass later; completion sees the cached
# rest elapsed time one pass later. These are handovers, not timeout slack.
PHASE_HANDOVER_MS = 2 * CONTROL_LOOP_PERIOD_MS
STEP_FIELDS = {'phase', 'duration_ms', 'target', 'light', 'vibration', 'motion',
               'audio', 'tone_hz', 'stage'}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def integer(value, minimum, maximum):
    return type(value) is int and minimum <= value <= maximum


def viewer_frame_count(steps):
    # Each phase consumes ceil(duration * 60 / 1000) intervals. Admission to
    # first rAF consumes one more interval, followed by one per phase handover.
    frames = sum((step['duration_ms'] * VIEWER_FRAMES_PER_SECOND + 999) // 1000
                 for step in steps)
    return frames + len(steps)


def presentation_budgets(steps):
    def rounded(duration_ms):
        return ((duration_ms + CONTROL_LOOP_PERIOD_MS - 1) //
                CONTROL_LOOP_PERIOD_MS) * CONTROL_LOOP_PERIOD_MS

    # Nominal 10 ms scheduling feasibility, not a guarantee under load. The
    # runtime deadline still aborts delayed control passes independently.
    physical_ms = 2 * MAX_CALIBRATED_TRAVEL_MS + PHASE_HANDOVER_MS
    physical_ms += sum(rounded(step['duration_ms']) for step in steps
                       if step['stage'] not in ('raise', 'lower'))
    # Nominal 60 Hz browser feasibility, including phase handovers. Preserve
    # the rational duration for exact boundary comparisons; slower frames still
    # abort through the viewer's independent runtime deadline.
    illustrative_ms = Fraction(viewer_frame_count(steps) * 1000, VIEWER_FRAMES_PER_SECOND)
    return physical_ms, illustrative_ms


def validate(score):
    # PhysicalEffects/BoardPins own these fixed hardware mappings. Reject edits
    # rather than showing a wiring/brightness change the firmware cannot use.
    require(integer(score['count'], 6, 6) and integer(score['leds_per_compartment'], 6, 6),
            'Firmware requires six compartments with six LEDs each')
    require(integer(score['max_brightness'], 64, 64), 'Firmware brightness is fixed at 64')
    channels = [{'compartment': n, 'servo': n - 1, 'vibration': n + 5,
                 'led_first': (n - 1) * 6, 'led_count': 6} for n in range(1, 7)]
    require(score['channels'] == channels, 'Unsupported firmware channel mapping')
    require(integer(score['illustrative_travel_ms'], 1, 65535),
            'Invalid illustrative travel duration')
    require(integer(score['show_ms'], 1, 65535), 'Invalid show duration')
    for name in ('main', 'short'):
        steps = score[name]
        require(5 <= len(steps) <= MAX_PREPARATION_STEPS + 4,
                f'{name}: requires 1..{MAX_PREPARATION_STEPS} preparation steps '
                'within the firmware scheduling allowance')
        stages = [step['stage'] for step in steps]
        require(stages == ['prepare'] * (len(steps) - 4) + list(STAGES[1:]),
                f'{name}: expected prepare steps followed by raise, show, lower, rest')
        for index, step in enumerate(steps):
            label = f'{name}[{index}]'
            require(not set(step) - STEP_FIELDS, f'{label}: unsupported step field')
            require(integer(step['duration_ms'], 1, 65535), f'{label}: invalid duration')
            target = step['target']
            require(target != 'all', f'{label}: only one LED segment may be targeted')
            require((type(target) is int and 1 <= target <= 6) or
                    (isinstance(target, str) and target in TARGETS), f'{label}: invalid target')
            require(isinstance(step['light'], list) and len(step['light']) == 3 and
                    all(integer(channel, 0, 255) for channel in step['light']),
                    f'{label}: invalid RGB light')
            require(type(step['vibration']) is bool, f'{label}: invalid vibration')
            stage = step['stage']
            expected_motion = stage if stage in ('raise', 'lower') else None
            require(step['motion'] == expected_motion, f'{label}: unsupported motion')
            tone = step.get('tone_hz', 0)
            # EspIdfVoiceAudio::Cue accepts only 80..2000 Hz. Scores outside
            # that range would play in the viewer but be skipped on hardware.
            require(integer(tone, 0, 2000) and ((step['audio'] is None and tone == 0) or
                    (step['audio'] == 'local_tone' and tone >= 80)),
                    f'{label}: unsupported audio/tone combination')
            if step['vibration']:
                require(stage == 'prepare' and target != 'all' and
                        step['duration_ms'] <= 150 and not steps[index + 1]['vibration'],
                        f'{label}: vibration requires one resting target and a pause')
            if stage != 'prepare':
                require(target == 'selected', f'{label}: phase must target selected compartment')
            if stage in ('raise', 'lower'):
                # Viewer travel is illustrative; the firmware always completes
                # motion after the separately provisioned servo calibration.
                require(step['duration_ms'] == score['illustrative_travel_ms'],
                        f'{label}: motion duration must equal illustrative_travel_ms; '
                        'firmware travel is calibration-owned')
            if stage == 'show':
                require(step['duration_ms'] == score['show_ms'],
                        f'{label}: show duration must equal show_ms')
            if stage == 'raise':
                require(tone == score['reveal_tone_hz'],
                        f'{label}: reveal tone must equal reveal_tone_hz')
            if stage == 'rest':
                require(step['light'] == [0, 0, 0] and step['audio'] is None,
                        f'{label}: rest requires lights and audio off')
        physical_ms, illustrative_ms = presentation_budgets(steps)
        require(physical_ms < MAX_PRESENTATION_MS,
                f'{name}: physical presentation budget {physical_ms} ms must be below '
                f'{MAX_PRESENTATION_MS} ms (maximum calibrated travel and nominal scheduling)')
        require(illustrative_ms < MAX_PRESENTATION_MS,
                f'{name}: illustrative viewer budget {float(illustrative_ms):.2f} ms '
                f'({viewer_frame_count(steps)} frames at nominal {VIEWER_FRAMES_PER_SECOND} Hz) '
                f'must be below {MAX_PRESENTATION_MS} ms')


def generate(score=None):
    if score is None:
        score = json.loads((ROOT / 'effects.json').read_text(encoding='utf-8'))
    validate(score)
    lines = ['// Generated by mechanika/final/generate_effects.py; edit effects.json.',
             '#pragma once', '#include <stdint.h>', 'namespace PresentationScore {',
             'enum class Stage : uint8_t { Prepare, Raise, Show, Lower, Rest };',
             '// Raise/Lower durationMs is viewer-only; firmware motion uses servo calibration.',
             'struct Step { Stage stage; uint16_t durationMs; int8_t target; uint8_t red, green, blue; bool vibration; uint16_t toneHz; };']
    for name in ('main', 'short'):
        steps = score[name]
        lines.append('constexpr Step ' + name.title() + '[] = {')
        for step in steps:
            target = TARGETS.get(step['target'], step['target'])
            lines.append('  {Stage::%s, %d, %d, %d, %d, %d, %s, %d},' % (
                step['stage'].title(), step['duration_ms'], target, *step['light'],
                str(step['vibration']).lower(), step.get('tone_hz', 0)))
        preparation_ms = sum(step['duration_ms'] for step in steps if step['stage'] == 'prepare')
        lines += ['};', f'constexpr uint32_t {name.title()}PreparationMs = {preparation_ms};']
    lines += [f'constexpr uint32_t ShowMs = {score["show_ms"]};', '}']
    return '\n'.join(lines) + '\n'


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    content = generate()
    if args.check:
        if HEADER.parent.exists():
            require(HEADER.read_text(encoding='utf-8') == content, 'Stale generated PresentationScore.h')
    else:
        HEADER.write_text(content, encoding='utf-8')
