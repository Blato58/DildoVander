import datetime
import hashlib
import importlib.util
import unittest
from pathlib import Path


SPEC = importlib.util.spec_from_file_location("validate_ios_profile", Path(__file__).with_name("validate-ios-profile.py"))
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
NOW = datetime.datetime(2026, 10, 22, tzinfo=datetime.timezone.utc)
TEAM = "A1B2C3D4E5"
BUNDLE = "de.dildovander.companion"
CERTIFICATE = b"public certificate fixture"
FINGERPRINT = hashlib.sha1(CERTIFICATE).hexdigest().upper()
UUID = "12345678-1234-1234-1234-123456789abc"


def profile_fixture(kind="app_store"):
    profile = {
        "UUID": UUID,
        "TeamIdentifier": [TEAM],
        "CreationDate": (NOW - datetime.timedelta(days=1)).replace(tzinfo=None),
        "ExpirationDate": (NOW + datetime.timedelta(days=1)).replace(tzinfo=None),
        "DeveloperCertificates": [CERTIFICATE],
        "Entitlements": {
            "application-identifier": f"{TEAM}.{BUNDLE}",
            "get-task-allow": kind == "development",
        },
    }
    if kind == "app_store":
        profile["Entitlements"]["beta-reports-active"] = True
    else:
        profile["ProvisionedDevices"] = ["registered-device"]
    return profile


class ValidateProfileTests(unittest.TestCase):
    def test_accepts_store_without_devices_and_preserves_device_modes(self):
        for kind in ("app_store", "ad_hoc", "development"):
            with self.subTest(kind=kind):
                self.assertEqual(UUID, MODULE.validate_profile(profile_fixture(kind), TEAM, BUNDLE, kind, NOW))

    def test_rejects_wrong_identity_type_dates_and_unsafe_uuid(self):
        mutations = [
            {"TeamIdentifier": ["OTHERTEAM0"]}, {"UUID": "../../profile"},
            {"ProvisionedDevices": []}, {"ProvisionedDevices": ["device"]},
            {"ProvisionsAllDevices": True}, {"DeveloperCertificates": []},
            {"CreationDate": NOW + datetime.timedelta(seconds=1)},
            {"ExpirationDate": NOW}, {"ExpirationDate": "not a date"},
        ]
        for mutation in mutations:
            with self.subTest(mutation=mutation):
                profile = profile_fixture()
                profile.update(mutation)
                with self.assertRaises(ValueError):
                    MODULE.validate_profile(profile, TEAM, BUNDLE, "app_store", NOW)
        for key, value in (("application-identifier", f"{TEAM}.*"), ("get-task-allow", True), ("beta-reports-active", False)):
            profile = profile_fixture()
            profile["Entitlements"][key] = value
            with self.subTest(entitlement=key), self.assertRaises(ValueError):
                MODULE.validate_profile(profile, TEAM, BUNDLE, "app_store", NOW)

    def test_device_modes_require_devices_and_correct_debug_entitlement(self):
        for kind in ("development", "ad_hoc"):
            profile = profile_fixture(kind)
            del profile["ProvisionedDevices"]
            with self.subTest(kind=kind), self.assertRaises(ValueError):
                MODULE.validate_profile(profile, TEAM, BUNDLE, kind, NOW)
        with self.assertRaises(ValueError):
            MODULE.validate_profile(profile_fixture("development"), TEAM, BUNDLE, "ad_hoc", NOW)

    def test_store_requires_one_matching_distribution_identity(self):
        profile = profile_fixture()
        distribution = f'1) {FINGERPRINT} "Apple Distribution: Test ({TEAM})"'
        self.assertEqual(FINGERPRINT, MODULE.select_identity(profile, distribution, "app_store"))
        for identities in ("", distribution.replace(FINGERPRINT, "0" * 40), distribution.replace("Apple Distribution", "Apple Development")):
            with self.subTest(identities=identities), self.assertRaises(ValueError):
                MODULE.select_identity(profile, identities, "app_store")
        other = b"other public certificate"
        profile["DeveloperCertificates"].append(other)
        other_identity = f'2) {hashlib.sha1(other).hexdigest().upper()} "Apple Distribution: Test ({TEAM})"'
        with self.assertRaises(ValueError):
            MODULE.select_identity(profile, distribution + "\n" + other_identity, "app_store")


if __name__ == "__main__":
    unittest.main()
