import copy
import json
from pathlib import Path
import unittest

from make_config import generate


class ConfigurationTests(unittest.TestCase):
    def setUp(self):
        self.disabled = json.loads(Path(__file__).with_name("service_config.example.json").read_text())

    def measured(self):
        config = copy.deepcopy(self.disabled)
        for key in ("profile_b_ok", "mute_ok", "inhibit_ok", "positions_ok"):
            config[key] = True
        for record in config["servos"]:
            record.update(rest_us=1150, raised_us=1800, travel_ms=450)
        return config

    def test_disabled_template_cannot_authorize_motion(self):
        self.assertIn("ServiceFlags[] = {0,0,0,0}", generate(self.disabled))

    def test_only_complete_measured_records_enable_hardware(self):
        config = self.measured()
        self.assertIn("ServiceFlags[] = {1,1,1,1}", generate(config))
        for field, value in (("rest_us", 499), ("raised_us", 2501), ("travel_ms", 501), ("travel_ms", True)):
            with self.subTest(field=field, value=value):
                bad = copy.deepcopy(config)
                bad["servos"][2][field] = value
                with self.assertRaises(ValueError):
                    generate(bad)
        config["servos"][2]["raised_us"] = config["servos"][2]["rest_us"]
        with self.assertRaises(ValueError):
            generate(config)

    def test_reject_profile_flag_type_and_unexpected_fields(self):
        for key, value in (("profile", "B-with-SD"), ("profile_b_ok", "true"), ("revision", True), ("extra", 1)):
            bad = self.measured()
            bad[key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                generate(bad)


if __name__ == "__main__":
    unittest.main()
