import Foundation
import ImageIO
import Vision

// This checks the actual captured pixels; status bars and launch spinners do not
// establish that the shared public-home UI finished rendering.
guard CommandLine.arguments.count == 2 else {
    fputs("Usage: check-ios-home-render /path/to/native.png\n", stderr)
    exit(2)
}
let imageURL = URL(fileURLWithPath: CommandLine.arguments[1])
guard let source = CGImageSourceCreateWithURL(imageURL as CFURL, nil),
      let image = CGImageSourceCreateImageAtIndex(source, 0, nil) else {
    fputs("Cannot decode native screenshot.\n", stderr)
    exit(2)
}

do {
    let request = VNRecognizeTextRequest()
    request.recognitionLevel = .accurate
    request.recognitionLanguages = ["en-US"]
    request.usesLanguageCorrection = false
    try VNImageRequestHandler(cgImage: image, options: [:]).perform([request])
    let text = (request.results ?? [])
        .compactMap { $0.topCandidates(1).first?.string }
        .joined(separator: " ")
    let normalized = text
        .folding(options: [.caseInsensitive, .diacriticInsensitive], locale: Locale(identifier: "en_US_POSIX"))
        .components(separatedBy: CharacterSet.alphanumerics.inverted)
        .filter { !$0.isEmpty }
        .joined(separator: " ")
    // Signed-out HomeView.vue heading. Keep this contract in sync with its copy.
    guard normalized.contains("kufr plny pribehu") else {
        fputs("Native screenshot does not yet show the public-home heading.\n", stderr)
        exit(1)
    }
} catch {
    fputs("Native screenshot text recognition failed: \(error)\n", stderr)
    exit(2)
}
