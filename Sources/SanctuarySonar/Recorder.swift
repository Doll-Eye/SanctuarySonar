import AVFoundation
import ScreenCaptureKit
import Keel

/// Records one window — picture and that app's sound — to a HEVC .mov, and takes stills.
///
/// Everything to do with the stream and the writer happens on `queue`, which is also the
/// queue ScreenCaptureKit delivers samples on, so start, append and finish can never
/// interleave. The owner hears about the result through `onStopped` on the main queue.
///
/// Quality matters more than size here: the recordings are test data for reading a map
/// that has already been through one video encoder on the way from the streaming service,
/// and a second lossy pass would blur exactly the thin lines the reader needs. Hence a high
/// bitrate (0.3 bits per pixel per frame, ~33 Mbit/s at 2560×1440) and a key frame every two
/// seconds so any moment can be seeked to quickly.
final class Recorder: NSObject, SCStreamOutput, SCStreamDelegate {

    struct Counts { var frames = 0; var audioBuffers = 0; var dropped = 0; var elapsed: TimeInterval = 0 }

    /// Called on the main queue once the file is finished — or abandoned, with the reason.
    var onStopped: ((URL?, Error?) -> Void)?

    private let queue = DispatchQueue(label: "sanctuarysonar.capture")
    private var stream: SCStream?
    private var writer: AVAssetWriter?
    private var videoInput: AVAssetWriterInput?
    private var audioInput: AVAssetWriterInput?
    private var firstFrameTime: CMTime?
    private var lastFrameTime: CMTime?
    private var counts = Counts()
    private var finishing = false

    static let framesPerSecond: Int32 = 30

    /// The pixel size a capture of this window comes out at — the window's own backing
    /// size, rounded down to even numbers because HEVC wants them.
    static func pixelSize(of filter: SCContentFilter) -> (Int, Int) {
        let scale = CGFloat(filter.pointPixelScale)
        let width = Int((filter.contentRect.width * scale).rounded()) & ~1
        let height = Int((filter.contentRect.height * scale).rounded()) & ~1
        return (max(width, 2), max(height, 2))
    }

    /// Starts recording. Returns the pixel size being recorded.
    func start(window: SCWindow, to url: URL) async throws -> (Int, Int) {
        let filter = SCContentFilter(desktopIndependentWindow: window)
        let (width, height) = Self.pixelSize(of: filter)

        let config = SCStreamConfiguration()
        config.width = width
        config.height = height
        config.minimumFrameInterval = CMTime(value: 1, timescale: Self.framesPerSecond)
        config.pixelFormat = kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange
        config.queueDepth = 6
        config.showsCursor = false
        config.capturesAudio = true
        config.sampleRate = 48_000
        config.channelCount = 2
        config.excludesCurrentProcessAudio = true

        let writer = try AVAssetWriter(outputURL: url, fileType: .mov)
        let bitrate = Int(Double(width * height) * Double(Self.framesPerSecond) * 0.3)
        let video = AVAssetWriterInput(mediaType: .video, outputSettings: [
            AVVideoCodecKey: AVVideoCodecType.hevc,
            AVVideoWidthKey: width,
            AVVideoHeightKey: height,
            AVVideoCompressionPropertiesKey: [
                AVVideoAverageBitRateKey: bitrate,
                AVVideoExpectedSourceFrameRateKey: Int(Self.framesPerSecond),
                AVVideoMaxKeyFrameIntervalKey: Int(Self.framesPerSecond) * 2
            ]
        ])
        video.expectsMediaDataInRealTime = true
        let audio = AVAssetWriterInput(mediaType: .audio, outputSettings: [
            AVFormatIDKey: kAudioFormatMPEG4AAC,
            AVSampleRateKey: 48_000,
            AVNumberOfChannelsKey: 2,
            AVEncoderBitRateKey: 192_000
        ])
        audio.expectsMediaDataInRealTime = true
        guard writer.canAdd(video), writer.canAdd(audio) else {
            throw RecorderError.writerRefused
        }
        writer.add(video)
        writer.add(audio)
        guard writer.startWriting() else {
            throw writer.error ?? RecorderError.writerRefused
        }

        let stream = SCStream(filter: filter, configuration: config, delegate: self)
        try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: queue)
        try stream.addStreamOutput(self, type: .audio, sampleHandlerQueue: queue)

        queue.sync {
            self.writer = writer
            self.videoInput = video
            self.audioInput = audio
            self.stream = stream
            self.firstFrameTime = nil
            self.lastFrameTime = nil
            self.counts = Counts()
            self.finishing = false
        }
        do {
            try await stream.startCapture()
        } catch {
            queue.sync {
                writer.cancelWriting()
                self.clear()
            }
            try? FileManager.default.removeItem(at: url)
            throw error
        }
        log("Recording \(width)×\(height) at up to \(Self.framesPerSecond) fps, \(bitrate / 1_000_000) Mbit/s → \(url.path)")
        return (width, height)
    }

    /// Stops and finishes the file; `onStopped` reports the result.
    func stop() {
        guard let stream = queue.sync(execute: { self.stream }) else { return }
        Task {
            do { try await stream.stopCapture() } catch { log("stopCapture: \(error.localizedDescription)") }
            self.finish(error: nil)
        }
    }

    var isRecording: Bool { queue.sync { writer != nil && !finishing } }

    func currentCounts() -> Counts { queue.sync { counts } }

    // MARK: Samples

    func stream(_ stream: SCStream, didOutputSampleBuffer buffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard !finishing, let writer, writer.status == .writing, buffer.isValid else { return }
        switch type {
        case .screen:
            // Only complete frames carry a picture; idle ones mean "nothing changed".
            guard let attachments = CMSampleBufferGetSampleAttachmentsArray(buffer, createIfNecessary: false)
                    as? [[SCStreamFrameInfo: Any]],
                  let raw = attachments.first?[.status] as? Int,
                  SCFrameStatus(rawValue: raw) == .complete else { return }
            let time = buffer.presentationTimeStamp
            if firstFrameTime == nil {
                writer.startSession(atSourceTime: time)
                firstFrameTime = time
            }
            guard let videoInput, videoInput.isReadyForMoreMediaData else { counts.dropped += 1; return }
            if videoInput.append(buffer) {
                counts.frames += 1
                lastFrameTime = time
                if let first = firstFrameTime { counts.elapsed = (time - first).seconds }
            } else {
                counts.dropped += 1
            }
        case .audio:
            // Sound before the first picture would sit before the session start.
            guard firstFrameTime != nil, let audioInput, audioInput.isReadyForMoreMediaData else { return }
            if audioInput.append(buffer) { counts.audioBuffers += 1 }
        default:
            break
        }
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        log("Capture stopped by the system: \(error.localizedDescription)")
        finish(error: error)
    }

    // MARK: Finishing

    private func finish(error: Error?) {
        queue.async {
            guard !self.finishing, let writer = self.writer else { return }
            self.finishing = true
            let url = writer.outputURL
            let counts = self.counts
            let report: (URL?, Error?) -> Void = { url, error in
                DispatchQueue.main.async { self.onStopped?(url, error) }
            }
            guard self.firstFrameTime != nil else {
                // Not one picture arrived: an empty file is worse than none.
                writer.cancelWriting()
                try? FileManager.default.removeItem(at: url)
                self.clear()
                report(nil, error ?? RecorderError.noPicture)
                return
            }
            self.videoInput?.markAsFinished()
            self.audioInput?.markAsFinished()
            if let last = self.lastFrameTime { writer.endSession(atSourceTime: last) }
            writer.finishWriting {
                self.queue.async {
                    let failure = writer.status == .completed ? error : (writer.error ?? error)
                    log("Recording finished: \(counts.frames) frames, \(counts.audioBuffers) audio buffers, \(counts.dropped) dropped, \(Int(counts.elapsed)) s, status \(writer.status.rawValue)")
                    self.clear()
                    report(writer.status == .completed ? url : nil, failure)
                }
            }
        }
    }

    private func clear() {
        stream = nil
        writer = nil
        videoInput = nil
        audioInput = nil
        finishing = false
    }

    // MARK: Stills

    /// A still of the window at its full pixel size.
    static func picture(of window: SCWindow) async throws -> CGImage {
        let filter = SCContentFilter(desktopIndependentWindow: window)
        let (width, height) = pixelSize(of: filter)
        let config = SCStreamConfiguration()
        config.width = width
        config.height = height
        config.showsCursor = false
        return try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: config)
    }

    static func writePNG(_ image: CGImage, to url: URL) -> Bool {
        guard let destination = CGImageDestinationCreateWithURL(url as CFURL, "public.png" as CFString, 1, nil) else {
            return false
        }
        CGImageDestinationAddImage(destination, image, nil)
        return CGImageDestinationFinalize(destination)
    }
}

enum RecorderError: LocalizedError {
    case writerRefused
    case noPicture

    var errorDescription: String? {
        switch self {
        case .writerRefused: return "the video file could not be set up"
        case .noPicture: return "no picture came from that window"
        }
    }
}
