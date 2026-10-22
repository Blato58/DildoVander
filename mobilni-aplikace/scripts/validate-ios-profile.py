"""Validate decoded Apple profiles before importing signing credentials."""

import argparse
import datetime
import hashlib
import plistlib
import re
from pathlib import Path


def validate_profile(profile, team_id, bundle_id, signing_kind, now=None):
    if signing_kind not in ("development", "ad_hoc", "app_store"):
        raise ValueError("Unsupported iOS signing kind.")
    entitlements = profile.get("Entitlements", {})
    if profile.get("TeamIdentifier") != [team_id]:
        raise ValueError("Provisioning profile Team ID does not match.")
    if entitlements.get("application-identifier") != f"{team_id}.{bundle_id}":
        raise ValueError("Provisioning profile Bundle ID does not match.")
    if entitlements.get("get-task-allow") is not (signing_kind == "development"):
        raise ValueError("The provisioning profile type does not match the signing kind.")
    if profile.get("ProvisionsAllDevices"):
        raise ValueError("Enterprise provisioning profiles are not supported.")
    if signing_kind == "app_store":
        if "ProvisionedDevices" in profile or entitlements.get("beta-reports-active") is not True:
            raise ValueError("Expected an App Store Connect distribution profile without devices.")
    elif not profile.get("ProvisionedDevices"):
        raise ValueError("The profile does not contain a registered device.")
    now = now or datetime.datetime.now(datetime.timezone.utc)
    for key in ("CreationDate", "ExpirationDate"):
        value = profile.get(key)
        if not isinstance(value, datetime.datetime):
            raise ValueError("The provisioning profile has no valid lifetime.")
        value = value.replace(tzinfo=datetime.timezone.utc) if value.tzinfo is None else value
        if (key == "CreationDate" and value > now) or (key == "ExpirationDate" and value <= now):
            raise ValueError("The provisioning profile is not currently valid.")
    certificates = profile.get("DeveloperCertificates")
    if not isinstance(certificates, list) or not certificates or any(not isinstance(cert, bytes) or not cert for cert in certificates):
        raise ValueError("The provisioning profile has no valid signing certificates.")
    uuid = profile.get("UUID")
    if not isinstance(uuid, str) or not re.fullmatch(r"[A-Fa-f0-9]{8}(?:-[A-Fa-f0-9]{4}){3}-[A-Fa-f0-9]{12}", uuid):
        raise ValueError("The provisioning profile UUID is invalid.")
    return uuid


def select_identity(profile, identities, signing_kind):
    matches = []
    for certificate in profile.get("DeveloperCertificates", []):
        fingerprint = hashlib.sha1(certificate).hexdigest().upper()
        identity = re.search(rf"^.*\b{fingerprint}\b.*$", identities, re.MULTILINE)
        if identity and (
            signing_kind != "app_store"
            or re.search(r'"(?:Apple Distribution|iPhone Distribution):', identity.group())
        ):
            matches.append(fingerprint)
    if len(matches) != 1:
        raise ValueError("Exactly one imported signing identity of the required type must match the profile.")
    return matches[0]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for argument in ("profile", "team_id", "bundle_id", "signing_kind"):
        parser.add_argument(argument)
    parser.add_argument("--identities", help="security find-identity output from the isolated keychain")
    arguments = parser.parse_args()
    with Path(arguments.profile).open("rb") as source:
        profile = plistlib.load(source)
    try:
        uuid = validate_profile(profile, arguments.team_id, arguments.bundle_id, arguments.signing_kind)
        if arguments.identities:
            print(select_identity(profile, Path(arguments.identities).read_text(encoding="utf-8"), arguments.signing_kind))
        else:
            print(uuid)
    except ValueError as error:
        raise SystemExit(str(error)) from error


if __name__ == "__main__":
    main()
