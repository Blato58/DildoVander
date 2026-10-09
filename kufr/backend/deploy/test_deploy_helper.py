import importlib.util
import unittest
from pathlib import Path


specification = importlib.util.spec_from_file_location(
    "kufr_deploy_helper",
    Path(__file__).with_name("deploy-helper.py"),
)
helper = importlib.util.module_from_spec(specification)
specification.loader.exec_module(helper)


class DeploymentConfigurationTests(unittest.TestCase):
    def test_fake_backend_stays_on_loopback_without_secrets(self):
        environment = helper.make_environment({
            "deviceId": "",
            "credentialSha256": "",
            "openAiApiKey": "",
            "caps": {"session": "", "daily": "", "monthly": ""},
        }).decode("ascii")

        self.assertIn("ASPNETCORE_URLS=http://127.0.0.1:5080\n", environment)
        self.assertIn("Backend__DatabasePath=/var/lib/kufr-backend/journal.sqlite\n", environment)
        self.assertNotIn("OPENAI_API_KEY", environment)
        self.assertNotIn("Devices__", environment)
        self.assertNotIn("CapMicroUsd", environment)

    def test_complete_configuration_uses_backend_keys(self):
        environment = helper.make_environment({
            "deviceId": "kufr-1",
            "credentialSha256": "a" * 64,
            "openAiApiKey": "sk-proj_example",
            "caps": {"session": "200000", "daily": "5000000", "monthly": "20000000"},
        }).decode("ascii")

        self.assertIn("Devices__0__DeviceId=kufr-1\n", environment)
        self.assertNotIn("CapMicroUsd", environment)
        self.assertNotIn("DailyCapMicroUsd", environment)
        self.assertNotIn("MonthlyCapMicroUsd", environment)

    def test_legacy_monetary_settings_are_ignored(self):
        for caps in ({"session": "3000001"}, {"session": "-1"}, "invalid-ignored", None):
            with self.subTest(caps=caps):
                environment = helper.make_environment({"caps": caps}).decode("ascii")
                self.assertNotIn("CapMicroUsd", environment)

    def test_partial_or_injected_configuration_is_rejected(self):
        base = {
            "deviceId": "",
            "credentialSha256": "",
            "openAiApiKey": "",
            "caps": {"session": "", "daily": "", "monthly": ""},
        }
        for change in (
            {"deviceId": "kufr-1"},
            {"openAiApiKey": "sk-proj_fake\nASPNETCORE_URLS=http://0.0.0.0:5080"},
        ):
            with self.subTest(change=change), self.assertRaises(ValueError):
                helper.make_environment(base | change)


if __name__ == "__main__":
    unittest.main()
