#!/usr/bin/env python3
"""Validate owner supplied recordings and atomically generate a read-only bank.

No provider calls. WAV must be the approved voice, PCM s16le mono 24 kHz.
Missing recordings produce an explicitly unavailable bank; never substitute tones.
The entire bank ships through the existing authenticated firmware OTA path.
"""
import argparse
import hashlib
import json
from pathlib import Path
import tempfile
import wave
import struct
import re

ROOT = Path(__file__).resolve().parents[1]
CAPACITY = 1500000  # Conservatively bounded inside each existing 6.25 MiB OTA slot.
STEPS = (7,8,9,10,11,12,13,14,16,17,19,21,23,25,28,31,34,37,41,45,50,55,60,66,73,80,88,97,107,118,130,143,157,173,190,209,230,253,279,307,337,371,408,449,494,544,598,658,724,796,876,963,1060,1166,1282,1411,1552,1707,1878,2066,2272,2499,2749,3024,3327,3660,4026,4428,4871,5358,5894,6484,7132,7845,8630,9493,10442,11487,12635,13899,15289,16818,18500,20350,22385,24623,27086,29794,32767)
INDEX = (-1,-1,-1,-1,2,4,6,8)


def encode_ima(pcm):
    samples = struct.unpack("<" + "h" * (len(pcm) // 2), pcm)
    assert samples
    predictor, index = samples[0], 0
    encoded = bytearray(struct.pack("<hBB", predictor, index, 0))
    previous = None
    for sample in samples[1:]:
        delta = sample - predictor
        nibble = 8 if delta < 0 else 0
        delta = abs(delta)
        step = STEPS[index]
        difference = step >> 3
        for bit, divisor in ((4, 1), (2, 2), (1, 4)):
            value = step // divisor
            if delta >= value:
                nibble |= bit
                delta -= value
                difference += value
        predictor = max(-32768, min(32767, predictor + (-difference if nibble & 8 else difference)))
        index = max(0, min(88, index + INDEX[nibble & 7]))
        if previous is None:
            previous = nibble
        else:
            encoded.append(previous | (nibble << 4))
            previous = None
    if previous is not None:
        encoded.append(previous)
    return bytes(encoded)


def inspect_recordings(manifest_path, audio_dir):
    """Report proposed inventory fields; this never approves voice/provenance."""
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    inventory = []
    for entry in manifest["clips"]:
        path = audio_dir / (entry["id"] + ".wav")
        if not path.is_file():
            inventory.append({"id": entry["id"], "status": "missing_assets"})
            continue
        with wave.open(str(path), "rb") as wav:
            assert (wav.getnchannels(), wav.getsampwidth(), wav.getframerate(), wav.getcomptype()) == (1, 2, 24000, "NONE")
            pcm = wav.readframes(wav.getnframes())
        assert pcm and len(pcm) <= 384000
        encoded = encode_ima(pcm)
        inventory.append({"id": entry["id"], "durationMs": len(pcm) * 1000 // 48000,
            "sampleCount": len(pcm) // 2, "sizeBytes": len(encoded),
            "sha256": hashlib.sha256(encoded).hexdigest()})
    return inventory


def generate(manifest_path, audio_dir):
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    assert manifest["catalogVersion"] == "ollivander-private-party-v1"
    assert manifest["bankVersion"] == "show-pack-offline-v1"
    assert manifest["repertoireVersion"] == "wand-repertoire-v1"
    repertoire = (ROOT.parent / "backend/Features/Show/ShowRepertoire.cs").read_text(encoding="utf-8")
    repertoire = repertoire[repertoire.index("ShowLine[] Ambient"):repertoire.index("internal static void Validate")]
    shared_ambient = {reference: (family, text) for reference, family, text in
        re.findall(r'new\("([^"]+)", "([^"]+)", "([^"]+)"\)', repertoire)}
    rows, total, missing = [], 0, []
    ids = set()
    for entry in manifest["clips"]:
        clip_id = entry["id"]
        assert clip_id not in ids and clip_id.replace("-", "").isalnum()
        ids.add(clip_id)
        assert entry["language"] == "cs-CZ" and entry["voice"] == "marin"
        assert entry["text"] and len(entry["text"]) <= 180
        assert entry["format"] == "ima_adpcm4_mono" and entry["sampleRate"] == 24000
        assert entry["maxDurationMs"] <= 8000
        assert entry["source"] == "owner-recording" and entry["license"] == "owner-authorized"
        assert 0 <= entry["compartment"] <= 6
        if clip_id.startswith("ambient-"):
            assert shared_ambient.get(entry["repertoireRef"]) == (entry["family"], entry["text"]), "shared ambient repertoire drift"
        pcm, duration, digest, sample_count = b"", 0, "", 0
        if audio_dir is not None and (audio_dir / f"{clip_id}.wav").is_file():
            recording = audio_dir / f"{clip_id}.wav"
            with wave.open(str(recording), "rb") as wav:
                assert (wav.getnchannels(), wav.getsampwidth(), wav.getframerate(), wav.getcomptype()) == (1, 2, 24000, "NONE"), recording
                pcm = wav.readframes(wav.getnframes())
            duration = len(pcm) * 1000 // 48000
            assert pcm and len(pcm) <= 384000 and 0 < duration <= entry["maxDurationMs"], recording
            sample_count = len(pcm) // 2
            pcm = encode_ima(pcm)
            digest = hashlib.sha256(pcm).hexdigest()
            # The manifest is an approved immutable inventory, not a hash updater.
            assert entry["sha256"] == digest and entry["sizeBytes"] == len(pcm) and entry["durationMs"] == duration and entry["sampleCount"] == sample_count, recording
        else:
            missing.append(clip_id)
        total += len(pcm)
        rows.append((entry, pcm, duration, digest, sample_count))
    assert total <= CAPACITY, f"bank {total} exceeds bundled capacity {CAPACITY}"
    assert all(f"slot-{slot}" in ids for slot in range(1, 7))
    assert all(f"ambient-{index}" in ids for index in range(1, 13))
    assert all(f"{family}-{index}" in ids for family in ("intro", "ending", "operation") for index in (1, 2))
    lines = ["// Generated by scripts/prepare_offline_bank.py. Do not edit.", "#pragma once", "#include \"OfflineShow.h\"", "namespace kufr { namespace offline { namespace bank {", f"constexpr size_t TotalBytes = {total};", f"constexpr size_t CapacityBytes = {CAPACITY};", f"constexpr bool Complete = {'false' if missing else 'true'};", 'constexpr char Version[] = "show-pack-offline-v1";', 'constexpr char Voice[] = "marin";']
    for index, (_, pcm, _, _, _) in enumerate(rows):
        if pcm:
            lines.append(f"static const uint8_t Pcm{index}[] = {{")
            lines.extend(",".join(str(value) for value in pcm[offset:offset + 64]) + "," for offset in range(0, len(pcm), 64))
            lines.append("};")
    lines.append("static const Clip Clips[] = {")
    for index, (entry, pcm, duration, digest, sample_count) in enumerate(rows):
        lines.append("  {%s,%s,%s,%d,%d,%s,%d,ClipFormat::ImaAdpcm4,%d}," % (json.dumps(entry["id"]), json.dumps(entry["text"], ensure_ascii=True), f"Pcm{index}" if pcm else "nullptr", len(pcm), duration, json.dumps(digest), entry["compartment"], sample_count))
    lines.extend(["};", "} } }", ""])
    return "\n".join(lines), {"totalBytes": total, "capacityBytes": CAPACITY, "streamWorkingBytes": 960, "missing": missing, "state": "missing_assets" if missing else "ready"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--audio-dir", type=Path)
    parser.add_argument("--manifest", type=Path, default=ROOT / "offline-bank/manifest.json")
    parser.add_argument("--output", type=Path, default=ROOT / "src/features/offline_show/OfflineBank.generated.h")
    parser.add_argument("--check", action="store_true")
    parser.add_argument("--inspect-recordings", action="store_true")
    args = parser.parse_args()
    if args.inspect_recordings:
        if args.audio_dir is None:
            parser.error("--inspect-recordings requires --audio-dir")
        print(json.dumps(inspect_recordings(args.manifest, args.audio_dir), indent=2))
        return
    content, status = generate(args.manifest, args.audio_dir)
    if args.check:
        assert args.output.read_text(encoding="utf-8") == content, "generated bank differs"
    else:
        if status["missing"] and args.output.is_file():
            assert "constexpr bool Complete = true;" not in args.output.read_text(encoding="utf-8"), "incomplete bank cannot replace a verified complete bank"
        args.output.parent.mkdir(parents=True, exist_ok=True)
        with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", newline="\n", dir=args.output.parent, delete=False) as temporary:
            temporary.write(content)
            name = temporary.name
        Path(name).replace(args.output)
    print(json.dumps(status, ensure_ascii=True))


if __name__ == "__main__":
    main()
