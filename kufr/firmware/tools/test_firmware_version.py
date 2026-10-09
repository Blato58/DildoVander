import unittest

from firmware_version import resolve_version


class FirmwareVersionTests(unittest.TestCase):
    def test_new_runs_increase_without_source_edits(self):
        self.assertEqual(resolve_version('0.1.1+ci', 41), '0.1.42')
        self.assertEqual(resolve_version('0.1.1+ci', 42), '0.1.43')
        self.assertEqual(resolve_version('0.1.1+ci', 42), '0.1.43')

    def test_component_carry_keeps_canonical_three_part_version(self):
        self.assertEqual(resolve_version('0.1.65535+ci', 1), '0.2.0')
        self.assertEqual(resolve_version('0.65535.65535+ci', 1), '1.0.0')

    def test_invalid_counter_or_overflow_cannot_produce_a_release(self):
        for number in (None, True, False, 0, -1, '42', 1.5, 9007199254740992):
            with self.subTest(number=number), self.assertRaises(ValueError):
                resolve_version('0.1.1+ci', number)
        with self.assertRaises(ValueError):
            resolve_version('65535.65535.65535+ci', 1)

    def test_legacy_policy_retains_its_exact_version(self):
        self.assertEqual(resolve_version('0.1.1'), '0.1.1')

    def test_unknown_or_noncanonical_policy_is_rejected(self):
        for policy in ('0.1.01+ci', '0.1.1+other', '65536.0.0+ci', '0.1.1\n', ''):
            with self.subTest(policy=policy), self.assertRaises(ValueError):
                resolve_version(policy, 42)


if __name__ == '__main__':
    unittest.main()
