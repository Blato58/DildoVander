import json
from pathlib import Path
import struct
import tempfile
import unittest

from prepare_dance import prepare


class PrepareDanceTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.config = self.root / "config.json"
        self.output = self.root / "sd"
        self.header = struct.pack("<4sI4s4sIHHIIHH4sI", b"RIFF", 48036, b"WAVE", b"fmt ", 16,
                                  1, 1, 24000, 48000, 2, 16, b"data", 48000)
        (self.root / "own.wav").write_bytes(self.header + bytes(48000))
        self.track = {"file": "own.wav", "bpm": 120, "offsetMs": 0,
                      "red": 255, "green": 8, "blue": 16, "vibrationEveryBeats": 2}

    def tearDown(self):
        self.temporary.cleanup()

    def save(self, tracks):
        self.config.write_text(json.dumps({"tracks": tracks}), encoding="utf-8")

    def test_exact_manifest_and_unmodified_pcm(self):
        self.save([self.track])
        prepare(self.config, self.output)
        self.assertEqual((self.output / "dance.csv").read_bytes(), b"KUFR_DANCE_V1\nown.wav,120,0,255,8,16,2\n")
        self.assertEqual((self.output / "own.wav").read_bytes(), (self.root / "own.wav").read_bytes())

    def test_existing_directory_is_preserved(self):
        self.save([self.track])
        self.output.mkdir()
        marker = self.output / "do-not-touch"
        marker.write_text("retained")
        with self.assertRaises(FileExistsError):
            prepare(self.config, self.output)
        self.assertEqual(marker.read_text(), "retained")
        self.assertEqual(list(self.output.iterdir()), [marker])

    def test_all_tracks_validated_before_writing(self):
        self.save([self.track, {**self.track, "file": "absent.wav"}])
        with self.assertRaises(FileNotFoundError):
            prepare(self.config, self.output)
        self.assertFalse(self.output.exists())

    def test_config_ranges_duplicates_and_traversal_name(self):
        for entry in [
            {**self.track, "bpm": 39}, {**self.track, "bpm": 241},
            {**self.track, "offsetMs": 1000}, {**self.track, "red": 256},
            {**self.track, "vibrationEveryBeats": 17}, {**self.track, "bpm": True},
            {**self.track, "filename": "../song.wav"}, {**self.track, "extra": 1},
            {**self.track, "filename": "_song.wav"}, {**self.track, "filename": "-song.wav"},
        ]:
            self.save([entry])
            with self.subTest(entry=entry), self.assertRaises(ValueError):
                prepare(self.config, self.output)
            self.assertFalse(self.output.exists())
        self.save([self.track, {**self.track, "filename": "Own.wav"}])
        with self.assertRaises(ValueError):
            prepare(self.config, self.output)

    def test_wav_format_size_and_duration(self):
        self.save([self.track])
        path = self.root / "own.wav"
        for content in [self.header[:43], self.header + bytes(47999), self.header + bytes(48001),
                        self.header[:24] + struct.pack("<I", 44100) + self.header[28:] + bytes(48000)]:
            path.write_bytes(content)
            with self.assertRaises(ValueError):
                prepare(self.config, self.output)
            self.assertFalse(self.output.exists())

    def test_track_count(self):
        for tracks in [[], [self.track] * 13]:
            self.save(tracks)
            with self.assertRaises(ValueError):
                prepare(self.config, self.output)


if __name__ == "__main__":
    unittest.main()
