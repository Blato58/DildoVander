"""Validate the shared viewer score and generate every firmware phase."""
import argparse
import difflib
import hashlib
import json
import re
from fractions import Fraction
from pathlib import Path

ROOT = Path(__file__).resolve().parent
FIRMWARE_ROOT = ROOT.parents[1] / 'firmware'
HEADER = FIRMWARE_ROOT / 'src/features/physical_effects/PresentationScore.h'
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
SOURCE = ROOT / 'effects.json'
SCHEMA = ROOT / 'effects.schema.json'
PLAN = ROOT / 'effects.plan.json'
MANIFEST = ROOT / 'effects.manifest.json'
SEMANTICS_VERSION = 'dv02-phase-1'
MAX_SOURCE_BYTES = 256 * 1024
MAX_JSON_DEPTH = 16
MAX_SCENES = 16
MAX_PHASES = 64
MAX_CUES = 256
MAX_PHASE_CUES = 8
MAX_ACCENT_PHASES = 8
CAPABILITY_BITS = {'led': 1, 'vibration': 2, 'audio.tone': 4,
                   'calibrated-motion': 8, 'upstream-dv11': 16}
PROFILE = {'name': 'profile-b-single', 'maxDurationMs': MAX_PRESENTATION_MS,
           'maxBrightness': 64, 'maxVibrationMs': 150, 'maxTravelMs': 3000, 'maxToneMs': 200,
           'maxLedGroups': 1, 'calibration': 'runtime-provisioned-separate-identity',
           'legacyMaxPhases': MAX_PREPARATION_STEPS + 4,
           'maxPhases': MAX_PHASES, 'maxCues': MAX_CUES,
           'maxPhaseCues': MAX_PHASE_CUES, 'maxAccentPhases': MAX_ACCENT_PHASES}


def canonical(value):
    """Project encoding: sorted keys, compact separators, UTF-8, one final LF."""
    return (json.dumps(value, ensure_ascii=False, sort_keys=True,
                       separators=(',', ':'), allow_nan=False) + '\n').encode('utf-8')


def digest(value):
    return hashlib.sha256(value).hexdigest()


def parse_source(raw):
    """Bound bytes/depth before json allocation; JSON integers only, no coercion."""
    require(isinstance(raw, bytes) and len(raw) <= MAX_SOURCE_BYTES,
            'source exceeds byte limit')
    try:
        text = raw.decode('utf-8', errors='strict')
    except UnicodeError as error:
        raise ValueError('source requires valid UTF-8') from error
    depth = 0
    quoted = escaped = False
    for character in text:
        if quoted:
            if escaped:
                escaped = False
            elif character == '\\':
                escaped = True
            elif character == '"':
                quoted = False
        elif character == '"':
            quoted = True
        elif character in '[{':
            depth += 1
            require(depth <= MAX_JSON_DEPTH, 'source exceeds depth limit')
        elif character in ']}':
            depth -= 1

    def pairs(items):
        result = {}
        for key, value in items:
            require(key not in result, f'duplicate JSON key: {key}')
            result[key] = value
        return result

    def number(token):
        require(len(token) <= 10, 'integer token exceeds limit')
        value = int(token)
        require(-2147483648 <= value <= 4294967295, 'integer overflow')
        return value

    def unsupported_number(token):
        raise ValueError(f'non-integer JSON number: {token[:32]}')

    try:
        return json.loads(text, object_pairs_hook=pairs, parse_int=number,
                          parse_float=unsupported_number, parse_constant=unsupported_number)
    except (json.JSONDecodeError, RecursionError, UnicodeError) as error:
        raise ValueError('invalid JSON source') from error


def read_source(path):
    # Do not read an untrusted whole file before discovering it exceeds bounds.
    with Path(path).open('rb') as stream:
        raw = stream.read(MAX_SOURCE_BYTES + 1)
    require(len(raw) <= MAX_SOURCE_BYTES, 'source exceeds byte limit')
    return raw


def object_schema(properties, required=None):
    return {'type': 'object', 'properties': properties, 'additionalProperties': False,
            'required': list(properties) if required is None else required}


def integer_schema(minimum=0, maximum=65535):
    return {'type': 'integer', 'minimum': minimum, 'maximum': maximum}


def array_schema(items, minimum=0, maximum=MAX_CUES):
    return {'type': 'array', 'items': items, 'minItems': minimum, 'maxItems': maximum}


def enum_schema(*values):
    return {'enum': list(values)}


def scene_schema():
    identity = {'type': 'string', 'minLength': 1, 'maxLength': 64,
                'pattern': '^[a-z][a-z0-9-]*$'}
    rgb = array_schema(integer_schema(0, 255), 3, 3)
    group = {'oneOf': [object_schema({'binding': enum_schema('selection', 'next', 'previous')}),
                       object_schema({'slot': integer_schema(1, 6)})]}
    timing = object_schema({'offsetMs': integer_schema(), 'end': {'oneOf': [
        object_schema({'kind': enum_schema('phase-end')}),
        object_schema({'kind': enum_schema('duration'), 'durationMs': integer_schema(1)})]}})
    common = {'id': identity, 'timing': timing,
              'importance': enum_schema('required', 'optional'),
              'onUnavailable': enum_schema('reject', 'skip'),
              'latePolicy': enum_schema('sample-current', 'skip-if-late', 'abort-show')}
    cue_types = []
    for kind in ('led.solid', 'led.fade', 'vibration.pulse', 'audio.tone'):
        fields = dict(common, kind=enum_schema(kind))
        if kind.startswith('led.'):
            fields.update(group=group, rgb=rgb, level=integer_schema(0, 64))
            if kind == 'led.fade':
                fields['fromRgb'] = rgb
        elif kind == 'vibration.pulse':
            fields['group'] = group
        else:
            fields['frequencyHz'] = integer_schema(80, 2000)
        cue_types.append(object_schema(fields))
    completion = {'oneOf': [
        object_schema({'kind': enum_schema('elapsed'), 'durationMs': integer_schema(1)}),
        object_schema({'kind': enum_schema('calibrated-move'),
                       'direction': enum_schema('raise', 'lower'),
                       'target': enum_schema('selection')})]}
    phase = object_schema({'id': identity, 'completion': completion,
                           'cues': array_schema({'oneOf': cue_types}, 0, MAX_PHASE_CUES)})
    capabilities = array_schema(enum_schema(*CAPABILITY_BITS), 0, len(CAPABILITY_BITS))
    scene = object_schema({'schemaVersion': enum_schema(1), 'sceneId': identity,
                           'sceneVersion': integer_schema(1), 'variant': identity,
                           # V1's sole physical presentation routes are the
                           # existing main/short owner adapters. Do not accept
                           # a new scene that firmware cannot actually execute.
                           'kind': enum_schema('accent'),
                           'maxDurationMs': integer_schema(1, MAX_PRESENTATION_MS),
                           'trigger': enum_schema('explicit-only'), 'defaultEnabled': enum_schema(False),
                           'resourcePolicy': enum_schema('profile-b-single'),
                           'requiredCapabilities': capabilities, 'optionalCapabilities': capabilities,
                           'phases': array_schema(phase, 1, MAX_PHASES)})
    step_properties = {'phase': {'type': 'string', 'minLength': 1, 'maxLength': 128},
                       'duration_ms': integer_schema(1),
                       'target': {'oneOf': [integer_schema(1, 6), enum_schema(*TARGETS)]},
                       'light': rgb, 'vibration': {'type': 'boolean'},
                       'motion': enum_schema(None, 'raise', 'lower'),
                       'audio': enum_schema(None, 'local_tone'), 'tone_hz': integer_schema(0, 2000),
                       'stage': enum_schema(*STAGES)}
    step = object_schema(step_properties, sorted(STEP_FIELDS - {'tone_hz'}))
    channel = object_schema({name: integer_schema(0, 36) for name in
                             ('compartment', 'servo', 'vibration', 'led_first', 'led_count')})
    string = {'type': 'string', 'minLength': 1, 'maxLength': 1024}
    schema = object_schema({'schemaVersion': enum_schema(1), 'sceneVersion': integer_schema(1),
                            'revision': string, 'count': integer_schema(6, 6),
                            'leds_per_compartment': integer_schema(6, 6),
                            'max_brightness': integer_schema(64, 64),
                            'main': array_schema(step, 5, MAX_PREPARATION_STEPS + 4),
                            'short': array_schema(step, 5, MAX_PREPARATION_STEPS + 4),
                            'show_ms': integer_schema(1), 'reveal_tone_hz': integer_schema(80, 2000),
                            'calibrated': enum_schema(False),
                            'illustrative_travel_ms': integer_schema(1),
                            'audio_status': string, 'stop': string, 'transport': string,
                            'channels': array_schema(channel, 6, 6),
                            'scenes': array_schema(scene, 0, MAX_SCENES - 2)})
    schema['$schema'] = 'https://json-schema.org/draft/2020-12/schema'
    schema['$id'] = 'urn:dildovander:effects:1'
    schema['description'] = ('V1 authors main/short legacy presentation adapters and explicit opt-in accents; '
                             'new arbitrary presentation scenes are unsupported and rejected.')
    return schema


def validate_structure(value, schema, path='$'):
    """Evaluate the closed Draft 2020-12 subset emitted by scene_schema()."""
    if 'oneOf' in schema:
        matches = 0
        for variant in schema['oneOf']:
            try:
                validate_structure(value, variant, path)
                matches += 1
            except ValueError:
                pass
        require(matches == 1, f'{path}: invalid union variant')
    if 'enum' in schema:
        require(any(type(value) is type(item) and value == item for item in schema['enum']),
                f'{path}: unsupported value')
    kind = schema.get('type')
    if kind == 'object':
        require(type(value) is dict, f'{path}: expected object')
        require(not set(value) - set(schema['properties']), f'{path}: unknown field')
        require(set(schema['required']) <= set(value), f'{path}: missing required field')
        for key, item in value.items():
            validate_structure(item, schema['properties'][key], f'{path}.{key}')
    elif kind == 'array':
        require(type(value) is list, f'{path}: expected array')
        require(schema['minItems'] <= len(value) <= schema['maxItems'], f'{path}: array count limit')
        for index, item in enumerate(value):
            validate_structure(item, schema['items'], f'{path}[{index}]')
    elif kind == 'integer':
        require(integer(value, schema['minimum'], schema['maximum']), f'{path}: invalid integer')
    elif kind == 'boolean':
        require(type(value) is bool, f'{path}: expected boolean')
    elif kind == 'string':
        require(type(value) is str and schema['minLength'] <= len(value) <= schema['maxLength'],
                f'{path}: invalid string')
        require(not any(0xD800 <= ord(character) <= 0xDFFF for character in value),
                f'{path}: invalid Unicode scalar')
        if 'pattern' in schema:
            require(re.fullmatch(schema['pattern'], value) is not None, f'{path}: invalid identity')


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


def legacy_scene(score, name):
    """Lossless adapter for the historically authored arrays, never a second source."""
    phases = []
    start_ms = 0
    for index, step in enumerate(score[name]):
        phase_id = f'{name}-{index:03d}'
        motion = step['motion']
        completion = ({'kind': 'calibrated-move', 'direction': motion, 'target': 'selection'}
                      if motion else {'kind': 'elapsed', 'durationMs': step['duration_ms']})
        target = step['target']
        group = ({'slot': target} if type(target) is int else
                 {'binding': 'selection' if target == 'selected' else target})
        cues = [{'id': phase_id + '-light', 'kind': 'led.solid', 'group': group,
                 'rgb': step['light'], 'level': score['max_brightness'],
                 'timing': {'offsetMs': 0, 'end': {'kind': 'phase-end'}},
                 'importance': 'required', 'onUnavailable': 'reject', 'latePolicy': 'sample-current'}]
        if step['vibration']:
            cues.append({'id': phase_id + '-pulse', 'kind': 'vibration.pulse', 'group': group,
                         'timing': {'offsetMs': 0, 'end': {'kind': 'duration',
                                                        'durationMs': step['duration_ms']}},
                         'importance': 'required', 'onUnavailable': 'reject',
                         'latePolicy': 'skip-if-late'})
        if step.get('tone_hz', 0):
            cues.append({'id': phase_id + '-tone', 'kind': 'audio.tone',
                         'frequencyHz': step['tone_hz'],
                         'timing': {'offsetMs': 0, 'end': {'kind': 'duration',
                                                        'durationMs': 180 if motion == 'raise' else 100}},
                         'importance': 'optional', 'onUnavailable': 'skip',
                         'latePolicy': 'skip-if-late'})
        phases.append({'id': phase_id, 'label': step['phase'], 'completion': completion,
                       'illustrativeDurationMs': step['duration_ms'],
                       'illustrativeStartMs': start_ms, 'cues': cues})
        start_ms += step['duration_ms']
    remaining_ms = PHASE_HANDOVER_MS
    for phase in reversed(phases):
        completion = phase['completion']
        duration = (MAX_CALIBRATED_TRAVEL_MS if completion['kind'] == 'calibrated-move'
                    else completion['durationMs'])
        remaining_ms += ((duration + CONTROL_LOOP_PERIOD_MS - 1) // CONTROL_LOOP_PERIOD_MS) * CONTROL_LOOP_PERIOD_MS
        phase['worstCaseRemainingMs'] = remaining_ms
    return {'schemaVersion': 1, 'sceneId': 'legacy-presentation',
            'sceneVersion': score['sceneVersion'], 'variant': name, 'kind': 'presentation',
            'maxDurationMs': MAX_PRESENTATION_MS, 'resourcePolicy': PROFILE['name'],
            'requiredCapabilities': ['calibrated-motion', 'led', 'vibration'],
            'optionalCapabilities': ['audio.tone'], 'phases': phases,
            'worstCaseDurationMs': presentation_budgets(score[name])[0],
            'illustrativeDurationMs': start_ms, 'legacyAdapter': True}


def validate_scene(scene):
    phases = scene['phases']
    legacy = scene.get('legacyAdapter', False)
    require(1 <= len(phases) <= (MAX_PREPARATION_STEPS + 4 if legacy else MAX_PHASES),
            'expanded phase count limit')
    require(scene['kind'] != 'accent' or len(phases) <= MAX_ACCENT_PHASES,
            'accent phase count limit')
    phase_ids = set()
    cue_ids = set()
    moves = []
    derived = {'required': set(), 'optional': set()}
    if scene['sceneId'] == 'special-referral':
        derived['required'].add('upstream-dv11')
    total_ms = 0
    start_ms = 0
    previous_pulse_at_boundary = False
    for phase_index, phase in enumerate(phases):
        require(phase['id'] not in phase_ids, 'duplicate phase ID')
        phase_ids.add(phase['id'])
        completion = phase['completion']
        moving = completion['kind'] == 'calibrated-move'
        duration = MAX_CALIBRATED_TRAVEL_MS if moving else completion['durationMs']
        if moving:
            require(scene['kind'] == 'presentation', 'accent cannot contain calibrated-move')
            require(completion['target'] == 'selection', 'motion requires selection target')
            moves.append((phase_index, completion['direction']))
            derived['required'].add('calibrated-motion')
        total_ms += ((duration + 9) // 10) * 10
        led_intervals = []
        pulse_intervals = []
        tone_intervals = []
        for cue in phase['cues']:
            require(cue['id'] not in cue_ids, 'duplicate cue ID')
            cue_ids.add(cue['id'])
            require(len(cue_ids) <= MAX_CUES, 'expanded cue count limit')
            kind = cue['kind']
            capability = ('led' if kind.startswith('led.') else
                          'vibration' if kind == 'vibration.pulse' else 'audio.tone')
            derived[cue['importance']].add(capability)
            require(cue['onUnavailable'] == ('reject' if cue['importance'] == 'required' else 'skip'),
                    'importance and unavailable policy conflict')
            offset = cue['timing']['offsetMs']
            end = cue['timing']['end']
            phase_end = end['kind'] == 'phase-end'
            require(offset < duration, 'cue offset outside phase')
            end_ms = duration if phase_end else offset + end['durationMs']
            if not moving:
                require(end_ms <= duration, 'cue exceeds phase boundary')
            if kind.startswith('led.'):
                require(cue['level'] <= PROFILE['maxBrightness'], 'LED brightness exceeds profile')
                require(not (kind == 'led.fade' and moving and phase_end),
                        'calibrated fade requires explicit finite interpolation duration')
                led_intervals.append((offset, end_ms))
            else:
                require(not phase_end, 'tone and pulse require finite duration')
                require(cue['latePolicy'] != 'sample-current', 'pulse and tone cannot catch up')
                if kind == 'vibration.pulse':
                    require(not moving, 'vibration overlaps calibrated move')
                    require(end['durationMs'] <= PROFILE['maxVibrationMs'], 'vibration duration exceeds profile')
                    pulse_intervals.append((offset, end_ms))
                else:
                    require(end['durationMs'] <= PROFILE['maxToneMs'], 'tone duration exceeds audio port limit')
                    tone_intervals.append((offset, end_ms))
        for intervals, label in ((led_intervals, 'LED groups'), (pulse_intervals, 'vibration'),
                                 (tone_intervals, 'audio tones')):
            ordered = sorted(intervals)
            require(all(left[1] <= right[0] for left, right in zip(ordered, ordered[1:])),
                    f'overlapping {label}')
        ordered_pulses = sorted(pulse_intervals)
        require(all(left[1] < right[0] for left, right in zip(ordered_pulses, ordered_pulses[1:])),
                'vibration pulses require a pause')
        require(not (previous_pulse_at_boundary and ordered_pulses and ordered_pulses[0][0] == 0),
                'vibration pulses require a pause across phases')
        previous_pulse_at_boundary = bool(ordered_pulses and ordered_pulses[-1][1] == duration)
        if not legacy:
            illustrative = duration if not moving else 0
            # A new movement has no invented calibration. Viewer marks its
            # duration unknown; only legacy has historical illustrative travel.
            phase['illustrativeDurationMs'] = illustrative
            phase['illustrativeStartMs'] = start_ms
            start_ms += illustrative
    require(scene['kind'] != 'presentation' or [direction for _, direction in moves] == ['raise', 'lower'],
            'presentation requires exactly one raise then lower')
    if moves:
        raise_index, lower_index = moves[0][0], moves[1][0]
        require(raise_index > 0 and lower_index > raise_index + 1 and lower_index < len(phases) - 1,
                'presentation requires prepare, show and final rest')
        rest = phases[-1]
        require(rest['completion']['kind'] == 'elapsed' and
                all(cue['kind'] == 'led.solid' and cue['rgb'] == [0, 0, 0] for cue in rest['cues']),
                'final rest requires outputs off')
    for importance, declared_name in (('required', 'requiredCapabilities'), ('optional', 'optionalCapabilities')):
        declared = scene[declared_name]
        require(len(declared) == len(set(declared)), 'duplicate capability')
        # A capability needed by any required cue is reserved as required.
        expected = derived[importance]
        if importance == 'optional':
            expected -= derived['required']
        if legacy:
            require(expected <= set(declared), 'legacy capability envelope mismatch')
        else:
            require(expected == set(declared), 'declared capability envelope mismatch')
    require(not set(scene['requiredCapabilities']) & set(scene['optionalCapabilities']),
            'required/optional capability conflict')
    if not legacy:
        # Bound transition and terminal cleanup work as one nominal pass each.
        scene['worstCaseDurationMs'] = total_ms + (len(phases) + 1) * CONTROL_LOOP_PERIOD_MS
        scene['illustrativeDurationMs'] = start_ms
    require(scene['worstCaseDurationMs'] < scene['maxDurationMs'], 'scene cannot meet hard deadline')


def build_plan(score):
    validate_structure(score, scene_schema())
    validate(score)
    scenes = [legacy_scene(score, name) for name in ('main', 'short')]
    # Copy JSON data so normalization never mutates the caller's author source.
    scenes.extend(json.loads(json.dumps(score['scenes'], ensure_ascii=False)))
    identities = set()
    accent_ids = set()
    scene_kinds = {}
    for scene in scenes:
        identity = (scene['sceneId'], scene['variant'])
        require(identity not in identities, 'duplicate scene identity/variant')
        identities.add(identity)
        require(scene['sceneId'] not in scene_kinds or scene_kinds[scene['sceneId']] == scene['kind'],
                'scene ID cannot change physical kind')
        scene_kinds[scene['sceneId']] = scene['kind']
        if scene['kind'] == 'accent':
            # The v1 physical owner admits accents by sceneId. Two variants
            # with that ID would otherwise be silently selected by array order.
            require(scene['sceneId'] not in accent_ids, 'duplicate accent scene ID')
            accent_ids.add(scene['sceneId'])
        validate_scene(scene)
        scene['sceneHash'] = digest(canonical(scene))
    return {'semanticsVersion': SEMANTICS_VERSION,
            'presentationCatalog': 'legacy-main-short-only; explicit-new-presentations-unsupported',
            'timeUnit': 'integer-milliseconds', 'intervals': 'half-open',
            'calibrationIdentity': 'runtime-provisioned-separate-identity',
            'ledNormalization': 'legacy-rgb-unscaled-level-applied-once-floor-channel-times-level-div-255',
            'fadeInterpolation': 'from-plus-truncate-toward-zero-delta-times-elapsed-div-duration-then-level-once',
            'limits': PROFILE, 'scenes': scenes}


def render_header(score, manifest, plan):
    lines = ['// Generated by mechanika/final/generate_effects.py; edit effects.json.',
             '#pragma once', '#include <stdint.h>', 'namespace PresentationScore {',
             'enum class Stage : uint8_t { Prepare, Raise, Show, Lower, Rest };',
             '// Raise/Lower durationMs is viewer-only; firmware motion uses servo calibration.',
             'struct Step { Stage stage; uint16_t durationMs; int8_t target; uint8_t red, green, blue; bool vibration; uint16_t toneHz; };']
    for field in ('sourceHash', 'compiledHash', 'bundleHash', 'semanticsVersion'):
        name = field[0].upper() + field[1:]
        lines.append(f'constexpr char {name}[] = "{manifest[field]}";')
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
        scene = next(scene for scene in plan['scenes'] if scene['sceneId'] == 'legacy-presentation' and scene['variant'] == name)
        prefix = name.title()
        lines.append(f'constexpr const char* {prefix}PhaseIds[] = {{' +
                     ', '.join('"' + phase['id'] + '"' for phase in scene['phases']) + '};')
        lines.append(f'constexpr uint32_t {prefix}RemainingMs[] = {{' +
                     ', '.join(str(phase['worstCaseRemainingMs']) for phase in scene['phases']) + '};')
        lines += [f'constexpr char {prefix}SceneId[] = "{scene["sceneId"]}";',
                  f'constexpr char {prefix}SceneHash[] = "{scene["sceneHash"]}";',
                  f'constexpr uint32_t {prefix}SceneVersion = {scene["sceneVersion"]};',
                  f'constexpr uint32_t {prefix}WorstCaseMs = {scene["worstCaseDurationMs"]};',
                  f'constexpr uint32_t {prefix}DeadlineMs = {scene["maxDurationMs"]};',
                  f'constexpr uint32_t {prefix}PhaseCount = {len(steps)};']
    lines += [f'constexpr uint32_t ShowMs = {score["show_ms"]};']
    lines.extend(render_accents(plan))
    lines.append('}')
    return '\n'.join(lines) + '\n'


def render_accents(plan):
    accents = [scene for scene in plan['scenes'] if scene['kind'] == 'accent']
    lines = ['enum class CueKind : uint8_t { LedSolid, LedFade, VibrationPulse, AudioTone };',
             'enum class LatePolicy : uint8_t { SampleCurrent, SkipIfLate, AbortShow };',
             'struct SceneCue { CueKind kind; uint16_t offsetMs, durationMs; bool phaseEnd; int8_t target; uint8_t red, green, blue, fromRed, fromGreen, fromBlue, level; uint16_t frequencyHz; bool required; LatePolicy latePolicy; };',
             'struct ScenePhase { uint16_t durationMs; uint16_t cueOffset; uint8_t cueCount; const char* phaseId; };',
             'struct AccentScene { const char* sceneId; const char* variant; uint32_t sceneVersion; uint16_t maxDurationMs; uint16_t phaseOffset; uint8_t phaseCount; uint8_t requiredCapabilities, optionalCapabilities; const char* sceneHash; };',
             'constexpr uint8_t CapabilityLed = 1, CapabilityVibration = 2, CapabilityAudioTone = 4, CapabilityCalibratedMotion = 8, CapabilityUpstreamDv11 = 16;',
             'constexpr SceneCue AccentCues[] = {']
    cue_rows = []
    phase_rows = []
    scene_rows = []
    kind_names = {'led.solid': 'LedSolid', 'led.fade': 'LedFade',
                  'vibration.pulse': 'VibrationPulse', 'audio.tone': 'AudioTone'}
    policies = {'sample-current': 'SampleCurrent', 'skip-if-late': 'SkipIfLate', 'abort-show': 'AbortShow'}
    for scene in accents:
        phase_offset = len(phase_rows)
        for phase in scene['phases']:
            cue_offset = len(cue_rows)
            for cue in phase['cues']:
                end = cue['timing']['end']
                group = cue.get('group', {'binding': 'selection'})
                target = group.get('slot', TARGETS.get(group.get('binding'), 0))
                rgb = cue.get('rgb', [0, 0, 0])
                from_rgb = cue.get('fromRgb', rgb)
                values = [f'CueKind::{kind_names[cue["kind"]]}', str(cue['timing']['offsetMs']),
                          str(end.get('durationMs', 0)), str(end['kind'] == 'phase-end').lower(),
                          str(target), *(str(value) for value in rgb + from_rgb),
                          str(cue.get('level', 0)), str(cue.get('frequencyHz', 0)),
                          str(cue['importance'] == 'required').lower(),
                          f'LatePolicy::{policies[cue["latePolicy"]]}']
                cue_rows.append('  {' + ', '.join(values) + '},')
            phase_rows.append('  {%d, %d, %d, "%s"},' %
                              (phase['completion']['durationMs'], cue_offset, len(phase['cues']), phase['id']))
        required = sum(CAPABILITY_BITS[item] for item in scene['requiredCapabilities'])
        optional = sum(CAPABILITY_BITS[item] for item in scene['optionalCapabilities'])
        scene_rows.append('  {"%s", "%s", %d, %d, %d, %d, %d, %d, "%s"},' %
                          (scene['sceneId'], scene['variant'], scene['sceneVersion'],
                           scene['maxDurationMs'], phase_offset, len(scene['phases']), required, optional,
                           scene['sceneHash']))
    # Standard C++ has no zero-sized arrays; the count hides inert sentinels.
    lines += cue_rows or ['  {CueKind::LedSolid, 0, 0, true, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, LatePolicy::SkipIfLate},']
    lines += ['};', 'constexpr ScenePhase AccentPhases[] = {']
    lines += phase_rows or ['  {0, 0, 0, ""},']
    lines += ['};', 'constexpr AccentScene Accents[] = {']
    lines += scene_rows or ['  {"", "", 0, 0, 0, 0, 0, 0, ""},']
    lines += ['};', f'constexpr uint8_t AccentCount = {len(accents)};']
    return lines


def compile_bundle(raw):
    score = parse_source(raw)
    plan = build_plan(score)
    schema_bytes = canonical(scene_schema())
    plan_bytes = canonical(plan)
    manifest = {'manifestVersion': 1, 'semanticsVersion': SEMANTICS_VERSION,
                'encoding': 'UTF-8 sorted keys compact JSON one trailing LF; no Unicode normalization',
                'sourceHash': digest(raw), 'schemaHash': digest(schema_bytes),
                'compilerHash': digest(Path(__file__).read_bytes()),
                'profileHash': digest(canonical(PROFILE)), 'compiledHash': digest(plan_bytes),
                'calibrationIdentity': 'runtime-provisioned-separate-identity'}
    # Hash the non-self-referential identity components, then expose the digest.
    manifest['bundleHash'] = digest(canonical(manifest))
    return {HEADER: render_header(score, manifest, plan).encode('utf-8'),
            SCHEMA: schema_bytes, PLAN: plan_bytes, MANIFEST: canonical(manifest)}


def generate(score=None):
    raw = read_source(SOURCE) if score is None else canonical(score)
    return compile_bundle(raw)[HEADER].decode('utf-8')


def firmware_context_present():
    # Manufacturing ZIPs contain only final/. A repository/worktree still
    # requires its header even when that file or its directory was deleted.
    repository_marker = len(ROOT.parents) > 1 and (ROOT.parents[1] / '.git').exists()
    return FIRMWARE_ROOT.exists() or repository_marker


def export_bundle(raw, check=False):
    outputs = compile_bundle(raw)
    for path, content in outputs.items():
        if check:
            if path == HEADER and not firmware_context_present():
                continue
            require(path.is_file() and path.read_bytes() == content, f'Stale generated {path.name}')
        else:
            path.write_bytes(content)


def author_diff(before_raw, after_raw):
    before = build_plan(parse_source(before_raw))
    after = build_plan(parse_source(after_raw))
    return ''.join(difflib.unified_diff(
        json.dumps(before, ensure_ascii=False, sort_keys=True, indent=2).splitlines(keepends=True),
        json.dumps(after, ensure_ascii=False, sort_keys=True, indent=2).splitlines(keepends=True),
        fromfile='baseline-plan', tofile='candidate-plan'))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument('--check', action='store_true')
    mode.add_argument('--diff', type=Path, metavar='BASELINE_SOURCE',
                      help='Print the semantic authoring diff without writing exports')
    args = parser.parse_args()
    raw = read_source(SOURCE)
    if args.diff:
        print(author_diff(read_source(args.diff), raw), end='')
    else:
        export_bundle(raw, check=args.check)
