"""Apply public signing metadata only to App's unique Release configuration."""

import argparse
import copy
import json
import plistlib
import re
from pathlib import Path


def signing_settings(team_id, bundle_id, identity, profile_uuid, build_number, passkey_domain=None):
    formats = (
        (team_id, r"[A-Z0-9]{10}", "Team ID"),
        (bundle_id, r"[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+", "Bundle ID"),
        (identity, r"[A-F0-9]{40}", "signing identity fingerprint"),
        (profile_uuid, r"[A-Fa-f0-9]{8}(?:-[A-Fa-f0-9]{4}){3}-[A-Fa-f0-9]{12}", "profile UUID"),
        (build_number, r"[1-9][0-9]{0,3}(?:\.[0-9]{1,2}){0,2}", "build version"),
    )
    for value, pattern, label in formats:
        if not isinstance(value, str) or not re.fullmatch(pattern, value):
            raise ValueError(f"Invalid {label}.")
    settings = {
        "CODE_SIGN_STYLE": "Manual",
        "CODE_SIGN_IDENTITY": identity,
        "PROVISIONING_PROFILE_SPECIFIER": profile_uuid,
        "DEVELOPMENT_TEAM": team_id,
        "PRODUCT_BUNDLE_IDENTIFIER": bundle_id,
        "CURRENT_PROJECT_VERSION": build_number,
    }
    if passkey_domain is not None:
        dns = r"(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z](?:[a-z0-9-]{0,61}[a-z0-9])?"
        if not isinstance(passkey_domain, str) or not re.fullmatch(dns, passkey_domain):
            raise ValueError("Invalid canonical passkey DNS domain.")
        settings["KUFR_PASSKEY_RP_DOMAIN"] = passkey_domain
        settings["KUFR_ACCOUNT_ORIGIN"] = "https://" + passkey_domain
    return settings


def configure_project(project, settings):
    project = copy.deepcopy(project)
    objects = project.get("objects") if isinstance(project, dict) else None
    if not isinstance(objects, dict):
        raise ValueError("Xcode project has no objects dictionary.")

    def object_with_type(reference, expected_type):
        value = objects.get(reference) if isinstance(reference, str) else None
        if not isinstance(value, dict) or value.get("isa") != expected_type:
            raise ValueError(f"Expected a valid {expected_type} reference.")
        return value

    root = object_with_type(project.get("rootObject"), "PBXProject")
    app_targets = [
        (reference, value)
        for reference, value in objects.items()
        if isinstance(value, dict)
        and value.get("isa") == "PBXNativeTarget"
        and value.get("name") == "App"
    ]
    if len(app_targets) != 1:
        raise ValueError("Expected exactly one App native target.")
    target_reference, target = app_targets[0]
    if target.get("productType") != "com.apple.product-type.application":
        raise ValueError("App must be an application target.")
    targets = root.get("targets")
    if not isinstance(targets, list) or targets.count(target_reference) != 1:
        raise ValueError("App must belong to the root project exactly once.")

    list_reference = target.get("buildConfigurationList")
    configuration_list = object_with_type(list_reference, "XCConfigurationList")
    owners = [
        value for value in objects.values()
        if isinstance(value, dict) and value.get("buildConfigurationList") == list_reference
    ]
    if len(owners) != 1:
        raise ValueError("App's configuration list must not be shared.")
    configuration_references = configuration_list.get("buildConfigurations")
    if not isinstance(configuration_references, list) or not configuration_references:
        raise ValueError("App has no build configurations.")
    if any(not isinstance(reference, str) for reference in configuration_references):
        raise ValueError("Invalid App configuration reference.")
    if len(set(configuration_references)) != len(configuration_references):
        raise ValueError("App has duplicate configuration references.")
    releases = []
    for reference in configuration_references:
        configuration = object_with_type(reference, "XCBuildConfiguration")
        if configuration.get("name") == "Release":
            releases.append((reference, configuration))
    if len(releases) != 1:
        raise ValueError("Expected exactly one App Release configuration.")
    release_reference, release = releases[0]
    for reference, value in objects.items():
        if reference == list_reference or not isinstance(value, dict):
            continue
        if value.get("isa") == "XCConfigurationList" and release_reference in value.get("buildConfigurations", []):
            raise ValueError("App's Release configuration must not be shared.")
    build_settings = release.get("buildSettings")
    if not isinstance(build_settings, dict):
        raise ValueError("App Release has no build settings dictionary.")
    for key in build_settings:
        if any(key.startswith(f"{setting}[") for setting in settings):
            raise ValueError("App Release has a conditional signing override.")
    build_settings.update(settings)
    return project


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for argument in ("project_json", "output_plist", "team_id", "bundle_id", "identity", "profile_uuid", "build_number"):
        parser.add_argument(argument)
    parser.add_argument("--passkey-domain", help="Canonical DNS domain for the App Associated Domains entitlement")
    arguments = parser.parse_args()
    settings = signing_settings(
        arguments.team_id, arguments.bundle_id, arguments.identity,
        arguments.profile_uuid, arguments.build_number, arguments.passkey_domain,
    )
    project = json.loads(Path(arguments.project_json).read_text(encoding="utf-8"))
    configured = configure_project(project, settings)
    with Path(arguments.output_plist).open("wb") as output:
        plistlib.dump(configured, output, sort_keys=False)


if __name__ == "__main__":
    main()
