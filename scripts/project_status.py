#!/usr/bin/env python3
"""Deterministic, offline project tracking. Uses only the Python standard library."""

import argparse
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import urllib.parse
import urllib.request


STATES = {"planned", "active", "blocked", "closed", "deferred", "cancelled"}
KINDS = {"source_review", "unit", "integration", "browser", "native_ios", "esp_boot",
         "esp_runtime", "physical_inspection", "electrical_measurement",
         "long_running_soak", "production_deploy"}
PHYSICAL_KINDS = {"esp_boot", "esp_runtime", "physical_inspection",
                  "electrical_measurement", "long_running_soak"}
PHYSICAL_IDENTITIES = {"assemblyId", "firmwareDigest", "wiringRevision", "mechanicsRevision", "configDigest"}
RESULTS = {"pass", "fail", "inconclusive"}
SEVERITIES = {"critical": 0, "high": 1, "medium": 2, "low": 3}
ID_PATTERN = re.compile(r"[A-Za-z0-9][A-Za-z0-9_.-]*\Z")
PLACEHOLDER = re.compile(r"(?:TODO|TBD|PLACEHOLDER|CHANGEME|REPLACE[_ -]?ME|<[^>]+>)\Z", re.I)


class InvalidData(Exception):
    """Invalid authoring data, distinct from a valid unsuccessful gate."""


def require(condition, message):
    if not condition:
        raise InvalidData(message)


def nonempty(value, location):
    require(isinstance(value, str) and bool(value.strip()), f"{location}: expected nonempty string")


def object_value(value, location):
    require(isinstance(value, dict), f"{location}: expected object")
    return value


def array(value, location):
    require(isinstance(value, list), f"{location}: expected array")
    return value


def identifier(value, location):
    require(isinstance(value, str) and ID_PATTERN.fullmatch(value), f"{location}: invalid identifier")


def version(value, location):
    object_value(value, location)
    require(type(value.get("schemaVersion")) is int and value["schemaVersion"] == 1,
            f"{location}: schemaVersion must be 1")


def timestamp(value, location):
    nonempty(value, location)
    try:
        parsed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as error:
        raise InvalidData(f"{location}: invalid UTC timestamp") from error
    require(parsed.tzinfo is not None and parsed.utcoffset() == dt.timedelta(0),
            f"{location}: timestamp must include UTC timezone")
    return parsed


def reject_placeholders(value, location):
    if isinstance(value, dict):
        for key, child in value.items():
            reject_placeholders(child, f"{location}.{key}")
    elif isinstance(value, list):
        for index, child in enumerate(value):
            reject_placeholders(child, f"{location}[{index}]")
    elif isinstance(value, str):
        require(not PLACEHOLDER.fullmatch(value.strip()), f"{location}: placeholder is forbidden")
        require("example.com" not in value.lower() and "example.invalid" not in value.lower(),
                f"{location}: example reference is forbidden")


def no_duplicates_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, f"duplicate JSON property: {key}")
        result[key] = value
    return result


def load_json(path):
    try:
        value = json.loads(path.read_text(encoding="utf-8-sig"), object_pairs_hook=no_duplicates_object,
                           parse_constant=lambda value: (_ for _ in ()).throw(InvalidData(f"invalid number: {value}")))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise InvalidData(f"{path.name}: cannot read JSON: {error}") from error
    version(value, path.name)
    reject_placeholders(value, path.name)
    return value


def safe_path(root, reference):
    """Reject portable traversal forms before resolving symlinks or Windows drives."""
    nonempty(reference, "reference")
    require("\\" not in reference and not reference.startswith("/") and ":" not in reference,
            f"unsafe repository reference: {reference}")
    parts = reference.split("/")
    require(not any(part in {"..", ""} for part in parts), f"unsafe repository reference: {reference}")
    path = (root / reference).resolve()
    require(path.is_relative_to(root.resolve()), f"reference escapes repository: {reference}")
    return path


def check_artifact_reference(root, reference):
    nonempty(reference, "artifactRefs")
    parsed = urllib.parse.urlsplit(reference)
    if parsed.scheme:
        require(parsed.scheme in {"http", "https"} and bool(parsed.netloc) and
                parsed.username is None and parsed.password is None,
                f"unsupported or credential-bearing artifact reference: {reference}")
        return None
    require(not parsed.query and not parsed.fragment, f"artifact reference must name a file: {reference}")
    return safe_path(root, urllib.parse.unquote(reference))


def has_artifact_identity(identities):
    return any(key == "artifactDigest" or key.endswith("ArtifactDigest") for key in identities)


def subject(value, location):
    object_value(value, location)
    require(bool(value), f"{location}: exact identity must not be empty")
    for key, identity in value.items():
        identifier(key, f"{location} key")
        require(isinstance(identity, (str, bool, int)) and not isinstance(identity, float),
                f"{location}.{key}: identity must be string, boolean or integer")
        if isinstance(identity, str):
            nonempty(identity, f"{location}.{key}")
        if key.lower().endswith("digest"):
            require(isinstance(identity, str) and re.fullmatch(r"[0-9a-f]{64}", identity),
                    f"{location}.{key}: expected lowercase SHA-256 digest")
    if "commit" in value:
        require(isinstance(value["commit"], str) and re.fullmatch(r"[0-9a-f]{7,64}", value["commit"]),
                f"{location}.commit: expected Git revision")
        require(type(value.get("dirty")) is bool, f"{location}: commit identity requires boolean dirty")
    if "dirty" in value:
        require(type(value["dirty"]) is bool, f"{location}.dirty: expected boolean")
        if value["dirty"]:
            require(has_artifact_identity(value), f"{location}: dirty bytes require artifactDigest or component ArtifactDigest")
    return value


def flatten_components(components):
    object_value(components, "components")
    result = {}

    def visit(value):
        for key, identity in value.items():
            if isinstance(identity, dict):
                visit(identity)
            else:
                require(key not in result or result[key] == identity,
                        f"components: conflicting identity key {key}")
                result[key] = identity

    visit(components)
    if result:
        subject(result, "components")
    return result


def graph_acyclic(graph, label):
    visited, active = set(), set()

    def visit(node):
        require(node not in active, f"{label}: cycle involving {node}")
        if node in visited:
            return
        active.add(node)
        for child in graph[node]:
            require(child in graph, f"{label}: unknown ID {child}")
            visit(child)
        active.remove(node)
        visited.add(node)

    for node in sorted(graph):
        visit(node)


def criterion_definition(value, location):
    object_value(value, location)
    identifier(value.get("id"), f"{location}.id")
    require(type(value.get("revision")) is int and value["revision"] > 0,
            f"{location}.revision: expected positive integer")
    nonempty(value.get("expected"), f"{location}.expected")
    kinds = array(value.get("requiredEvidenceKinds"), f"{location}.requiredEvidenceKinds")
    require(bool(kinds) and all(isinstance(kind, str) and kind in KINDS for kind in kinds),
            f"{location}: invalid or empty requiredEvidenceKinds")
    require(len(set(kinds)) == len(kinds), f"{location}: duplicate evidence kinds")
    require(value.get("rule", "allOf") in {"allOf", "anyOf"}, f"{location}: invalid evidence rule")
    require(len(kinds) <= 1 or "rule" in value, f"{location}: multiple kinds require explicit rule")
    subject(value.get("appliesTo"), f"{location}.appliesTo")
    refs = array(value.get("evidenceRefs"), f"{location}.evidenceRefs")
    require(all(isinstance(ref, str) for ref in refs) and len(set(refs)) == len(refs),
            f"{location}: invalid or duplicate evidenceRefs")


class Project:
    def __init__(self, root):
        self.root = Path(root).resolve()
        self.directory = self.root / "docs/project"
        self.items = self.read_collection("work-items", "id")
        self.receipts = self.read_collection("evidence", "evidenceId")
        self.targets = self.read_collection("targets", "targetId")
        self.releases = self.read_collection("releases", "releaseId")
        config_path = self.directory / "project.json"
        self.config = load_json(config_path) if config_path.exists() else {"schemaVersion": 1}

    def read_collection(self, name, key):
        result = {}
        for path in sorted((self.directory / name).glob("*.json")):
            value = load_json(path)
            identifier(value.get(key), f"{path.name}.{key}")
            require(value[key] == path.stem, f"{path.name}: {key} must equal filename")
            require(value[key] not in result, f"duplicate {key}: {value[key]}")
            result[value[key]] = value
        return result

    def find_criterion(self, item_id, criterion_id, revision=None):
        require(item_id in self.items, f"unknown work item: {item_id}")
        criteria = list(self.items[item_id]["acceptance"])
        closure = self.items[item_id].get("closure", {})
        criteria += closure.get("acceptedCriteria", [])
        for criterion in criteria:
            if criterion["id"] == criterion_id and (revision is None or criterion["revision"] == revision):
                return criterion
        raise InvalidData(f"{item_id}: unknown criterion/revision {criterion_id}/{revision}")

    def validate(self):
        require(bool(self.items), "no work items configured")
        for item_id, item in self.items.items():
            self.validate_item(item_id, item)
        graph_acyclic({key: [dep["itemId"] for dep in item["dependencies"]]
                       for key, item in self.items.items()}, "dependencies")
        graph_acyclic({key: [item["closure"]["replacementItemId"]]
                       if item.get("closure", {}).get("outcome") == "superseded" else []
                       for key, item in self.items.items()}, "work item replacements")
        for evidence_id, receipt in self.receipts.items():
            self.validate_receipt(evidence_id, receipt)
        self.validate_supersedes()
        for item_id, item in self.items.items():
            for criterion in item["acceptance"]:
                self.validate_evidence_refs(item_id, criterion)
            closure = item.get("closure", {})
            if item["state"] == "closed" and closure["outcome"] == "accepted":
                for criterion in closure["acceptedCriteria"]:
                    self.validate_evidence_refs(item_id, criterion)
                    outcome = self.evaluate_criterion(item_id, criterion, closure["subject"], historical=True,
                                                      require_available=False)
                    require(outcome["state"] == "passed", f"{item_id}: accepted closure lacks valid historical evidence for {criterion['id']} ({outcome['state']})")
                refs = set(closure["evidenceRefs"])
                require(refs == {ref for criterion in closure["acceptedCriteria"] for ref in criterion["evidenceRefs"]},
                        f"{item_id}: closure evidenceRefs must match acceptedCriteria snapshot")
        for target_id, target in self.targets.items():
            self.validate_target(target_id, target)
        self.validate_config()
        self.validate_releases()

    def validate_item(self, item_id, item):
        if "closure" in item:
            object_value(item["closure"], f"{item_id}.closure")
        for key in ("initiative", "title"):
            nonempty(item.get(key), f"{item_id}.{key}")
        for key in ("owner", "decisionRef"):
            if key in item:
                nonempty(item[key], f"{item_id}.{key}")
        if "currentNote" in item:
            require(isinstance(item["currentNote"], str), f"{item_id}.currentNote: expected string")
        require(isinstance(item.get("state"), str) and item["state"] in STATES, f"{item_id}: invalid state")
        require(isinstance(item.get("priority"), str) and item["priority"] in {"P0", "P1", "P2", "P3"},
                f"{item_id}: invalid priority")
        require(type(item.get("sequence")) is int and item["sequence"] >= 0, f"{item_id}: invalid sequence")
        scopes = array(item.get("scope"), f"{item_id}.scope")
        require(bool(scopes), f"{item_id}: scope must not be empty")
        for scope in scopes:
            nonempty(scope, f"{item_id}.scope")
        dependencies = array(item.get("dependencies"), f"{item_id}.dependencies")
        seen = set()
        for dependency in dependencies:
            object_value(dependency, f"{item_id}.dependency")
            identifier(dependency.get("itemId"), f"{item_id}.dependency.itemId")
            require(dependency.get("requiredMilestone") in {"implemented", "accepted"},
                    f"{item_id}: invalid requiredMilestone")
            require(dependency["itemId"] not in seen, f"{item_id}: duplicate dependency")
            seen.add(dependency["itemId"])
        acceptance = array(item.get("acceptance"), f"{item_id}.acceptance")
        require(bool(acceptance), f"{item_id}: acceptance must not be empty")
        criterion_ids = set()
        for criterion in acceptance:
            criterion_definition(criterion, f"{item_id}.acceptance")
            require(criterion["id"] not in criterion_ids, f"{item_id}: duplicate criterion ID")
            criterion_ids.add(criterion["id"])
        blocker_ids = set()
        for blocker in array(item.get("blockers"), f"{item_id}.blockers"):
            object_value(blocker, f"{item_id}.blocker")
            identifier(blocker.get("id"), f"{item_id}.blocker.id")
            require(blocker["id"] not in blocker_ids, f"{item_id}: duplicate blocker ID")
            blocker_ids.add(blocker["id"])
            for key in ("problem", "unblockCondition"):
                nonempty(blocker.get(key), f"{item_id}.blocker.{key}")
            require(isinstance(blocker.get("severity"), str) and blocker["severity"] in SEVERITIES,
                    f"{item_id}: invalid blocker severity")
            blocks = array(blocker.get("blocks"), f"{item_id}.blocker.blocks")
            require(bool(blocks) and all(isinstance(key, str) and key in criterion_ids | {"continuation"} for key in blocks),
                    f"{item_id}: blocker references unknown criterion")
            if "role" in blocker:
                nonempty(blocker["role"], f"{item_id}.blocker.role")
        if item["state"] == "blocked":
            require(bool(item["blockers"]), f"{item_id}: blocked state requires blocker")
        if item["state"] in {"planned", "active", "blocked"}:
            nonempty(item.get("nextAction"), f"{item_id}.nextAction")
        if item["state"] in {"deferred", "cancelled"}:
            nonempty(item.get("decisionRef"), f"{item_id}.decisionRef")
        if "implementedSubject" in item:
            implemented = subject(item["implementedSubject"], f"{item_id}.implementedSubject")
            require(has_artifact_identity(implemented) or "firmwareDigest" in implemented or
                    "commit" in implemented and "dirty" in implemented,
                    f"{item_id}: implementedSubject must identify exact bytes")
        for related in array(item.get("relatedItems", []), f"{item_id}.relatedItems"):
            require(isinstance(related, str) and related in self.items, f"{item_id}: unknown related item")
        if item["state"] == "closed":
            closure = object_value(item.get("closure"), f"{item_id}.closure")
            require(closure.get("outcome") in {"accepted", "superseded"}, f"{item_id}: invalid closure outcome")
            nonempty(closure.get("reason"), f"{item_id}.closure.reason")
            if closure["outcome"] == "superseded":
                replacement = closure.get("replacementItemId")
                require(isinstance(replacement, str) and replacement in self.items and replacement != item_id,
                        f"{item_id}: superseded closure requires replacementItemId")
            else:
                subject(closure.get("subject"), f"{item_id}.closure.subject")
                refs = array(closure.get("evidenceRefs"), f"{item_id}.closure.evidenceRefs")
                require(bool(refs) and all(isinstance(ref, str) and ref in self.receipts for ref in refs),
                        f"{item_id}: invalid closure evidenceRefs")
                snapshot = array(closure.get("acceptedCriteria"), f"{item_id}.closure.acceptedCriteria")
                require(bool(snapshot), f"{item_id}: acceptedCriteria must not be empty")
                snapshot_ids = set()
                for criterion in snapshot:
                    criterion_definition(criterion, f"{item_id}.acceptedCriteria")
                    require(criterion["id"] not in snapshot_ids, f"{item_id}: duplicate historical criterion")
                    snapshot_ids.add(criterion["id"])

    def validate_evidence_refs(self, item_id, criterion):
        for ref in criterion["evidenceRefs"]:
            require(ref in self.receipts, f"{item_id}/{criterion['id']}: unknown evidence {ref}")
            require(any(claim["workItemId"] == item_id and claim["criterionId"] == criterion["id"]
                        for claim in self.receipts[ref]["claims"]),
                    f"{item_id}/{criterion['id']}: evidence {ref} makes no matching claim")

    def validate_receipt(self, evidence_id, receipt):
        timestamp(receipt.get("observedAtUtc"), f"{evidence_id}.observedAtUtc")
        require(isinstance(receipt.get("kind"), str) and receipt["kind"] in KINDS, f"{evidence_id}: invalid kind")
        require(isinstance(receipt.get("result"), str) and receipt["result"] in RESULTS, f"{evidence_id}: invalid result")
        identities = subject(receipt.get("subject"), f"{evidence_id}.subject")
        if receipt["kind"] in PHYSICAL_KINDS:
            require(PHYSICAL_IDENTITIES | {"operator"} <= identities.keys(),
                    f"{evidence_id}: physical observation requires assembly, firmware, wiring, mechanics, configuration and operator identities")
        else:
            require("commit" in identities or has_artifact_identity(identities),
                    f"{evidence_id}: software evidence requires commit or artifactDigest")
        for key in ("method", "environment"):
            nonempty(receipt.get(key), f"{evidence_id}.{key}")
        for key in ("supersedesReason", "resolution"):
            if key in receipt:
                nonempty(receipt[key], f"{evidence_id}.{key}")
        limitations = array(receipt.get("limitations"), f"{evidence_id}.limitations")
        for limitation in limitations:
            nonempty(limitation, f"{evidence_id}.limitations")
        refs = array(receipt.get("artifactRefs", []), f"{evidence_id}.artifactRefs")
        for reference in refs:
            check_artifact_reference(self.root, reference)
        require(bool(refs) or bool(receipt.get("observation")), f"{evidence_id}: observation or artifactRefs required")
        if "observation" in receipt:
            nonempty(receipt["observation"], f"{evidence_id}.observation")
        if receipt["kind"] == "electrical_measurement":
            nonempty(receipt.get("instrument"), f"{evidence_id}.instrument")
            measured = receipt.get("measurements")
            require(isinstance(measured, (list, dict)) and bool(measured), f"{evidence_id}: measurements required")
        claims = array(receipt.get("claims"), f"{evidence_id}.claims")
        require(bool(claims), f"{evidence_id}: claims must not be empty")
        keys = set()
        for claim in claims:
            object_value(claim, f"{evidence_id}.claim")
            identifier(claim.get("workItemId"), f"{evidence_id}.claim.workItemId")
            identifier(claim.get("criterionId"), f"{evidence_id}.claim.criterionId")
            require(type(claim.get("criterionRevision")) is int and claim["criterionRevision"] > 0,
                    f"{evidence_id}: invalid criterionRevision")
            require(isinstance(claim.get("result"), str) and claim["result"] in RESULTS, f"{evidence_id}: invalid claim result")
            self.find_criterion(claim["workItemId"], claim["criterionId"])
            key = (claim["workItemId"], claim["criterionId"], claim["criterionRevision"])
            require(key not in keys, f"{evidence_id}: duplicate claim")
            keys.add(key)
        results = {claim["result"] for claim in claims}
        expected = "fail" if "fail" in results else "inconclusive" if "inconclusive" in results else "pass"
        require(receipt["result"] == expected, f"{evidence_id}: aggregate result contradicts claims")

    def validate_supersedes(self):
        graph = {}
        for evidence_id, receipt in self.receipts.items():
            refs = array(receipt.get("supersedes", []), f"{evidence_id}.supersedes")
            require(all(isinstance(ref, str) for ref in refs) and len(set(refs)) == len(refs),
                    f"{evidence_id}: invalid supersedes")
            graph[evidence_id] = refs
        graph_acyclic(graph, "supersedes")
        for evidence_id, refs in graph.items():
            receipt = self.receipts[evidence_id]
            for ref in refs:
                original = self.receipts[ref]
                claim_keys = lambda record: {(claim["workItemId"], claim["criterionId"], claim["criterionRevision"])
                                             for claim in record["claims"]}
                if receipt["subject"] != original["subject"] or claim_keys(receipt) != claim_keys(original):
                    nonempty(receipt.get("supersedesReason"), f"{evidence_id}: changed supersedes subject/claims requires supersedesReason")
                require(timestamp(receipt["observedAtUtc"], evidence_id) >= timestamp(original["observedAtUtc"], ref),
                        f"{evidence_id}: supersedes cannot predate original observation")
                original_fails = {(claim["workItemId"], claim["criterionId"], claim["criterionRevision"])
                                  for claim in original["claims"] if claim["result"] == "fail"}
                new_passes = {(claim["workItemId"], claim["criterionId"], claim["criterionRevision"])
                              for claim in receipt["claims"] if claim["result"] == "pass"}
                if original_fails & new_passes:
                    nonempty(receipt.get("resolution"), f"{evidence_id}: pass replacing failure requires resolution describing repair/re-test")

    def validate_target(self, target_id, target):
        nonempty(target.get("purpose"), f"{target_id}.purpose")
        flatten_components(target.get("components"))
        criteria = array(target.get("requiredCriteria"), f"{target_id}.requiredCriteria")
        keys = set()
        for reference in criteria:
            object_value(reference, f"{target_id}.requiredCriteria")
            identifier(reference.get("workItemId"), f"{target_id}.workItemId")
            identifier(reference.get("criterionId"), f"{target_id}.criterionId")
            key = (reference["workItemId"], reference["criterionId"])
            require(key not in keys, f"{target_id}: duplicate required criterion")
            keys.add(key)
            require(key[0] in self.items and any(value["id"] == key[1] for value in self.items[key[0]]["acceptance"]),
                    f"{target_id}: unknown current criterion {key[0]}/{key[1]}")
        for exception in array(target.get("exceptions", []), f"{target_id}.exceptions"):
            object_value(exception, f"{target_id}.exception")
            require(exception.get("targetId") == target_id, f"{target_id}: exception must name this target")
            require((exception.get("workItemId"), exception.get("criterionId")) in keys,
                    f"{target_id}: exception must name required criterion")
            for key in ("reason", "decisionRef"):
                nonempty(exception.get(key), f"{target_id}.exception.{key}")
            if "expiresAtUtc" in exception:
                timestamp(exception["expiresAtUtc"], f"{target_id}.exception.expiresAtUtc")

    def validate_config(self):
        focus = self.config.get("focusInitiative")
        if focus is not None:
            require(isinstance(focus, str) and any(item["initiative"] == focus for item in self.items.values()),
                    "project.focusInitiative: unknown initiative")
        target_id = self.config.get("currentTarget")
        if target_id is not None:
            require(isinstance(target_id, str) and target_id in self.targets, "project.currentTarget: unknown target")
        for section, limit in object_value(self.config.get("statusLimits", {}), "statusLimits").items():
            require(section in {"active", "planned", "blockers"} and type(limit) is int and 0 <= limit <= 12,
                    "statusLimits: expected section limits between 0 and 12")

    def validate_releases(self):
        for release_id, release in self.releases.items():
            timestamp(release.get("publishedAtUtc"), f"{release_id}.publishedAtUtc")
            nonempty(release.get("sourceRevision"), f"{release_id}.sourceRevision")
            subject(release.get("subject"), f"{release_id}.subject")
            refs = array(release.get("artifactRefs"), f"{release_id}.artifactRefs")
            require(bool(refs), f"{release_id}: published artifactRefs required")
            for reference in refs:
                check_artifact_reference(self.root, reference)
            if "targetId" in release:
                require(release["targetId"] in self.targets, f"{release_id}: unknown target")

    def available(self, receipt):
        if receipt.get("observation"):
            return True
        refs = receipt.get("artifactRefs", [])
        return bool(refs) and all((path := check_artifact_reference(self.root, ref)) is not None and path.is_file()
                                  for ref in refs)

    def evaluate_criterion(self, item_id, criterion, target_subject=None, historical=False, require_available=True):
        expected = dict(criterion["appliesTo"])
        if target_subject is not None:
            # Only declared relevant keys constrain this criterion. Other component changes do not.
            expected = {key: target_subject.get(key) for key in expected}
        candidates, stale, unavailable = [], False, False
        receipts = self.receipts.values()
        if historical:
            receipts = [self.receipts[ref] for ref in criterion["evidenceRefs"]]
        for receipt in receipts:
            relevant_subject = dict(expected)
            if receipt["kind"] in PHYSICAL_KINDS:
                identities = target_subject if target_subject is not None else criterion["appliesTo"]
                relevant_subject.update({key: identities.get(key) for key in PHYSICAL_IDENTITIES})
            elif not (has_artifact_identity(relevant_subject) or
                      "commit" in relevant_subject and "dirty" in relevant_subject):
                # A serial/assembly name alone cannot identify tested software bytes.
                relevant_subject["artifactDigest"] = None
            claims = [claim for claim in receipt["claims"] if claim["workItemId"] == item_id and claim["criterionId"] == criterion["id"]]
            for claim in claims:
                if claim["criterionRevision"] != criterion["revision"] or not all(
                        key in receipt["subject"] and relevant_subject[key] is not None and
                        type(receipt["subject"][key]) is type(relevant_subject[key]) and receipt["subject"][key] == relevant_subject[key]
                        for key in relevant_subject):
                    if receipt["evidenceId"] in criterion["evidenceRefs"]:
                        stale = True
                    continue
                candidates.append((receipt, claim))
        # Candidates already match all relevant identity axes. Provenance (e.g. operator)
        # may change without identifying different tested bytes or hardware.
        by_id = {receipt["evidenceId"]: (receipt, claim) for receipt, claim in candidates}
        edges = {key: [ref for ref in receipt.get("supersedes", []) if ref in by_id]
                 for key, (receipt, _) in by_id.items()}
        replaced = {ref for refs in edges.values() for ref in refs}
        terminals = set(by_id) - replaced
        superseded = set()
        for terminal in terminals:
            terminal_receipt, terminal_claim = by_id[terminal]
            resolves_failure = (terminal_claim["result"] == "pass" and terminal in criterion["evidenceRefs"] and
                                terminal_receipt["kind"] in criterion["requiredEvidenceKinds"] and
                                (not require_available or self.available(terminal_receipt)))
            pending = [(terminal, False)]
            visited = set()
            while pending:
                current, resolution_seen = pending.pop()
                if (current, resolution_seen) in visited:
                    continue
                visited.add((current, resolution_seen))
                resolution_seen |= bool(by_id[current][0].get("resolution"))
                for ref in edges[current]:
                    old_claim = by_id[ref][1]
                    # An inconclusive correction cannot erase failure. A later inconclusive
                    # correction of a pass also revives failures that the pass had resolved.
                    same_kind = by_id[ref][0]["kind"] == terminal_receipt["kind"]
                    if old_claim["result"] != "fail" or resolves_failure and resolution_seen and same_kind:
                        superseded.add(ref)
                    pending.append((ref, resolution_seen))
        active = [(receipt, claim) for receipt, claim in candidates if receipt["evidenceId"] not in superseded]
        selected = [(receipt, claim) for receipt, claim in active if receipt["evidenceId"] in criterion["evidenceRefs"]]
        failures = [receipt["evidenceId"] for receipt, claim in active if claim["result"] == "fail"]
        passes = [receipt["evidenceId"] for receipt, claim in active if claim["result"] == "pass"]
        if failures:
            return {"state": "conflict" if passes else "failed", "reason": "unresolved failure: " + ", ".join(sorted(failures)), "unavailable": False}
        kinds = set()
        for receipt, claim in selected:
            if claim["result"] != "pass" or receipt["kind"] not in criterion["requiredEvidenceKinds"]:
                continue
            if not require_available or self.available(receipt):
                kinds.add(receipt["kind"])
            else:
                unavailable = True
        required = set(criterion["requiredEvidenceKinds"])
        passed = required <= kinds if criterion.get("rule", "allOf") == "allOf" else bool(required & kinds)
        if passed:
            state = "passed"
        elif kinds & required:
            state = "partial"
        elif stale:
            state = "stale"
        else:
            state = "unverified"
        return {"state": state, "reason": "required evidence: " + ", ".join(sorted(required - kinds)), "unavailable": unavailable}

    def verification(self, item, target_subject=None):
        results = [self.evaluate_criterion(item["id"], criterion, target_subject) for criterion in item["acceptance"]]
        states = {result["state"] for result in results}
        for state in ("conflict", "failed"):
            if state in states:
                return state
        if states == {"passed"}:
            return "passed"
        if "partial" in states or "passed" in states:
            return "partial"
        return "stale" if "stale" in states else "unverified"

    def gate(self, target_id, as_of=None):
        target = self.targets[target_id]
        if not target["requiredCriteria"]:
            return {"state": "not_configured", "details": [], "unavailable": False}
        identities = flatten_components(target["components"])
        details = []
        unavailable = False
        exceptions_used = False
        for reference in target["requiredCriteria"]:
            item_id, criterion_id = reference["workItemId"], reference["criterionId"]
            criterion = next(value for value in self.items[item_id]["acceptance"] if value["id"] == criterion_id)
            result = self.evaluate_criterion(item_id, criterion, identities)
            blockers = [blocker["id"] for blocker in self.items[item_id]["blockers"]
                        if criterion_id in blocker["blocks"] or "continuation" in blocker["blocks"]]
            dependencies = self.dependency_reasons(self.items[item_id], identities)
            if blockers or dependencies:
                reasons = (["blockers: " + ", ".join(sorted(blockers))] if blockers else []) + dependencies
                result = {"state": "blocked", "reason": "; ".join(reasons), "unavailable": False}
            exception = next((value for value in target.get("exceptions", [])
                              if value["workItemId"] == item_id and value["criterionId"] == criterion_id and
                              ("expiresAtUtc" not in value or as_of is not None and
                               timestamp(value["expiresAtUtc"], target_id) > as_of)), None)
            waived = result["state"] != "passed" and exception is not None
            exceptions_used |= waived
            unavailable |= result["unavailable"] and not waived
            details.append({"workItemId": item_id, "criterionId": criterion_id, **result, "exception": waived})
        unsuccessful = [value for value in details if value["state"] != "passed" and not value["exception"]]
        if not unsuccessful:
            state = "ready_with_exceptions" if exceptions_used else "ready"
        elif any(value["state"] in {"failed", "conflict", "blocked"} for value in unsuccessful):
            state = "failed"
        elif any(value["state"] == "stale" for value in unsuccessful):
            state = "stale"
        else:
            state = "unknown"
        return {"state": state, "details": details, "unavailable": unavailable}

    def dependency_reasons(self, item, identities):
        reasons = []
        for dependency in item["dependencies"]:
            other = self.items[dependency["itemId"]]
            milestone = dependency["requiredMilestone"]
            if milestone == "implemented":
                implemented = other.get("implementedSubject", {})
                fulfilled = bool(implemented) and (identities is None or all(
                    key not in identities or (type(identities[key]) is type(value) and identities[key] == value)
                    for key, value in implemented.items()))
            else:
                fulfilled = (other["state"] == "closed" and other.get("closure", {}).get("outcome") == "accepted"
                             and self.verification(other, identities) == "passed" and not other["blockers"])
            if not fulfilled:
                reasons.append(f"dependency {other['id']} requires {milestone} (state {other['state']})")
            reasons.extend(f"dependency {other['id']}: {reason}" for reason in self.dependency_reasons(other, identities))
        return reasons

    def continuation_reasons(self, item):
        reasons = [f"blocker {blocker['id']}: {blocker['problem']}" for blocker in item["blockers"]
                   if "continuation" in blocker["blocks"]]
        if item["state"] == "blocked" and not reasons:
            reasons += [f"blocked: {blocker['problem']}" for blocker in item["blockers"]]
        target_id = self.config.get("currentTarget")
        identities = flatten_components(self.targets[target_id]["components"]) if target_id else None
        reasons += self.dependency_reasons(item, identities)
        return reasons

    def next_item(self, initiative=None, scope=None):
        focus = initiative or self.config.get("focusInitiative")
        candidates = [item for item in self.items.values() if item["state"] in {"active", "planned", "blocked"}
                      and (focus is None or item["initiative"] == focus)
                      and (scope is None or any(value == scope or value.startswith(scope.rstrip("/") + "/")
                                               or scope.startswith(value.rstrip("/") + "/") for value in item["scope"]))]
        candidates.sort(key=lambda item: (item["priority"], item["sequence"], item["id"]))
        blocked = []
        for item in candidates:
            reasons = self.continuation_reasons(item)
            if not reasons:
                return item, f"priority {item['priority']}, sequence {item['sequence']}; dependencies satisfied", blocked
            blocked.append((item["id"], reasons))
        reason = "; ".join(f"{item_id}: {', '.join(reasons)}" for item_id, reasons in blocked)
        return None, reason or f"no eligible work item in initiative {focus or '(all)'} and scope {scope or '(all)'}", blocked

    def all_blockers(self):
        return sorted([(item["id"], blocker) for item in self.items.values() for blocker in item["blockers"]],
                      key=lambda value: (SEVERITIES[value[1]["severity"]], value[0], value[1]["id"]))

    def digest(self):
        paths = [path for folder in ("work-items", "evidence", "targets", "releases", "adr")
                 for path in (self.directory / folder).rglob("*") if path.is_file()]
        paths += [path for path in (self.directory / "ACCEPTANCE.md", self.directory / "project.json",
                                    self.directory / "migration.json", self.root / "scripts/project_status.py") if path.is_file()]
        digest = hashlib.sha256()
        for path in sorted(paths, key=lambda value: value.relative_to(self.root).as_posix()):
            name = path.relative_to(self.root).as_posix().encode("utf-8")
            contents = path.read_bytes()
            # Framed bytes avoid ambiguous concatenations, independent of OS paths/newlines.
            digest.update(len(name).to_bytes(8, "big") + name + len(contents).to_bytes(8, "big") + contents)
        return digest.hexdigest()

    def render(self, as_of=None):
        target_id = self.config.get("currentTarget")
        identities = flatten_components(self.targets[target_id]["components"]) if target_id else None
        item, reason, _ = self.next_item()
        lines = ["<!-- Generated by scripts/project_status.py; edit JSON inputs, then render. -->",
                 "# Project status", "", f"Input SHA-256: `{self.digest()}`", "",
                 f"Focus: {self.config.get('focusInitiative', 'multiple / unspecified')}",
                 f"Current target: {target_id or 'not configured'}", "", "## Next permitted work", ""]
        if item:
            lines += [f"[{item['id']}](work-items/{item['id']}.json) — {compact(item['title'])}",
                      compact(item["nextAction"]), f"Selection: {compact(reason)}"]
        else:
            lines.append("No eligible next step: " + compact(reason))
        lines += ["", "## Gates", ""]
        if not self.targets:
            lines.append("not_configured — no target")
        for key in sorted(self.targets):
            gate = self.gate(key, as_of)
            counts = {}
            for result in gate["details"]:
                counts[result["state"]] = counts.get(result["state"], 0) + 1
            detail = ", ".join(f"{state}: {count}" for state, count in sorted(counts.items()))
            lines.append(f"- [{key}](targets/{key}.json): **{gate['state']}**" + (f" ({detail})" if detail else ""))
        if as_of:
            lines.append(f"Exception evaluation as of: {as_of.isoformat()}")
        limits = {"active": 5, "planned": 5, "blockers": 5, **self.config.get("statusLimits", {})}
        for section, states in (("active", {"active", "blocked"}), ("planned", {"planned"})):
            selected = sorted([value for value in self.items.values() if value["state"] in states],
                              key=lambda value: (value["priority"], value["sequence"], value["id"]))
            lines += ["", "## " + ("Active work" if section == "active" else "Nearest planned work"), ""]
            for value in selected[:limits[section]]:
                lines.append(f"- [{value['id']}](work-items/{value['id']}.json) {value['priority']} / {value['state']} / {self.verification(value, identities)} — {compact(value['title'])}")
            lines.append(f"Total: {len(selected)}; hidden: {max(0, len(selected) - limits[section])}.")
        blockers = self.all_blockers()
        lines += ["", "## Blockers", ""]
        for item_id, blocker in blockers[:limits["blockers"]]:
            lines.append(f"- [{item_id}/{blocker['id']}](work-items/{item_id}.json): {blocker['severity']} — {compact(blocker['problem'])}")
        severity_counts = ", ".join(f"{severity}: {sum(1 for _, blocker in blockers if blocker['severity'] == severity)}"
                                    for severity in SEVERITIES)
        lines += [f"Total: {len(blockers)} ({severity_counts}); hidden: {max(0, len(blockers) - limits['blockers'])}.",
                  "Full list: `python scripts/project_status.py blockers`.", "", "## Sources", "",
                  "[Acceptance rules](ACCEPTANCE.md) · [Work items](work-items/) · [Evidence](evidence/) · [Targets](targets/) · [Releases](releases/)",
                  "Published releases do not establish installation or acceptance."]
        observed = [receipt["observedAtUtc"] for receipt in self.receipts.values()]
        if observed:
            lines.append("Last observation: " + max(observed, key=lambda value: timestamp(value, "observedAtUtc")))
        require(len(lines) <= 140, "render exceeds 140 lines; reduce visible limits or target count")
        return ("\n".join(lines) + "\n").encode("utf-8")


def compact(value):
    text = " ".join(value.split()).replace("|", "\\|")
    return text if len(text) <= 280 else text[:277] + "..."


def atomic_write(path, contents):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(dir=path.parent, prefix=".STATUS-", delete=False) as stream:
            temporary = Path(stream.name)
            stream.write(contents)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        if temporary is not None and temporary.exists():
            temporary.unlink()


def markdown_anchors(text):
    anchors, counts = set(), {}
    for line in text.splitlines():
        match = re.match(r"^\s{0,3}#{1,6}\s+(.+?)\s*#*\s*$", line)
        if match:
            heading = re.sub(r"\[([^]]+)\]\([^)]*\)", r"\1", match[1]).lower()
            slug = "".join(char for char in heading if char.isalnum() or char in " _-").replace(" ", "-")
            count = counts.get(slug, 0)
            counts[slug] = count + 1
            anchors.add(slug + (f"-{count}" if count else ""))
    anchors.update(re.findall(r"\b(?:id|name)\s*=\s*['\"]([^'\"]+)['\"]", text))
    return anchors


def check_local_link(root, source, href, repository_relative=False):
    nonempty(href, "link.href")
    parsed = urllib.parse.urlsplit(href)
    if repository_relative:
        require(not parsed.scheme and not href.startswith("//"), f"destination must be repository-relative: {href}")
    if parsed.scheme:
        require(parsed.scheme in {"https", "http", "mailto"}, f"unsupported link scheme: {href}")
        return
    require(not href.startswith("//") and "\\" not in href, f"unsafe local link: {href}")
    path_part = urllib.parse.unquote(parsed.path)
    if repository_relative:
        path = safe_path(root, path_part) if path_part else safe_path(root, source)
    else:
        source_path = safe_path(root, source)
        require(not path_part.startswith("/") and ":" not in path_part, f"unsafe local link: {href}")
        path = (source_path.parent / path_part).resolve() if path_part else source_path
        require(path.is_relative_to(root.resolve()), f"local link escapes repository: {href}")
    require(path.exists(), f"missing local link: {source} -> {href}")
    fragment = urllib.parse.unquote(parsed.fragment)
    if fragment:
        if path.suffix.lower() == ".json":
            contents = load_json(path)
            anchors = set()

            def collect(value):
                if isinstance(value, dict):
                    if isinstance(value.get("id"), str):
                        anchors.add(value["id"])
                    for child in value.values():
                        collect(child)
                elif isinstance(value, list):
                    for child in value:
                        collect(child)

            collect(contents)
        else:
            anchors = markdown_anchors(path.read_text(encoding="utf-8-sig"))
        require(fragment in anchors, f"missing anchor: {source} -> {href}")


def migration_check(project):
    migration = load_json(project.directory / "migration.json")
    sources = {}
    for source in array(migration.get("sources"), "migration.sources"):
        object_value(source, "migration.source")
        path = source.get("path")
        safe_path(project.root, path)
        require(path not in sources, f"migration: duplicate source {path}")
        digest = source.get("sha256")
        require(isinstance(digest, str) and re.fullmatch(r"[0-9a-f]{64}", digest), "migration: invalid source sha256")
        archive = safe_path(project.root, source.get("archivePath"))
        require(archive.is_file(), f"migration: missing archive {archive.name}")
        contents = archive.read_bytes()
        require(hashlib.sha256(contents).hexdigest() == digest, f"migration: archive hash mismatch for {path}")
        sources[path] = contents.decode("utf-8-sig").splitlines()
    require(bool(sources), "migration: sources must not be empty")
    mappings = array(migration.get("mappings"), "migration.mappings")
    covered = {path: set() for path in sources}
    for mapping in mappings:
        object_value(mapping, "migration.mapping")
        source = mapping.get("source")
        require(isinstance(source, str) and source in sources, "migration: mapping has unknown source")
        nonempty(mapping.get("section"), "migration.mapping.section")
        nonempty(mapping.get("reason"), "migration.mapping.reason")
        start, end = mapping.get("lineStart"), mapping.get("lineEnd")
        require(type(start) is int and type(end) is int and 1 <= start <= end <= len(sources[source]),
                "migration: invalid mapping line range")
        covered[source].update(range(start, end + 1))
        require(mapping.get("classification") in {"current", "historical", "unresolved"}, "migration: invalid classification")
        destinations = array(mapping.get("destinations"), "migration.mapping.destinations")
        require(bool(destinations) or mapping["classification"] == "historical", "migration: current/unresolved mapping needs destination")
        for destination in destinations:
            check_local_link(project.root, source, destination, repository_relative=True)
    for path, lines in sources.items():
        missing = [index for index, line in enumerate(lines, 1) if line.strip() and index not in covered[path]]
        require(not missing, f"migration: unmapped nonempty source lines in {path}: {missing[:10]}")
    for blocker in array(migration.get("openBlockers"), "migration.openBlockers"):
        object_value(blocker, "migration.openBlocker")
        require(blocker.get("source") in sources, "migration: blocker has unknown source")
        nonempty(blocker.get("section"), "migration.openBlocker.section")
        item_id = blocker.get("workItemId")
        require(isinstance(item_id, str) and item_id in project.items, "migration: blocker has unknown work item")
        require(any(value["id"] == blocker.get("blockerId") for value in project.items[item_id]["blockers"]),
                "migration: open blocker lost")
    for link in array(migration.get("links"), "migration.links"):
        object_value(link, "migration.link")
        safe_path(project.root, link.get("source"))
        check_local_link(project.root, link["source"], link.get("href"))
    nonempty(migration.get("rollback"), "migration.rollback")
    return f"Migration verified: {len(sources)} archives, {len(mappings)} mappings, {len(migration['openBlockers'])} open blocker mappings."


def verify_evidence(project, online=False):
    unavailable = []
    for evidence_id, receipt in sorted(project.receipts.items()):
        # Self-contained observations require no external artifact for evaluation.
        if receipt.get("observation") and not online:
            continue
        for ref in receipt.get("artifactRefs", []):
            path = check_artifact_reference(project.root, ref)
            if path is not None:
                if not path.is_file():
                    unavailable.append(f"{evidence_id}: missing artifact {ref}")
            elif not online:
                unavailable.append(f"{evidence_id}: remote artifact requires --online: {ref}")
            else:
                try:
                    with urllib.request.urlopen(urllib.request.Request(ref, method="HEAD"), timeout=15) as response:
                        if response.status >= 400:
                            unavailable.append(f"{evidence_id}: remote artifact HTTP {response.status}")
                except (OSError, ValueError) as error:
                    unavailable.append(f"{evidence_id}: remote artifact unavailable ({type(error).__name__})")
    return unavailable


def validate_baseline(project, revision):
    """Explicit history check; rendering and normal structural validation need no Git."""
    nonempty(revision, "--base")
    require(not revision.startswith("-"), "--base: option-like revision is forbidden")

    def git(*arguments):
        result = subprocess.run(["git", "-C", str(project.root), *arguments], capture_output=True, check=False)
        require(result.returncode == 0, "--base: Git baseline cannot be read: " + result.stderr.decode("utf-8", errors="replace").strip())
        return result.stdout

    commit = git("rev-parse", "--verify", "--end-of-options", revision + "^{commit}").decode("ascii").strip()
    paths = git("ls-tree", "-rz", "--name-only", commit, "--", "docs/project/evidence", "docs/project/work-items").decode("utf-8").split("\0")
    used = {(claim["workItemId"], claim["criterionId"]) for receipt in project.receipts.values() for claim in receipt["claims"]}
    previous_items = {}
    for reference in (path for path in paths if path.endswith(".json")):
        contents = git("show", f"{commit}:{reference}")
        path = safe_path(project.root, reference)
        if reference.startswith("docs/project/evidence/"):
            require(path.is_file() and path.read_bytes() == contents,
                    f"immutable receipt changed or deleted: {reference}; create a new evidenceId and supersedes")
            continue
        previous = json.loads(contents.decode("utf-8-sig"), object_pairs_hook=no_duplicates_object)
        item_id = previous.get("id")
        previous_items[item_id] = previous
        if item_id not in project.items:
            require(not any(key[0] == item_id for key in used), f"used work item removed: {item_id}")
            continue
        current = {value["id"]: value for value in project.items[item_id]["acceptance"]}
        for original in previous.get("acceptance", []):
            criterion_id = original["id"]
            if (item_id, criterion_id) not in used or criterion_id not in current:
                continue
            updated = current[criterion_id]
            semantic_keys = ("expected", "requiredEvidenceKinds", "rule", "appliesTo")
            changed = any(original.get(key, "allOf" if key == "rule" else None) !=
                          updated.get(key, "allOf" if key == "rule" else None) for key in semantic_keys)
            require(not changed or updated["revision"] > original["revision"],
                    f"{item_id}/{criterion_id}: changed used criterion semantics require increased revision")
    for item_id, item in project.items.items():
        previous = previous_items.get(item_id, {})
        old_closure = previous.get("closure", {})
        closure = item.get("closure", {})
        if old_closure.get("outcome") == "accepted":
            require(closure == old_closure, f"{item_id}: historical accepted closure must remain immutable")
        elif item["state"] == "closed" and closure.get("outcome") == "accepted":
            snapshot = {value["id"]: value for value in closure["acceptedCriteria"]}
            current = {value["id"]: value for value in item["acceptance"]}
            require(snapshot == current, f"{item_id}: new accepted closure must snapshot all current criteria")
            require(not item["blockers"], f"{item_id}: new accepted closure cannot retain blockers")


def build_parser():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", default=str(Path(__file__).resolve().parents[1]), help="repository root")
    subparsers = parser.add_subparsers(dest="command", required=True)
    for name in ("validate", "render", "next", "blockers", "verify-evidence", "migration-check"):
        command = subparsers.add_parser(name)
        command.add_argument("--root", default=argparse.SUPPRESS, help="repository root")
        if name in {"validate", "render"}:
            command.add_argument("--as-of", help="explicit UTC timestamp for expiring exceptions")
        if name == "validate":
            command.add_argument("--readiness", action="store_true")
            command.add_argument("--target", help="target ID; default currentTarget or all targets")
            command.add_argument("--base", help="explicit Git baseline for receipt immutability and criterion revisions")
        elif name == "render":
            command.add_argument("--check", action="store_true")
        elif name == "next":
            command.add_argument("--initiative")
            command.add_argument("--scope")
        elif name == "verify-evidence":
            command.add_argument("--online", action="store_true")
    return parser


def main(argv=None):
    args = build_parser().parse_args(argv)
    try:
        project = Project(args.root)
        project.validate()
        as_of = timestamp(args.as_of, "--as-of") if getattr(args, "as_of", None) else None
        if args.command == "validate":
            if args.base:
                validate_baseline(project, args.base)
            print(f"Tracking valid: {len(project.items)} work items, {len(project.receipts)} receipts, {len(project.targets)} targets.")
            if args.readiness:
                target_ids = [args.target] if args.target else [project.config["currentTarget"]] if project.config.get("currentTarget") else sorted(project.targets)
                require(all(key in project.targets for key in target_ids), "unknown readiness target")
                if not target_ids:
                    print("Readiness: not_configured")
                    return 1
                outcomes = [project.gate(key, as_of) for key in target_ids]
                for key, outcome in zip(target_ids, outcomes):
                    print(f"{key}: {outcome['state']}")
                    for detail in outcome["details"]:
                        if detail["state"] != "passed":
                            print(f"  {detail['workItemId']}/{detail['criterionId']}: {detail['state']} ({detail['reason']})" + (" [explicit exception]" if detail["exception"] else ""))
                unsuccessful = [outcome for outcome in outcomes if outcome["state"] not in {"ready", "ready_with_exceptions"}]
                if unsuccessful:
                    return 1 if any(outcome["state"] == "failed" or not outcome["unavailable"] for outcome in unsuccessful) else 3
        elif args.command == "render":
            contents = project.render(as_of)
            path = project.directory / "STATUS.md"
            if args.check:
                if not path.is_file() or path.read_bytes() != contents:
                    print("STATUS.md differs; run render.", file=sys.stderr)
                    return 1
                print("STATUS.md is current.")
            else:
                atomic_write(path, contents)
                print("Rendered docs/project/STATUS.md.")
        elif args.command == "next":
            item, reason, _ = project.next_item(args.initiative, args.scope)
            if item is None:
                print("No eligible next step: " + reason)
                return 1
            print(f"{item['id']}: {item['nextAction']}\nReason: {reason}")
        elif args.command == "blockers":
            for item_id, blocker in project.all_blockers():
                print(f"{item_id}/{blocker['id']} [{blocker['severity']}] blocks {', '.join(blocker['blocks'])}: {blocker['problem']}\n  Unblock: {blocker['unblockCondition']}" + (f"; role: {blocker['role']}" if "role" in blocker else ""))
            print(f"Total: {len(project.all_blockers())} blockers.")
        elif args.command == "verify-evidence":
            unavailable = verify_evidence(project, args.online)
            for message in unavailable:
                print(message)
            if unavailable:
                return 3
            print("Evidence consistent and available for offline evaluation." if not args.online else "Evidence consistent; referenced artifacts checked.")
        else:
            print(migration_check(project))
        return 0
    except (InvalidData, OSError, UnicodeError, RecursionError, TypeError, KeyError, ValueError) as error:
        print(f"Invalid tracking data: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    # Windows console/code-page defaults must not change the JSON-derived UTF-8 output.
    for output in (sys.stdout, sys.stderr):
        if hasattr(output, "reconfigure"):
            output.reconfigure(encoding="utf-8")
    sys.exit(main())
