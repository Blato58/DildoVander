"""Package the owner's canonical WAVs into a NEW SD-ready directory.

Usage: python prepare_dance.py config.json output-directory
JSON: {"tracks": [{"file": "my-song.wav", "bpm": 120, "offsetMs": 0,
                  "red": 255, "green": 0, "blue": 128,
                  "vibrationEveryBeats": 2}]}
An optional "filename" supplies a flat ASCII destination name. Sources are
relative to the JSON file. Audio must already be canonical 44-byte-header
PCM16 mono 24 kHz WAV; this tool does not convert or acquire music.
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import stat
import struct
from dataclasses import dataclass

MAXIMUM_MANIFEST_BYTES = 2048
MAXIMUM_DATA_BYTES = 24_000 * 2 * 600
NAME = re.compile(r"[A-Za-z0-9][A-Za-z0-9_-]{0,59}\.wav", flags=re.ASCII)


@dataclass(frozen=True)
class Track:
    source: Path
    filename: str
    row: str
    size: int
    mtime_ns: int


def integer(value: object, minimum: int, maximum: int, field: str) -> int:
    if type(value) is not int or not minimum <= value <= maximum:
        raise ValueError(f"{field} must be an integer in {minimum}..{maximum}")
    return value


def validate_wav(source: Path, offset_ms: int) -> tuple[int, int]:
    attributes = source.lstat()
    if not stat.S_ISREG(attributes.st_mode):
        raise ValueError(f"Source must be a regular file: {source.name}")
    with source.open("rb") as audio:
        opened = os.fstat(audio.fileno())
        header = audio.read(44)
    if len(header) != 44:
        raise ValueError(f"Incomplete WAV header: {source.name}")
    riff, riff_size, wave, fmt, fmt_size, codec, channels, rate, byte_rate, align, bits, data, size = struct.unpack(
        "<4sI4s4sIHHIIHH4sI", header
    )
    if (riff, wave, fmt, fmt_size, codec, channels, rate, byte_rate, align, bits, data) != (
        b"RIFF", b"WAVE", b"fmt ", 16, 1, 1, 24_000, 48_000, 2, 16, b"data"
    ):
        raise ValueError(f"Requires canonical PCM16 mono 24 kHz WAV: {source.name}")
    if not 48_000 <= size <= MAXIMUM_DATA_BYTES or size % 2 or riff_size != size + 36 or opened.st_size != size + 44:
        raise ValueError(f"WAV size/duration must match exactly and be 1..600 seconds: {source.name}")
    if offset_ms >= size // 48:
        raise ValueError(f"offsetMs must be before the end of {source.name}")
    if opened.st_size != attributes.st_size or opened.st_mtime_ns != attributes.st_mtime_ns:
        raise ValueError(f"Source changed while validating: {source.name}")
    return opened.st_size, opened.st_mtime_ns


def load_tracks(config: Path) -> tuple[list[Track], bytes]:
    if config.stat().st_size > 16_384:
        raise ValueError("JSON configuration is too large")
    content = json.loads(config.read_text(encoding="utf-8"))
    if not isinstance(content, dict) or set(content) != {"tracks"}:
        raise ValueError("JSON must contain only tracks")
    rows = content["tracks"]
    if not isinstance(rows, list) or not 1 <= len(rows) <= 12:
        raise ValueError("Provide 1..12 tracks")
    tracks: list[Track] = []
    used: set[str] = set()
    required = {"file", "bpm", "offsetMs", "red", "green", "blue", "vibrationEveryBeats"}
    ranges = {"bpm": (40, 240), "offsetMs": (0, 10_000), "red": (0, 255), "green": (0, 255),
              "blue": (0, 255), "vibrationEveryBeats": (0, 16)}
    for entry in rows:
        if not isinstance(entry, dict) or not required <= set(entry) or set(entry) - required - {"filename"}:
            raise ValueError("Each track requires file, bpm, offsetMs, RGB and vibrationEveryBeats")
        if not isinstance(entry["file"], str) or not entry["file"]:
            raise ValueError("file must be a source path")
        source = config.parent / entry["file"]
        filename = entry.get("filename", source.name)
        if not isinstance(filename, str) or not NAME.fullmatch(filename):
            raise ValueError("filename requires a leading ASCII letter/digit, up to 60 letters/digits/underscore/hyphen, then .wav")
        if filename.lower() in used:
            raise ValueError("Duplicate destination filename (case insensitive)")
        used.add(filename.lower())
        values = [integer(entry[field], *ranges[field], field) for field in ranges]
        size, mtime = validate_wav(source, values[1])
        row = ",".join([filename, *(str(value) for value in values)])
        tracks.append(Track(source, filename, row, size, mtime))
    manifest = ("KUFR_DANCE_V1\n" + "\n".join(track.row for track in tracks) + "\n").encode("ascii")
    if len(manifest) > MAXIMUM_MANIFEST_BYTES:
        raise ValueError("Manifest exceeds firmware bound")
    return tracks, manifest


def prepare(config: Path, destination: Path) -> None:
    tracks, manifest = load_tracks(config)
    # An exclusive mkdir prevents overwriting any existing arbitrary directory.
    # Validation of ALL input tracks completes before this first write.
    destination.mkdir()
    created: list[Path] = []
    try:
        for track in tracks:
            target = destination / track.filename
            with track.source.open("rb") as audio, target.open("xb") as output:
                created.append(target)
                opened = os.fstat(audio.fileno())
                if not stat.S_ISREG(opened.st_mode) or opened.st_size != track.size or opened.st_mtime_ns != track.mtime_ns:
                    raise ValueError(f"Source changed before copy: {track.source.name}")
                remaining = track.size
                while remaining:
                    block = audio.read(min(65_536, remaining))
                    if not block:
                        raise ValueError(f"Source truncated during copy: {track.source.name}")
                    output.write(block)
                    remaining -= len(block)
                current = os.fstat(audio.fileno())
                if audio.read(1) or current.st_size != track.size or current.st_mtime_ns != track.mtime_ns:
                    raise ValueError(f"Source changed during copy: {track.source.name}")
        manifest_path = destination / "dance.csv"
        with manifest_path.open("xb") as output:
            created.append(manifest_path)
            output.write(manifest)
    except BaseException:
        # Delete only files this invocation created; never recursively remove an
        # arbitrary destination or an unexpected file supplied concurrently.
        for path in reversed(created):
            path.unlink(missing_ok=True)
        destination.rmdir()
        raise


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("config", type=Path)
    parser.add_argument("destination", type=Path)
    args = parser.parse_args()
    try:
        prepare(args.config, args.destination)
    except (OSError, ValueError) as error:
        parser.exit(1, f"Dance preparation failed: {error}\n")
    print(f"Prepared SD files in {args.destination}")


if __name__ == "__main__":
    main()
