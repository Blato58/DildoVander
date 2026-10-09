import copy
import datetime
import hashlib
import importlib.util
import json
import os
import plistlib
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).with_name("configure-ios-signing.py")
SPEC = importlib.util.spec_from_file_location("configure_ios_signing", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
TEAM = "A1B2C3D4E5"
BUNDLE = "cz.kufr.app"
CERTIFICATE = b"test public certificate"
IDENTITY = hashlib.sha1(CERTIFICATE).hexdigest().upper()
PROFILE = "12345678-1234-1234-1234-123456789abc"
METADATA = (TEAM, BUNDLE, IDENTITY, PROFILE, "42")


def project_fixture():
    return {
        "archiveVersion": "1", "objectVersion": "60", "classes": {}, "rootObject": "project",
        "objects": {
            "project": {"isa": "PBXProject", "targets": ["app", "bundle"], "buildConfigurationList": "project-list"},
            "app": {"isa": "PBXNativeTarget", "name": "App", "productType": "com.apple.product-type.application", "buildConfigurationList": "app-list"},
            "bundle": {"isa": "PBXNativeTarget", "name": "SwiftProtobuf_SwiftProtobuf", "productType": "com.apple.product-type.bundle", "buildConfigurationList": "bundle-list"},
            "project-list": {"isa": "XCConfigurationList", "buildConfigurations": ["project-release"]},
            "app-list": {"isa": "XCConfigurationList", "buildConfigurations": ["app-debug", "app-release"]},
            "bundle-list": {"isa": "XCConfigurationList", "buildConfigurations": ["bundle-release"]},
            "project-release": {"isa": "XCBuildConfiguration", "name": "Release", "buildSettings": {"CODE_SIGN_IDENTITY": "iPhone Developer"}},
            "app-debug": {"isa": "XCBuildConfiguration", "name": "Debug", "buildSettings": {"CODE_SIGN_STYLE": "Automatic"}},
            "app-release": {"isa": "XCBuildConfiguration", "name": "Release", "buildSettings": {"CODE_SIGN_STYLE": "Automatic", "SWIFT_VERSION": "5.0"}},
            "bundle-release": {"isa": "XCBuildConfiguration", "name": "Release", "buildSettings": {"CODE_SIGNING_ALLOWED": "NO"}},
        },
    }


class ConfigureIosSigningTests(unittest.TestCase):
    def configure(self, project):
        return MODULE.configure_project(project, MODULE.signing_settings(*METADATA))

    def test_only_app_release_settings_change(self):
        original = project_fixture()
        untouched = copy.deepcopy(original)
        actual = self.configure(original)
        expected = copy.deepcopy(original)
        expected["objects"]["app-release"]["buildSettings"].update(MODULE.signing_settings(*METADATA))
        self.assertEqual(expected, actual)
        self.assertEqual(untouched, original)

    def test_rejects_missing_or_ambiguous_app(self):
        for mutation in ("missing", "ambiguous", "wrong-product", "not-root-target"):
            with self.subTest(mutation=mutation):
                project = project_fixture()
                objects = project["objects"]
                if mutation == "missing":
                    del objects["app"]
                elif mutation == "ambiguous":
                    objects["second-app"] = copy.deepcopy(objects["app"])
                elif mutation == "wrong-product":
                    objects["app"]["productType"] = "com.apple.product-type.bundle"
                else:
                    objects["project"]["targets"] = ["bundle"]
                with self.assertRaises(ValueError):
                    self.configure(project)

    def test_rejects_missing_ambiguous_or_shared_configuration(self):
        for mutation in ("missing-list", "bad-list", "missing-reference", "duplicate-reference", "missing-release", "ambiguous-release", "shared-list", "shared-release", "missing-settings", "conditional-signing"):
            with self.subTest(mutation=mutation):
                project = project_fixture()
                objects = project["objects"]
                if mutation == "missing-list":
                    del objects["app-list"]
                elif mutation == "bad-list":
                    objects["app-list"]["isa"] = "PBXGroup"
                elif mutation == "missing-reference":
                    del objects["app-debug"]
                elif mutation == "duplicate-reference":
                    objects["app-list"]["buildConfigurations"].append("app-release")
                elif mutation == "missing-release":
                    objects["app-release"]["name"] = "Other"
                elif mutation == "ambiguous-release":
                    objects["app-debug"]["name"] = "Release"
                elif mutation == "shared-list":
                    objects["bundle"]["buildConfigurationList"] = "app-list"
                elif mutation == "shared-release":
                    objects["bundle-list"]["buildConfigurations"].append("app-release")
                elif mutation == "missing-settings":
                    del objects["app-release"]["buildSettings"]
                else:
                    objects["app-release"]["buildSettings"]["CODE_SIGN_IDENTITY[sdk=iphoneos*]"] = "Other"
                with self.assertRaises(ValueError):
                    self.configure(project)

    def test_rejects_invalid_metadata_including_private_key(self):
        for index, value in enumerate(("bad-team", "bundle<xml>", "-----BEGIN PRIVATE KEY-----", "profile-name", "0")):
            with self.subTest(index=index):
                metadata = list(METADATA)
                metadata[index] = value
                with self.assertRaises(ValueError):
                    MODULE.signing_settings(*metadata)

    def test_cli_serializes_valid_plist_with_public_metadata_only(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "project.json"
            output = Path(directory) / "project.pbxproj"
            source.write_text(json.dumps(project_fixture()), encoding="utf-8")
            result = subprocess.run([sys.executable, str(SCRIPT), str(source), str(output), *METADATA], capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual(self.configure(project_fixture()), plistlib.loads(output.read_bytes()))
            self.assertNotIn(b"PRIVATE KEY", output.read_bytes())
            self.assertNotIn(CERTIFICATE, output.read_bytes())


class SigningScriptCleanupTests(unittest.TestCase):
    def test_restores_project_and_preserves_target_scope_on_every_exit(self):
        git = shutil.which("git")
        git_bash = Path(git).parent.parent / "bin/bash.exe" if git else None
        bash = str(git_bash) if git_bash and git_bash.exists() else shutil.which("bash")
        if os.name == "nt" and (not git_bash or not git_bash.exists()):
            bash = None  # Windows' System32 bash can be an unconfigured WSL launcher.
        if not bash:
            self.skipTest("Bash is unavailable for the signing script cleanup regression.")
        for exit_point in ("archive", "export", "success", "helper", "restore", "recovery"):
            with self.subTest(exit_point=exit_point), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                (root / "scripts").mkdir()
                shutil.copyfile(SCRIPT, root / "scripts" / SCRIPT.name)
                project = root / "ios/App/App.xcodeproj/project.pbxproj"
                project.parent.mkdir(parents=True)
                original = b"// original OpenStep project, kept byte-for-byte\n"
                project.write_bytes(original)
                (root / "project.json").write_text(json.dumps(project_fixture()), encoding="utf-8")
                profile = {
                    "TeamIdentifier": [TEAM], "UUID": PROFILE,
                    "Entitlements": {"application-identifier": f"{TEAM}.{BUNDLE}", "get-task-allow": True},
                    "ProvisionedDevices": ["test-device"], "DeveloperCertificates": [CERTIFICATE],
                    "ExpirationDate": datetime.datetime.now(datetime.timezone.utc).replace(tzinfo=None) + datetime.timedelta(days=1),
                }
                (root / "profile.plist").write_bytes(plistlib.dumps(profile))
                bin_path = root / "bin"
                bin_path.mkdir()
                stubs = {
                    "cp": '''if [[ "$TEST_EXIT" == restore || "$TEST_EXIT" == recovery ]]; then
  if [[ "$1" == */project-original.pbxproj && "$2" == ios/App/App.xcodeproj/project.pbxproj ]]; then
    exit 1
  fi
fi
if [[ "$TEST_EXIT" == recovery && "$2" == */kufr-ios-project-recovery.*/project-original.pbxproj ]]; then
  exit 1
fi
exec /bin/cp "$@"
''',
                    "base64": "cat\n",
                    "security": 'case "$1" in\n list-keychains) ;;\n create-keychain) touch "${@: -1}" ;;\n delete-keychain) rm -f "$2" ;;\n cms) cat profile.plist ;;\n find-identity) printf "%s\\n" "$TEST_IDENTITY" ;;\nesac\n',
                    "plutil": 'if [[ "$1" == "-convert" ]]; then cp project.json "$4"; fi\n',
                    "python3": 'exec "$TEST_PYTHON" "$@"\n',
                    "xcodebuild": 'case "$1" in\n -help) echo debugging ;;\n archive) printf "%s\\n" "$@" > archive-arguments.txt; cp ios/App/App.xcodeproj/project.pbxproj archived-project.plist; [[ "$TEST_EXIT" != archive ]] ;;\n -exportArchive) [[ "$TEST_EXIT" != export ]] ;;\nesac\n',
                    "unzip": 'mkdir -p "$5/Payload/App.app"; touch "$5/Payload/App.app/embedded.mobileprovision"\n',
                    "codesign": "exit 0\n",
                    "defaults": 'echo "$IOS_BUNDLE_ID"\n',
                    "shasum": 'echo "test checksum"\n',
                }
                for name, body in stubs.items():
                    stub = bin_path / name
                    stub.write_text("#!/usr/bin/env bash\n" + body, encoding="ascii", newline="\n")
                    stub.chmod(0o755)
                (root / "runner-temp").mkdir()
                (root / "dist/ipa").mkdir(parents=True)
                (root / "dist/ipa/test.ipa").write_bytes(b"fixture")
                environment = os.environ.copy()
                environment.update({
                    "PATH": os.pathsep.join((str(bin_path), str(Path(bash).parent), environment["PATH"])),
                    "HOME": str(root / "home"), "RUNNER_TEMP": str(root / "runner-temp"),
                    "IOS_BUNDLE_ID": BUNDLE, "IOS_TEAM_ID": TEAM if exit_point != "helper" else "invalid",
                    "IOS_SIGNING_KIND": "development", "IOS_BUILD_NUMBER": "42",
                    "IOS_BUILD_CERTIFICATE_BASE64": "test", "IOS_PROVISION_PROFILE_BASE64": "test",
                    "IOS_P12_PASSWORD": "test", "IOS_KEYCHAIN_PASSWORD": "test",
                    "TEST_PYTHON": sys.executable.replace("\\", "/"), "TEST_IDENTITY": IDENTITY, "TEST_EXIT": exit_point,
                    "TEST_SIGNING_SCRIPT": str(SCRIPT.with_name("build-ios-ipa.sh")).replace("\\", "/"),
                })
                if exit_point == "helper":
                    # Match the existing profile gate, then fail helper metadata validation.
                    profile["TeamIdentifier"] = ["invalid"]
                    profile["Entitlements"]["application-identifier"] = f"invalid.{BUNDLE}"
                    (root / "profile.plist").write_bytes(plistlib.dumps(profile))
                result = subprocess.run(
                    [bash, "-c", 'export PATH="$PWD/bin:/usr/bin:$PATH"; exec bash "$TEST_SIGNING_SCRIPT"'],
                    cwd=root, env=environment, capture_output=True, text=True,
                )
                self.assertEqual(0 if exit_point == "success" else 1, result.returncode, result.stdout + result.stderr)
                if exit_point in ("restore", "recovery"):
                    self.assertNotEqual(original, project.read_bytes())
                    self.assertIn("Could not restore the Xcode project", result.stderr)
                else:
                    self.assertEqual(original, project.read_bytes())
                if exit_point == "restore":
                    recovery_directories = list((root / "runner-temp").iterdir())
                    self.assertEqual(1, len(recovery_directories))
                    recovery_directory = recovery_directories[0]
                    self.assertTrue(recovery_directory.name.startswith("kufr-ios-project-recovery."))
                    backup = recovery_directory / "project-original.pbxproj"
                    self.assertEqual(original, backup.read_bytes())
                    self.assertEqual([backup], list(recovery_directory.iterdir()))
                    self.assertIn("public backup retained at", result.stderr)
                else:
                    self.assertEqual([], list((root / "runner-temp").iterdir()))
                if exit_point == "recovery":
                    self.assertIn("or preserve its public backup", result.stderr)
                    self.assertNotIn("backup retained at", result.stderr)
                installed_profiles = root / "home/Library/MobileDevice/Provisioning Profiles"
                self.assertEqual([], list(installed_profiles.iterdir()))
                if exit_point == "helper":
                    self.assertFalse((root / "archive-arguments.txt").exists())
                else:
                    self.assertTrue((root / "archived-project.plist").exists(), result.stdout + result.stderr)
                    archived = plistlib.loads((root / "archived-project.plist").read_bytes())
                    self.assertEqual(MODULE.configure_project(project_fixture(), MODULE.signing_settings(*METADATA)), archived)
                    arguments = (root / "archive-arguments.txt").read_text(encoding="utf-8")
                    for setting in MODULE.signing_settings(*METADATA):
                        self.assertNotIn(setting + "=", arguments)


if __name__ == "__main__":
    unittest.main()
