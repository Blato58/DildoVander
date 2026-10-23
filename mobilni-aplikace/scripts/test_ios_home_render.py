import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


SCRIPTS = Path(__file__).parent
# Synthetic pixels are confined to this test's temporary directory. They never
# enter capture output, metadata, or the app bundle.
FIXTURE_SOURCE = r'''
import AppKit
import ImageIO

let directory = URL(fileURLWithPath: CommandLine.arguments[1])
let fixtures = [
    "black": "",
    "status": "12:53 AM Fri Oct 23",
    "heading": "Kufr pln\u{00fd} p\u{0159}\u{00ed}b\u{011b}h\u{016f}.",
    "uppercase": "KUFR PLNY PRIBEHU."
]
for (name, text) in fixtures {
    let context = CGContext(data: nil, width: 1200, height: 800, bitsPerComponent: 8,
                            bytesPerRow: 0, space: CGColorSpaceCreateDeviceRGB(),
                            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
    context.setFillColor(NSColor.black.cgColor)
    context.fill(CGRect(x: 0, y: 0, width: 1200, height: 800))
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(cgContext: context, flipped: false)
    let attributes: [NSAttributedString.Key: Any] = [
        .font: NSFont.systemFont(ofSize: 64, weight: .bold), .foregroundColor: NSColor.white
    ]
    NSAttributedString(string: text, attributes: attributes).draw(at: NSPoint(x: 60, y: 400))
    NSGraphicsContext.restoreGraphicsState()
    let destination = CGImageDestinationCreateWithURL(
        directory.appendingPathComponent(name + ".png") as CFURL, "public.png" as CFString, 1, nil
    )!
    CGImageDestinationAddImage(destination, context.makeImage()!, nil)
    guard CGImageDestinationFinalize(destination) else { exit(2) }
}
'''


@unittest.skipUnless(sys.platform == "darwin", "Real Vision/ImageIO fixtures require macOS.")
class NativeHomeRenderTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temporary = tempfile.TemporaryDirectory()
        cls.addClassCleanup(cls.temporary.cleanup)
        cls.directory = Path(cls.temporary.name)
        cls.checker = cls.directory / "check-ios-home-render"
        subprocess.run([
            "xcrun", "swiftc", str(SCRIPTS / "check-ios-home-render.swift"),
            "-framework", "Vision", "-framework", "ImageIO", "-o", str(cls.checker),
        ], check=True, timeout=120)
        fixture_source = cls.directory / "fixtures.swift"
        fixture_source.write_text(FIXTURE_SOURCE, encoding="ascii")
        fixture_tool = cls.directory / "fixtures"
        subprocess.run([
            "xcrun", "swiftc", str(fixture_source), "-framework", "AppKit",
            "-framework", "ImageIO", "-o", str(fixture_tool),
        ], check=True, timeout=120)
        subprocess.run([str(fixture_tool), str(cls.directory)], check=True, timeout=20)

    def test_black_or_status_text_does_not_establish_render_readiness(self):
        for name in ("black", "status"):
            with self.subTest(name=name):
                result = subprocess.run([str(self.checker), str(self.directory / f"{name}.png")], timeout=15)
                self.assertEqual(1, result.returncode)

    def test_public_home_heading_is_recognized_with_case_and_diacritics(self):
        for name in ("heading", "uppercase"):
            with self.subTest(name=name):
                result = subprocess.run([str(self.checker), str(self.directory / f"{name}.png")], timeout=15)
                self.assertEqual(0, result.returncode)

    def test_missing_image_fails_closed(self):
        result = subprocess.run([str(self.checker), str(self.directory / "absent.png")], timeout=15)
        self.assertEqual(2, result.returncode)


if __name__ == "__main__":
    unittest.main()
