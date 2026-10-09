import json
import unittest
from pathlib import Path
from generate_effects import generate, HEADER

ROOT = Path(__file__).resolve().parent


class ChoreographyContract(unittest.TestCase):
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
                self.assertIn(step['target'], [1,2,3,4,5,6,'selected','all','next','previous'])

    def test_firmware_generated_from_the_same_score(self):
        if HEADER.exists():
            self.assertEqual(HEADER.read_text(encoding='utf-8'), generate())


if __name__ == '__main__':
    unittest.main()
