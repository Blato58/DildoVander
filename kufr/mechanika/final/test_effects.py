import copy
import json
import unittest
from fractions import Fraction
from pathlib import Path
from generate_effects import generate, HEADER, MAX_PREPARATION_STEPS, presentation_budgets, viewer_frame_count

ROOT = Path(__file__).resolve().parent


class ChoreographyContract(unittest.TestCase):
    def setUp(self):
        self.score = json.loads((ROOT / 'effects.json').read_text(encoding='utf-8'))

    def phase(self, score, variant, stage):
        return next(step for step in score[variant] if step['stage'] == stage)

    def test_all_phase_colors_are_generated_or_rejected(self):
        baseline = generate(self.score)
        for variant in ('main', 'short'):
            for stage in ('raise', 'show', 'lower', 'rest'):
                with self.subTest(variant=variant, stage=stage):
                    changed = copy.deepcopy(self.score)
                    self.phase(changed, variant, stage)['light'] = [21, 32, 43]
                    if stage == 'rest':
                        with self.assertRaisesRegex(ValueError, 'rest requires lights and audio off'):
                            generate(changed)
                    else:
                        self.assertNotEqual(generate(changed), baseline)
                        self.assertIn(f'Stage::{stage.title()}', generate(changed))

    def test_post_preparation_durations_are_generated_or_rejected(self):
        baseline = generate(self.score)
        for variant in ('main', 'short'):
            for stage in ('raise', 'show', 'lower', 'rest'):
                with self.subTest(variant=variant, stage=stage):
                    changed = copy.deepcopy(self.score)
                    self.phase(changed, variant, stage)['duration_ms'] += 123
                    if stage == 'rest':
                        self.assertNotEqual(generate(changed), baseline)
                    else:
                        with self.assertRaisesRegex(ValueError, 'duration must equal'):
                            generate(changed)
        changed = copy.deepcopy(self.score)
        changed['show_ms'] += 123
        for variant in ('main', 'short'):
            self.phase(changed, variant, 'show')['duration_ms'] = changed['show_ms']
        self.assertIn(f'ShowMs = {changed["show_ms"]}', generate(changed))
        # Changing illustrative travel updates the generated metadata but never
        # replaces the calibration-owned duration of a physical servo command.
        changed = copy.deepcopy(self.score)
        changed['illustrative_travel_ms'] += 123
        for variant in ('main', 'short'):
            for stage in ('raise', 'lower'):
                self.phase(changed, variant, stage)['duration_ms'] = changed['illustrative_travel_ms']
        self.assertNotEqual(generate(changed), baseline)
        self.assertIn('firmware motion uses servo calibration', generate(changed))

    def test_post_preparation_audio_is_generated_or_rejected(self):
        baseline = generate(self.score)
        changed = copy.deepcopy(self.score)
        changed['reveal_tone_hz'] = 990
        for variant in ('main', 'short'):
            self.phase(changed, variant, 'raise')['tone_hz'] = 990
        self.assertNotEqual(generate(changed), baseline)
        for variant in ('main', 'short'):
            for stage in ('raise', 'show', 'lower', 'rest'):
                with self.subTest(variant=variant, stage=stage):
                    changed = copy.deepcopy(self.score)
                    step = self.phase(changed, variant, stage)
                    step.update(audio='local_tone', tone_hz=990)
                    if stage in ('raise', 'rest'):
                        with self.assertRaises(ValueError):
                            generate(changed)
                    else:
                        self.assertNotEqual(generate(changed), baseline)
                    step['audio'] = 'cloud_audio'
                    with self.assertRaisesRegex(ValueError, 'unsupported audio/tone'):
                        generate(changed)

    def test_unsafe_or_unsupported_phases_fail_closed(self):
        for variant in ('main', 'short'):
            for stage in ('raise', 'show', 'lower', 'rest'):
                for field, value in (('target', 'all'), ('vibration', True),
                                     ('motion', 'unsupported'), ('stage', 'unsupported'),
                                     ('duration_ms', 0), ('tone_hz', 70000),
                                     ('unknown_effect', True)):
                    with self.subTest(variant=variant, stage=stage, field=field):
                        changed = copy.deepcopy(self.score)
                        self.phase(changed, variant, stage)[field] = value
                        with self.assertRaises(ValueError):
                            generate(changed)
            changed = copy.deepcopy(self.score)
            changed[variant][-3], changed[variant][-2] = changed[variant][-2], changed[variant][-3]
            with self.assertRaisesRegex(ValueError, 'expected prepare steps'):
                generate(changed)

    def test_local_tones_match_the_physical_audio_port_range(self):
        for variant in ('main', 'short'):
            for tone in (80, 2000):
                with self.subTest(variant=variant, tone=tone):
                    changed = copy.deepcopy(self.score)
                    changed[variant][0].update(audio='local_tone', tone_hz=tone)
                    generate(changed)
            for tone in (0, 79, 2001, 4000):
                with self.subTest(variant=variant, tone=tone):
                    changed = copy.deepcopy(self.score)
                    changed[variant][0].update(audio='local_tone', tone_hz=tone)
                    with self.assertRaisesRegex(ValueError, 'unsupported audio/tone'):
                        generate(changed)
            changed = copy.deepcopy(self.score)
            changed[variant][0].update(audio=None, tone_hz=0)
            generate(changed)

    def test_preparation_step_count_fits_firmware_scheduling_allowance(self):
        self.assertEqual(MAX_PREPARATION_STEPS, 100)
        for variant in ('main', 'short'):
            for count in (1, 100, 101, 251):
                with self.subTest(variant=variant, count=count):
                    changed = copy.deepcopy(self.score)
                    preparation = copy.deepcopy(changed[variant][0])
                    preparation.update(duration_ms=1, vibration=False, audio=None, tone_hz=0)
                    changed[variant] = [copy.deepcopy(preparation) for _ in range(count)] + changed[variant][-4:]
                    if count <= MAX_PREPARATION_STEPS:
                        self.assertIn(f'{variant.title()}PreparationMs = {count}', generate(changed))
                    else:
                        with self.assertRaisesRegex(ValueError, 'firmware scheduling allowance'):
                            generate(changed)

    def test_current_variants_fit_maximum_calibration_and_viewer_budgets(self):
        self.assertEqual(presentation_budgets(self.score['main']), (14790, 11200))
        self.assertEqual(presentation_budgets(self.score['short']), (11970, Fraction(24200, 3)))
        self.assertEqual(viewer_frame_count(self.score['main']), 672)
        self.assertEqual(viewer_frame_count(self.score['short']), 484)
        generate(self.score)

    def test_physical_budget_rejects_overlong_show_preparation_and_rest(self):
        changed = copy.deepcopy(self.score)
        changed['show_ms'] = 20000
        for variant in ('main', 'short'):
            self.phase(changed, variant, 'show')['duration_ms'] = 20000
        with self.assertRaisesRegex(ValueError, 'main: physical presentation budget .* below 15000'):
            generate(changed)
        for variant in ('main', 'short'):
            for stage in ('prepare', 'rest'):
                with self.subTest(variant=variant, stage=stage):
                    changed = copy.deepcopy(self.score)
                    self.phase(changed, variant, stage)['duration_ms'] = 15000
                    with self.assertRaisesRegex(ValueError, f'{variant}: physical presentation budget'):
                        generate(changed)

    def test_physical_budget_rounding_and_strict_boundary_for_both_variants(self):
        for variant, last_valid_rest in (('main', 450), ('short', 3270)):
            for rest_ms, allowed in ((last_valid_rest, True),
                                     (last_valid_rest + 1, False),
                                     (last_valid_rest + 10, False)):
                with self.subTest(variant=variant, rest_ms=rest_ms):
                    changed = copy.deepcopy(self.score)
                    self.phase(changed, variant, 'rest')['duration_ms'] = rest_ms
                    if allowed:
                        self.assertEqual(presentation_budgets(changed[variant])[0], 14990)
                        generate(changed)
                    else:
                        self.assertEqual(presentation_budgets(changed[variant])[0], 15000)
                        with self.assertRaisesRegex(ValueError, f'{variant}: physical presentation budget 15000 ms'):
                            generate(changed)

    def test_viewer_budget_is_independent_of_calibrated_motion_budget(self):
        changed = copy.deepcopy(self.score)
        changed['illustrative_travel_ms'] = 5000
        for variant in ('main', 'short'):
            for stage in ('raise', 'lower'):
                self.phase(changed, variant, stage)['duration_ms'] = 5000
        self.assertEqual(presentation_budgets(changed['main'])[0], 14790)
        with self.assertRaisesRegex(ValueError, 'main: illustrative viewer budget .* frames at nominal 60 Hz'):
            generate(changed)

        # Keep main and both physical budgets safe. An authored 14999 ms short
        # timeline already exceeds the deadline once frame overhead is counted.
        changed['show_ms'] = 1
        changed['illustrative_travel_ms'] = 7024
        changed['main'] = [changed['main'][0]] + changed['main'][-4:]
        changed['main'][0]['duration_ms'] = 1
        self.phase(changed, 'main', 'rest')['duration_ms'] = 1
        for variant in ('main', 'short'):
            self.phase(changed, variant, 'show')['duration_ms'] = 1
            for stage in ('raise', 'lower'):
                self.phase(changed, variant, stage)['duration_ms'] = 7024
        self.assertEqual(sum(step['duration_ms'] for step in changed['short']), 14999)
        self.assertEqual(viewer_frame_count(changed['short']), 909)
        with self.assertRaisesRegex(ValueError, 'short: illustrative viewer budget .*909 frames'):
            generate(changed)

    def test_viewer_nominal_frame_budget_accepts_899_and_rejects_900_for_both_variants(self):
        baseline = copy.deepcopy(self.score)
        baseline['show_ms'] = 1
        baseline['illustrative_travel_ms'] = 7000
        for variant in ('main', 'short'):
            baseline[variant] = [baseline[variant][0]] + baseline[variant][-4:]
            baseline[variant][0].update(duration_ms=1, audio=None, tone_hz=0)
            self.phase(baseline, variant, 'show')['duration_ms'] = 1
            self.phase(baseline, variant, 'rest')['duration_ms'] = 866
            for stage in ('raise', 'lower'):
                self.phase(baseline, variant, stage)['duration_ms'] = 7000
            # 840 motion + 1 prepare + 1 show + 52 rest + 5 admission/handovers.
            self.assertEqual(viewer_frame_count(baseline[variant]), 899)
            self.assertEqual(presentation_budgets(baseline[variant])[1], Fraction(44950, 3))
        generate(baseline)
        for variant in ('main', 'short'):
            with self.subTest(variant=variant):
                changed = copy.deepcopy(baseline)
                self.phase(changed, variant, 'rest')['duration_ms'] = 867
                self.assertEqual(viewer_frame_count(changed[variant]), 900)
                self.assertEqual(presentation_budgets(changed[variant])[1], 15000)
                with self.assertRaisesRegex(ValueError, f'{variant}: illustrative viewer budget 15000.00 ms'):
                    generate(changed)

    def test_hardware_changes_fail_closed(self):
        for field, value in (('count', 5), ('leds_per_compartment', 5),
                             ('max_brightness', 63), ('max_brightness', 65)):
            with self.subTest(field=field, value=value):
                changed = copy.deepcopy(self.score)
                changed[field] = value
                with self.assertRaises(ValueError):
                    generate(changed)
        for field in ('compartment', 'servo', 'vibration', 'led_first', 'led_count'):
            with self.subTest(field=field):
                changed = copy.deepcopy(self.score)
                changed['channels'][0][field] += 1
                with self.assertRaisesRegex(ValueError, 'channel mapping'):
                    generate(changed)

    def test_scores_cannot_target_multiple_led_segments(self):
        self.assertEqual(self.score['main'][0]['target'], 'selected')
        for variant in ('main', 'short'):
            for index in range(len(self.score[variant])):
                for target in ('all', [1, 2], 63):
                    with self.subTest(variant=variant, index=index, target=target):
                        changed = copy.deepcopy(self.score)
                        changed[variant][index]['target'] = target
                        with self.assertRaises(ValueError):
                            generate(changed)

    def test_physical_limits_and_mapping(self):
        score = json.loads((ROOT / 'effects.json').read_text(encoding='utf-8'))
        self.assertEqual([c['servo'] for c in score['channels']], list(range(6)))
        self.assertEqual([c['vibration'] for c in score['channels']], list(range(6,12)))
        self.assertEqual([c['led_first'] for c in score['channels']], list(range(0,36,6)))
        self.assertLessEqual(score['max_brightness'], 64)
        for variant in ('main', 'short'):
            steps = score[variant]
            self.assertEqual([s['motion'] for s in steps if s['motion']], ['raise','lower'])
            self.assertEqual(sum(s['duration_ms'] for s in steps if s['stage']=='show'), score['show_ms'])
            for i, step in enumerate(steps):
                if step['vibration']:
                    self.assertIsNone(step['motion'])
                    self.assertLessEqual(step['duration_ms'], 150)
                    self.assertFalse(steps[i+1]['vibration'])
                self.assertIn(step['target'], [1,2,3,4,5,6,'selected','next','previous'])

    def test_firmware_generated_from_the_same_score(self):
        if HEADER.exists():
            self.assertEqual(HEADER.read_text(encoding='utf-8'), generate())


if __name__ == '__main__':
    unittest.main()
