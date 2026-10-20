from pathlib import Path
import tempfile
import unittest
from ui_boundary import ENTRY_POINTS, validate


class UiBoundaryTests(unittest.TestCase):
    def test_existing_ui_entry_points_are_independent(self):
        self.assertTrue(validate())

    def test_conditional_and_transitive_cad_calls_are_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for name in ENTRY_POINTS:
                path = root / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text('safe app build', encoding='utf-8')
            workflow = root / ENTRY_POINTS[0]
            workflow.write_text('if: inputs.optional\nuses: ./.github/actions/wrapper\n', encoding='utf-8')
            action = root / '.github/actions/wrapper/action.yml'
            action.parent.mkdir(parents=True)
            action.write_text('run: python mechanika/final/verify.py', encoding='utf-8')
            with self.assertRaisesRegex(ValueError, 'boundary violated'):
                validate(root)
            action.write_text('uses: ./.github/actions/verified-cad', encoding='utf-8')
            with self.assertRaisesRegex(ValueError, 'boundary violated'):
                validate(root)


if __name__ == '__main__':
    unittest.main()
