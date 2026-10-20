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
        for key in ("profile_b_ok",):
            config[key] = True
        for record in config["servos"]:
            record.update(rest_us=1150, raised_us=1800, travel_ms=450)
        return config

    def test_disabled_template_cannot_authorize_motion(self):
        self.assertIn("ServiceFlags[] = {0,0,0}", generate(self.disabled))

    def test_only_complete_measured_records_enable_hardware(self):
        config = self.measured()
        self.assertIn("ServiceFlags[] = {1,0,0}", generate(config))
        for field, value in (("rest_us", 499), ("raised_us", 2501), ("travel_ms", 3001), ("travel_ms", True)):
            with self.subTest(field=field, value=value):
                bad = copy.deepcopy(config)
                bad["servos"][2][field] = value
                with self.assertRaises(ValueError):
                    generate(bad)
        config["servos"][2]["raised_us"] = config["servos"][2]["rest_us"]
        with self.assertRaises(ValueError):
            generate(config)

    def test_camera_gate_requires_profile_b_and_explicit_boolean(self):
        config = self.measured()
        config["camera_verified"] = True
        self.assertIn("ServiceFlags[] = {1,1,0}", generate(config))
        config["profile_b_ok"] = False
        with self.assertRaises(ValueError): generate(config)
        config["profile_b_ok"] = True
        config["camera_verified"] = "true"
        with self.assertRaises(ValueError): generate(config)

    def test_reject_profile_flag_type_and_unexpected_fields(self):
        for key, value in (("profile", "B-with-SD"), ("profile_b_ok", "true"), ("revision", True), ("extra", 1)):
            bad = self.measured()
            bad[key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                generate(bad)

    def test_sd_gate_is_explicit_boolean_and_requires_profile_b(self):
        config = self.measured()
        config["sd_verified"] = True
        self.assertIn("ServiceFlags[] = {1,0,1}", generate(config))
        config["profile_b_ok"] = False
        with self.assertRaises(ValueError): generate(config)
        config["profile_b_ok"] = True
        config["sd_verified"] = 1
        with self.assertRaises(ValueError): generate(config)

    def test_legacy_configuration_defaults_sd_to_disabled(self):
        config = self.measured()
        config.pop("sd_verified")
        self.assertIn("ServiceFlags[] = {1,0,0}", generate(config))


if __name__ == "__main__":
    unittest.main()
