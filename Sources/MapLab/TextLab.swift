import AVFoundation
import CoreGraphics
import Foundation
import Cartography

/// `MapLab --text <recording.mov>`: reads the objective tracker every five seconds of a
/// recording and prints the lines and the parsed objective, for checking the reader offline.
enum TextLab {
    static func run(_ url: URL, titleBar: Int) async throws {
        let asset = AVURLAsset(url: url)
        let duration = try await asset.load(.duration).seconds
        let generator = AVAssetImageGenerator(asset: asset)
        generator.requestedTimeToleranceBefore = .zero
        generator.requestedTimeToleranceAfter = .zero
        var t = 0.5
        while t < duration {
            let (image, _) = try await generator.image(at: CMTime(seconds: t, preferredTimescale: 600))
            let r = TrackerLayout.rect(windowWidth: image.width, windowHeight: image.height, titleBar: titleBar)
            if let crop = image.cropping(to: CGRect(x: r.x, y: r.y, width: r.width, height: r.height)) {
                let started = Date()
                let lines = TrackerReader.lines(in: crop)
                let ms = Date().timeIntervalSince(started) * 1000
                let objective = Objective.parse(lines)
                print(String(format: "%6.1f s  %4.0f ms  ", t, ms)
                      + (objective.map { "\($0.place) → \($0.text)  [\($0.kind)]" } ?? "no objective")
                      + "   lines: " + lines.joined(separator: " | "))
            }
            t += 5
        }
    }
}
