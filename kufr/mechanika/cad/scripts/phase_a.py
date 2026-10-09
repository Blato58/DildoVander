"""Generate and gate the reference case before designing/installing components."""
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from common import ROOT, load_parameters
from reference.case import validate_and_export

if __name__ == "__main__":
    report = validate_and_export(load_parameters(), ROOT)
    print(f"Phase A {report['status']}: {report['sampled_lid_angles']} lid positions, body/lid/mounting-space STEP exported.")
