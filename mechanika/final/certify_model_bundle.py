"""Certify an existing exact ModelBundle from an already successful CAD report."""
import argparse
import json
from pathlib import Path
from model_verification import certify_bundle


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bundle", type=Path, required=True)
    parser.add_argument("--verification", type=Path, required=True)
    parser.add_argument("--source-root", type=Path, default=Path(__file__).parent)
    args = parser.parse_args()
    manifest = certify_bundle(args.bundle, args.verification, args.source_root)
    print(json.dumps({"modelId": manifest["modelId"], "bundleHash": manifest["bundleHash"], "cadVerification": manifest["provenance"]["cadVerification"]}))
