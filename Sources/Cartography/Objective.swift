import CoreGraphics
import CoreML
import Foundation
import Vision

/// Where the objective tracker is: the column of text under the minimap, right-aligned.
///
/// Measured on the 25 Sep 2026 Shadow recording (2646×1663 picture): from just under the
/// minimap (y ≈ 470 in the window) down about 600 px, the right 700 px. Same assumption as
/// `MinimapLayout`: the HUD scales with the picture's height. Unverified elsewhere.
public enum TrackerLayout {
    static let referenceHeight = 1663.0
    static let rightInset = 20.0 / referenceHeight
    static let width = 700.0 / referenceHeight
    static let top = 413.0 / referenceHeight
    static let height = 600.0 / referenceHeight

    public static func rect(windowWidth: Int, windowHeight: Int, titleBar: Int) -> (x: Int, y: Int, width: Int, height: Int) {
        let pictureHeight = Double(windowHeight - titleBar)
        let w = Int(width * pictureHeight), h = Int(height * pictureHeight)
        let x = windowWidth - Int(rightInset * pictureHeight) - w
        let y = titleBar + Int(top * pictureHeight)
        return (max(0, x), max(0, y), min(w, windowWidth), min(h, windowHeight - y))
    }

    /// The whole right-hand HUD column from the top of the picture to the tracker's foot:
    /// the area name ("Path of Blood | 4:26 AM", picture y 0–70 at 1663), the minimap, and
    /// the tracker. Read in one pass and split by height with `areaBand` and `trackerTop`.
    public static func hudRect(windowWidth: Int, windowHeight: Int, titleBar: Int) -> (x: Int, y: Int, width: Int, height: Int) {
        let pictureHeight = Double(windowHeight - titleBar)
        let w = Int(width * pictureHeight), h = Int((top + height) * pictureHeight)
        let x = windowWidth - Int(rightInset * pictureHeight) - w
        return (max(0, x), titleBar, min(w, windowWidth), min(h, windowHeight - titleBar))
    }

    /// Fractions of the HUD column's height.
    public static let areaBand = 70.0 / (413.0 + 600.0)
    public static let trackerTop = 413.0 / (413.0 + 600.0)
}

/// The area the player is in, from the line above the minimap: "Ghastly Depths © | 4:45 AM".
public enum AreaName {
    public static func parse(_ lines: [String]) -> String? {
        for line in lines {
            var text = line.components(separatedBy: "|").first ?? line
            text = text.replacingOccurrences(of: #"\d{1,2}:\d{2}\s*(AM|PM)?"#, with: "", options: .regularExpression)
            // Words in capitals are the interface ("TAB", or a menu's "GAME" and "SHOP"); area
            // names are in title case.
            let words = text.split(separator: " ").filter { w in !(w.count >= 2 && w == w.uppercased() && w.contains { $0.isLetter }) }
            let name = Objective.clean(words.joined(separator: " ").trimmingCharacters(in: .whitespaces))
            let titled = name.count >= 4 && name.first?.isUppercase == true
                && name.allSatisfy { $0.isLetter || $0 == " " || $0 == "'" || $0 == "’" || $0 == "-" }
            if titled { return name }
        }
        return nil
    }
}

/// What the tracker says about the dungeon, read from its lines.
///
/// The tracker lists quests top to bottom, each a title line followed by its objective
/// lines. Seen in Forbidden City: "Normal" (the difficulty), "Forbidden City", "Travel to the
/// Tomb of Thazbach", "Nightmare Dungeon", then the seasonal quest "Conquer a Nightmare
/// Dungeon" with its own lines. The dungeon is the first block, so its title is the first
/// line that is not the difficulty, and its objective is the line after.
public struct Objective: Equatable {
    public let place: String
    public let text: String

    /// The objective with counts removed, so "Slay the Enraged Spirits: 2" and "…: 1" are
    /// the same objective progressing, not a new one.
    /// Whether the place reads like a title — words of letters, as dungeon names are. A
    /// banner's fragment ("TION.") is not.
    public var looksLikeTitle: Bool {
        place.count >= 4 && place.first?.isUppercase == true
            && place.allSatisfy { $0.isLetter || $0 == " " || $0 == "'" || $0 == "’" || $0 == "-" }
    }

    public var kind: String { Self.split(text).kind }
    /// The count at the end of the line ("…: 3", "…: 2/5"), if there is one. The tracker's
    /// green "1" is read as "l" or "I" as often as not.
    public var count: Int? { Self.split(text).count }

    /// The objective's words and its count, apart.
    static func split(_ text: String) -> (kind: String, count: Int?) {
        let edges = CharacterSet(charactersIn: " :•◆·,.-'\"¥+*")
        guard let colon = text.lastIndex(of: ":") else {
            // No colon: a short number on its own at the end is still a count.
            if let last = text.split(separator: " ").last, last.count <= 2, let n = Int(last) {
                return (String(text.dropLast(last.count)).trimmingCharacters(in: edges), n)
            }
            return (text.trimmingCharacters(in: edges), nil)
        }
        let tail = text[text.index(after: colon)...]
        let letters = tail.filter(\.isLetter)
        // A count clause holds digits, or a lone l/I standing for 1, and no other letters.
        guard letters.isEmpty || letters == "l" || letters == "I" || letters == "i" else {
            return (text.trimmingCharacters(in: edges), nil)
        }
        let digits = (tail.split(separator: "/").first ?? "").filter(\.isNumber)
        let count = Int(digits) ?? (letters.isEmpty ? nil : 1)
        return (String(text[..<colon]).trimmingCharacters(in: edges), count)
    }

    /// Whether two readings are the same words, give or take a few letters — two, or a
    /// fifth of the letters, whichever is more. "Seaborn Goddess" was read as "Seabom
    /// Gpddess", "Seabom'Goildess" and "Godde3s" on 26 Sep, each a new objective at a
    /// fixed tolerance of two.
    public static func alike(_ a: String, _ b: String) -> Bool {
        let squash: (String) -> String = { $0.lowercased().filter { $0.isLetter || $0.isNumber } }
        let x = squash(a), y = squash(b)
        return x == y || HUDState.distance(x, y) <= max(2, min(x.count, y.count) / 5)
    }

    static let difficulties: Set<String> = ["normal", "hard", "expert", "penitent", "torment",
                                            "torment i", "torment ii", "torment iii", "torment iv"]

    /// Words an objective line starts with. The objective is found by what it says, and
    /// the place is the line above it: taking "the first two lines" let one stray fragment
    /// above the dungeon's name make "Forbidden City" the objective (233 readings on the
    /// 26 Sep replay with the fast recogniser).
    static let verbs = ["slay", "kill", "defeat", "destroy", "travel", "enter", "go ", "reach", "collect", "gather",
                        "free", "rescue", "find", "activate", "open", "use", "return", "bring", "carry", "place",
                        "deposit", "survive", "protect", "escort", "cleanse", "search", "explore", "investigate",
                        "dungeon cleared", "complete", "light", "ring", "speak", "interact", "pull", "break",
                        "purge", "escape", "loot", "obtain", "retrieve", "recover", "deliver", "assist", "help",
                        "hunt", "banish", "capture", "close", "seal", "summon", "confront", "ascend", "descend",
                        "follow", "locate", "discover", "clear", "kindle", "extinguish", "release", "restore"]

    public static func parse(_ lines: [String]) -> Objective? {
        let useful = lines.map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { clean($0).count > 2 && !difficulties.contains(clean($0).lowercased()) }
        guard useful.count >= 2 else { return nil }
        let stripped: (String) -> String = { $0.lowercased().trimmingCharacters(in: CharacterSet.letters.inverted) }
        // Only a line that starts with a verb is an objective. The old fallback to "the first
        // two lines" turned every corrupted reading ("Goddess: 3 '•*", "' estroy the…") into a
        // new objective without a verb, flipping the guide between enemies and exploring on
        // the 26 Sep Mariner's Refuge run — 107 flip-flops in 41 minutes.
        // …and only the line right under the dungeon's name: the seasonal quest lower down
        // ("Slay Harbingers") was read as the objective whenever the dungeon's own line was
        // unreadable in a frame.
        // The objective is the first verb line in the dungeon's block: right under its name
        // as a rule, but the Undercity puts its tier bar and rewards between ("Reward
        // Upgrades (0/4)", "Attunement: …", "Time Bonus: …", then "Reach District Boss
        // before time expires" — 27 Sep 2026, read as nothing for a whole run).
        guard useful.count >= 2,
              let i = (1..<min(useful.count, 6)).first(where: { n in Self.verbs.contains { stripped(useful[n]).hasPrefix($0) } })
        else { return nil }
        // Its place is the nearest title-looking line above it — a quest lower down has its
        // own title, and a place that changes is believed only after five readings.
        let titleLike: (String) -> Bool = { line in
            // On the raw line: "Reward Upgrades (0/4)" is a count, not a title (it was the
            // Undercity objective's place on 27 Sep).
            let t = line.trimmingCharacters(in: .whitespaces)
            // …and a line that starts with a verb is an objective, not a place: "Purge the
            // Kurast Undercity" over "Descend into the Undercity…" was read as place and
            // objective before the dungeon's own block appeared (27 Sep, 07:46).
            return t.count >= 4 && t.first?.isUppercase == true && t.split(separator: " ").count <= 4
                && t.allSatisfy { $0.isLetter || $0 == " " || $0 == "'" || $0 == "’" || $0 == "-" }
                && !Self.verbs.contains { stripped(t).hasPrefix($0) }
        }
        let placeLine = (1..<i).last(where: { titleLike(useful[$0]) }) ?? 0
        guard titleLike(useful[placeLine]) || placeLine == 0 && !Self.verbs.contains(where: { stripped(useful[0]).hasPrefix($0) }) else { return nil }
        do {
            // Whatever was read before the verb ("1;; Destroy", "4¥\" Destroy") is not words.
            var text = String(useful[i].drop { !$0.isLetter }).trimmingCharacters(in: CharacterSet(charactersIn: " •◆·,.-'¥\""))
            // A tick after "Dungeon cleared" reads as a stray letter ("v", "V"), and each
            // change of case was a new objective. A trailing single letter is never a word.
            if let last = text.split(separator: " ").last, last.count == 1, last.first?.isLetter == true {
                text = String(text.dropLast(1)).trimmingCharacters(in: .whitespaces)
            }
            return Objective(place: clean(useful[placeLine]), text: text)
        }
    }

    /// The icon beside a quest title is read as a stray token or two ("Forbidden City fI",
    /// "Forbidden City 11)"): drop trailing tokens of two characters or fewer, or any that
    /// hold no letter. Titles only — see `parse`.
    public static func clean(_ line: String) -> String {
        var tokens = line.split(separator: " ").map(String.init)
        while let last = tokens.last, tokens.count > 1,
              last.count <= 2 || last.rangeOfCharacter(from: .letters) == nil
                || last.allSatisfy({ "Il1|!".contains($0) }) { tokens.removeLast() }
        return tokens.joined(separator: " ").trimmingCharacters(in: .whitespaces)
    }
}

public enum TrackerReader {
    /// The tracker's lines, top to bottom. Accurate recognition: it runs about once a
    /// second on a small crop, so speed does not matter and the game's serif font needs it.
    public static func lines(in pixelBuffer: CVPixelBuffer) -> [String] {
        recognise(VNImageRequestHandler(cvPixelBuffer: pixelBuffer, options: [:]))
    }

    public static func lines(in image: CGImage) -> [String] {
        recognise(VNImageRequestHandler(cgImage: image, options: [:]))
    }

    /// The first recognition loads Vision's model and took 27 s cold on 25 Sep 2026; after
    /// that, 20–100 ms. Call this in the background before it matters.
    public static func warmUp() {
        guard let context = CGContext(data: nil, width: 64, height: 16, bitsPerComponent: 8, bytesPerRow: 0,
                                      space: CGColorSpaceCreateDeviceGray(),
                                      bitmapInfo: CGImageAlphaInfo.none.rawValue),
              let image = context.makeImage() else { return }
        _ = lines(in: image)
    }

    /// Lines with their vertical position, as a fraction of the image height from the top.
    public static func placedLines(in pixelBuffer: CVPixelBuffer) -> [(text: String, y: Double)] {
        placed(VNImageRequestHandler(cvPixelBuffer: pixelBuffer, options: [:])).map { ($0.text, $0.y) }
    }

    public static func placedLines(in image: CGImage) -> [(text: String, y: Double)] {
        placed(VNImageRequestHandler(cgImage: image, options: [:])).map { ($0.text, $0.y) }
    }

    /// Lines with their position as fractions of the image, x from the left and y from the top.
    public static func placedLinesXY(in pixelBuffer: CVPixelBuffer) -> [(text: String, x: Double, y: Double)] {
        placed(VNImageRequestHandler(cvPixelBuffer: pixelBuffer, options: [:]))
    }

    public static func placedLinesXY(in image: CGImage) -> [(text: String, x: Double, y: Double)] {
        placed(VNImageRequestHandler(cgImage: image, options: [:]))
    }

    /// One recognition at a time, app-wide. Two at once (the objective watcher and the
    /// map-screen watcher) is the pattern that made the accurate recogniser fail with
    /// "e5rt … 13" and fall back to the fast one, which garbles the game's serif face.
    /// A lock, not a serial queue: `DispatchQueue.sync` may run the block on another thread,
    /// and Vision then waited on the caller's thread — MapLab's async main had been resumed
    /// on Vision's own queue — and deadlocked at 170 s of a replay (26 Sep).
    private static let serial = NSLock()
    /// For checking the fast fallback offline: MapLab sets it.
    public nonisolated(unsafe) static var preferFast = false

    /// The last recognition error, for diagnosis.
    public nonisolated(unsafe) static var lastError: Error?

    /// A text request that runs on the CPU. On the Neural Engine, recognition started failing
    /// part-way through a long replay on 26 Sep — `CRImageReaderError.e5rtError(…create_
    /// precompiled_compute_operation…, 13)` on 874 of ~1000 reads, returning no lines and no
    /// thrown error — with the app also using the Neural Engine at the same time. Once a
    /// second on a small crop, the CPU is fast enough and does not fall over.
    public static func request() -> VNRecognizeTextRequest {
        let request = VNRecognizeTextRequest()
        request.recognitionLevel = preferFast ? .fast : .accurate
        request.usesLanguageCorrection = true
        request.recognitionLanguages = ["en-GB", "en-US"]
        let isCPU: (MLComputeDevice) -> Bool = { if case .cpu = $0 { return true }; return false }
        if let stages = try? request.supportedComputeStageDevices {
            for (stage, devices) in stages {
                if let cpu = devices.first(where: isCPU) { try? request.setComputeDevice(cpu, for: stage) }
            }
        }
        return request
    }

    private static func placed(_ handler: VNImageRequestHandler) -> [(text: String, x: Double, y: Double)] {
        serial.lock(); defer { serial.unlock() }
        return placedNow(handler)
    }

    private static func placedNow(_ handler: VNImageRequestHandler) -> [(text: String, x: Double, y: Double)] {
        var request = Self.request()
        do { try handler.perform([request]) } catch {
            // The accurate recogniser's runtime can fail system-wide ("e5rt … 13", seen
            // 26 Sep until the machine is restarted). The fast recogniser uses a different
            // engine; worse on the game's serif font, far better than nothing.
            lastError = error
            request = Self.request()
            request.recognitionLevel = .fast
            do { try handler.perform([request]) } catch { lastError = error; return [] }
        }
        if request.results?.isEmpty ?? true {
            // It has also returned nothing with no error, for a whole run: try the fast one.
            let fast = Self.request()
            fast.recognitionLevel = .fast
            if (try? handler.perform([fast])) != nil, !(fast.results?.isEmpty ?? true) { request = fast }
        }
        return (request.results ?? [])
            .sorted { $0.boundingBox.midY > $1.boundingBox.midY }
            .compactMap { o in o.topCandidates(1).first.map { ($0.string, Double(o.boundingBox.midX), 1 - Double(o.boundingBox.midY)) } }
    }

    private static func recognise(_ handler: VNImageRequestHandler) -> [String] {
        serial.lock(); defer { serial.unlock() }
        return recogniseNow(handler)
    }

    private static func recogniseNow(_ handler: VNImageRequestHandler) -> [String] {
        var request = Self.request()
        do { try handler.perform([request]) } catch {
            request = Self.request()
            request.recognitionLevel = .fast
            do { try handler.perform([request]) } catch { return [] }
        }
        let observations = (request.results ?? [])
        // Vision's boxes are bottom-left origin: sort by descending y for top to bottom.
        return observations
            .sorted { $0.boundingBox.midY > $1.boundingBox.midY }
            .compactMap { $0.topCandidates(1).first?.string }
    }
}

/// What an objective asks of the player, as far as where to go is concerned.
public struct Intent: Equatable {
    /// Something to kill: lead to red marks.
    public let slay: Bool
    /// Somewhere to reach: lead to arches not yet used.
    public let travel: Bool
    /// "… in the <area>" while in that area: stay in it.
    public let stayIn: String?
    /// Travelling to somewhere that is not here: leave this area.
    public let leave: String?
    /// Where a travel objective goes ("Travel to the Siren's Chamber" → "Siren's Chamber"),
    /// so that a place already walked through can be led to directly.
    public let destination: String?
    /// A timed run (the Kurast Undercity): the objective marker is the way, side rooms cost
    /// time, and the well-and-arch heuristics of an ordinary dungeon do not apply.
    public let timed: Bool

    public init(slay: Bool, travel: Bool, stayIn: String?, leave: String?, destination: String? = nil, timed: Bool = false) {
        self.slay = slay; self.travel = travel; self.stayIn = stayIn; self.leave = leave; self.destination = destination; self.timed = timed
    }

    public static let none = Intent(slay: false, travel: false, stayIn: nil, leave: nil)
}

public extension Objective {
    static let slayWords = ["slay", "kill", "defeat", "destroy"]
    static let travelWords = ["travel to", "enter ", "go to", "reach "]

    /// `currentArea` is the name above the minimap. On 26 Sep: "Slay all enemies in the
    /// Ghastly Depths" in the Ghastly Depths → slay, stay; "Travel to the Tomb of Thazbach"
    /// in the Ghastly Depths → travel, leave the Ghastly Depths.
    /// `timed` is what the HUD says about the run (`HUDState.timedRun`): the place alone was
    /// not enough — on 29 Sep 2026 the place read "Monsters" (a wrapped line above the
    /// objective) in two runs running, the run counted as untimed, the objective marker was
    /// never looked for, and the guide explored blindly ("it didn't find where I was
    /// supposed to be going").
    func intent(currentArea: String?, timed: Bool = false) -> Intent {
        let lower = kind.lowercased()
        let slay = Self.slayWords.contains { lower.contains($0) }
        let travel = Self.travelWords.contains { lower.contains($0) }
        var stayIn: String?, leave: String?, destination: String?
        if travel, let range = Self.travelWords.compactMap({ kind.range(of: $0, options: .caseInsensitive) }).first {
            var named = String(kind[range.upperBound...]).trimmingCharacters(in: CharacterSet(charactersIn: " ()."))
            if named.lowercased().hasPrefix("the ") { named.removeFirst(4) }
            // "Reach District Boss before time expires": the place ends at the condition.
            for cut in [" before ", " within ", " while ", " and ", " to "] {
                if let r = named.range(of: cut, options: .caseInsensitive) { named = String(named[..<r.lowerBound]) }
            }
            if named.count >= 3 { destination = named }
        }
        if let here = currentArea?.lowercased() {
            if let range = lower.range(of: " in the ") ?? lower.range(of: " in ") {
                let named = lower[range.upperBound...].trimmingCharacters(in: CharacterSet(charactersIn: " ()."))
                if !named.isEmpty, named.contains(here) || here.contains(named) { stayIn = currentArea }
            }
            // "Reach …" names a thing (the district boss, the exit), not a place to travel to:
            // no area to leave for it. "Travel to" and "Enter" name places.
            if travel, !lower.hasPrefix("reach") {
                let target = destination?.lowercased() ?? lower
                if !target.contains(here) && !here.contains(target) { leave = currentArea }
            }
        }
        return Intent(slay: slay, travel: travel, stayIn: stayIn, leave: leave, destination: destination,
                      timed: timed || place.lowercased().contains("undercity"))
    }
}

/// Believes what the HUD says only when it says it steadily — shared by the live guide and
/// MapLab so both behave the same. Areas: a reading cut short ("Path of Blo", "Ghast") is the
/// known area it begins; a new area must be read three times running. Objectives: two agreeing
/// readings for the same place, five if the place changed, and never while the minimap is
/// covered (the caller passes only readings taken while it is legible).
public final class HUDState {
    public private(set) var area: String?
    public private(set) var objective: Objective?
    /// The objective's count as last confirmed (two readings running), if it has one.
    public private(set) var count: Int?
    /// A countdown shown in the HUD column between the minimap and the tracker — the
    /// Undercity's timer (27 Sep 2026: "150" under "FLOOR 1/3") — as last confirmed.
    public private(set) var timer: Int?
    private var timerCandidate: (value: Int, reads: Int)?
    /// The floor counter under "FLOOR" in the same band ("1/3"): floor and how many.
    public private(set) var floor: (number: Int, of: Int)?
    private var floorCandidate: (value: Int, reads: Int)?
    public private(set) var knownAreas: [String] = []
    /// The tracker has said "Undercity" somewhere (the "Kurast Undercity" header line): a
    /// timed run, whatever the objective's place reads as.
    public private(set) var timedRun = false
    private var areaCandidate: (name: String, count: Int)?
    private var objectiveCandidate: (objective: Objective, count: Int)?
    private var countCandidate: (value: Int, reads: Int)?
    /// How often each spelling of the current objective has been read: the one read most
    /// is the one kept, so "Seabom" gives way to "Seaborn" as the readings come in.
    private var spellings: [String: (reads: Int, text: String)] = [:]

    public init() {}

    public func reset() {
        area = nil; objective = nil; count = nil; timer = nil; floor = nil; timedRun = false
        areaCandidate = nil; objectiveCandidate = nil; countCandidate = nil; timerCandidate = nil; floorCandidate = nil; spellings = [:]
    }

    /// Returns what changed: the area, the objective (a different one, not a better reading
    /// of the same), and the count when it moves after the objective was first read.
    public func update(_ lines: [(text: String, y: Double)]) -> (area: String?, objective: Objective?, count: Int?, timer: Int?, floor: (number: Int, of: Int)?) {
        var newArea: String?
        if !timedRun, lines.contains(where: { $0.text.lowercased().contains("undercity") }) { timedRun = true }
        // The floor counter: "1/3" in the band under the minimap, two readings running.
        var newFloor: (number: Int, of: Int)?
        if let text = lines.lazy.filter({ $0.y >= TrackerLayout.areaBand && $0.y < TrackerLayout.trackerTop })
            .map({ $0.text.trimmingCharacters(in: .whitespaces) })
            .first(where: { $0.count <= 5 && $0.contains("/") && $0.allSatisfy { $0.isNumber || $0 == "/" } }),
           let slash = text.firstIndex(of: "/"), let n = Int(text[..<slash]), let of = Int(text[text.index(after: slash)...]), of >= n, n >= 1 {
            let reads = floorCandidate?.value == n ? (floorCandidate?.reads ?? 0) + 1 : 1
            floorCandidate = (n, reads)
            if reads >= 2, n != floor?.number { floor = (n, of); newFloor = (n, of) }
        }
        // The timer: a bare number of up to three digits in the band under the minimap, two
        // readings running within a few seconds of each other (it counts down).
        // The lowest such number on screen: the floor counter "1/3" sits above the timer and
        // was read as "173" for eight seconds straight while an Afflicted monster's nameplate
        // was up (29 Sep 2026, 07:11), and the first number in the band was taken.
        var newTimer: Int?
        if let digits = lines.lazy.filter({ $0.y >= TrackerLayout.areaBand && $0.y < TrackerLayout.trackerTop })
            .map({ (text: $0.text.trimmingCharacters(in: .whitespaces), y: $0.y) })
            .filter({ (1...3).contains($0.text.count) && $0.text.allSatisfy(\.isNumber) })
            .max(by: { $0.y < $1.y })?.text, let value = Int(digits) {
            let reads = timerCandidate.map { abs($0.value - value) <= 3 } == true ? (timerCandidate?.reads ?? 0) + 1 : 1
            timerCandidate = (value, reads)
            // A jump up needs three readings: "213" was read twice during a floor change
            // (27 Sep, 08:03) and announced as time bought. A jump of more than a minute
            // needs six: "173" was read three times in a row at 26 s left (29 Sep, 07:11,
            // Temple District), announced, and then "60 seconds" was said at 14 s. Nothing
            // in the Undercity buys a minute at once; a floor change resets the reading.
            let jump = timer.map { value - $0 } ?? 0
            let needed = jump > 60 ? 6 : jump > 3 ? 3 : 2
            if reads >= needed, value != timer { timer = value; newTimer = value }
        }
        if let read = AreaName.parse(lines.filter { $0.y < TrackerLayout.areaBand }.map(\.text)) {
            let squash: (String) -> String = { $0.lowercased().filter(\.isLetter) }
            let r = squash(read)
            // The same area if it matches a known name, begins it (read cut short), or is
            // within a few letters of it ("Gh stly Depths"; "Hollow Cavims" for Hollow
            // Caverns is three edits, and became a third area on 26 Sep) — two, or a fifth
            // of the name's letters, whichever is more.
            // …or is a piece of it ("District", "C ve District" for Cave District under the
            // fast recogniser, 27 Sep — three areas for one, and the intent flipped with them).
            let known = knownAreas.first { squash($0) == r }
                ?? (r.count >= 4 ? knownAreas.first { squash($0).hasPrefix(r) } : nil)
                ?? (r.count >= 6 ? knownAreas.first { squash($0).contains(r) } : nil)
                ?? (r.count >= 6 ? knownAreas.first { Self.distance(squash($0), r) <= max(2, r.count / 5) } : nil)
            // A new area is a name of six letters or more, read the same three times.
            if known != nil || r.count >= 6 {
                let name = known ?? read
                let count = areaCandidate?.name == name ? (areaCandidate?.count ?? 0) + 1 : 1
                areaCandidate = (name, count)
                if name != area, count >= (known != nil ? 2 : 3) {
                    area = name
                    if known == nil { knownAreas.append(name) }
                    newArea = name
                }
            }
        }
        let (newObjective, newCount) = objectiveUpdate(lines)
        return (newArea, newObjective, newCount, newTimer, newFloor)
    }

    private func objectiveUpdate(_ lines: [(text: String, y: Double)]) -> (Objective?, Int?) {
        guard let read = Objective.parse(lines.filter { $0.y >= TrackerLayout.trackerTop }.map(\.text)), read.looksLikeTitle,
              // An objective that is the dungeon's own name is the tracker shifted by a line.
              read.text.caseInsensitiveCompare(objective?.place ?? "") != .orderedSame else { return (nil, nil) }
        // "Tomb ofThazbach" / "Tomb of Thazbach", "Seaborn" / "Seabom" / "Gpddess": the same
        // words within a few letters are the same objective and the same place.
        let same: (Objective?, Objective) -> Bool = { a, b in
            guard let a else { return false }
            return Objective.alike(a.kind, b.kind) && Objective.alike(a.place, b.place)
        }
        var newObjective: Objective?
        if let current = objective, same(current, read) {
            objectiveCandidate = nil
            var entry = spellings[read.kind] ?? (0, read.text)
            entry.reads += 1; entry.text = read.text
            if spellings.count < 40 || spellings[read.kind] != nil { spellings[read.kind] = entry }
            if entry.reads > (spellings[current.kind]?.reads ?? 0) {
                objective = Objective(place: current.place, text: read.text)
            }
        } else {
            let count = same(objectiveCandidate?.objective, read) ? (objectiveCandidate?.count ?? 0) + 1 : 1
            objectiveCandidate = (read, count)
            let needed = (objective == nil || Objective.alike(objective?.place ?? "", read.place)) ? 2 : 5
            guard count >= needed else { return (nil, nil) }
            objective = read
            objectiveCandidate = nil
            spellings = [read.kind: (count, read.text)]
            self.count = nil
            countCandidate = nil
            newObjective = read
        }
        // The count, believed after two readings running; the first is part of the
        // objective as announced, and only a move after that is progress.
        var newCount: Int?
        if let value = read.count {
            let reads = countCandidate?.value == value ? (countCandidate?.reads ?? 0) + 1 : 1
            countCandidate = (value, reads)
            if reads >= 2, value != count {
                if count != nil { newCount = value }
                count = value
            }
        }
        return (newObjective, newCount)
    }

    /// Edit distance, for names read with a letter or two wrong.
    static func distance(_ a: String, _ b: String) -> Int {
        let a = Array(a), b = Array(b)
        guard !a.isEmpty else { return b.count }
        var row = Array(0...b.count)
        for i in 1...a.count {
            var previous = row[0]; row[0] = i
            for j in 1...max(b.count, 1) where j <= b.count {
                let keep = row[j]
                row[j] = min(row[j] + 1, row[j - 1] + 1, previous + (a[i - 1] == b[j - 1] ? 0 : 1))
                previous = keep
            }
        }
        return row[b.count]
    }
}

/// The full dungeon map, opened with Start: recognised by its bottom bar of button prompts
/// ("Open Party Finder · Center on Player · Pan · Zoom · Pin Location · Close") and read
/// for the area list in its left panel, under the dungeon's name in capitals.
public enum MapScreen {
    static let promptWords = ["open", "party", "finder", "center", "player", "pan", "zoom", "pin", "location", "close"]

    /// `lines` are the window's text with positions as fractions of it. The bar is matched
    /// word by word with a majority, because the fast recogniser (Vision's fallback) reads
    /// it as "Centeron Playpr • Pini&Kation O Close" — four words of ten is the map; the
    /// skill tree's bar has three at most.
    /// How many of the bar's words a reading holds.
    public static func promptWordCount(_ bar: String) -> Int {
        let letters = bar.lowercased().filter(\.isLetter)
        return promptWords.filter { letters.contains($0) }.count
    }

    /// `force`: the caller has another reason to believe this is the map (the red MAP tab)
    /// and wants the areas read regardless of the bar.
    public static func read(_ lines: [(text: String, x: Double, y: Double)], force: Bool = false) -> (isMap: Bool, areas: [String], bar: String) {
        let bar = lines.filter { $0.y > 0.9 }.map(\.text).joined(separator: " ")
        guard force || promptWordCount(bar) >= 4 else { return (false, [], bar) }
        // The panel: title-case lines in the left third, below the dungeon's name in
        // capitals (which follows "WORLD MAP" and its entry).
        var areas: [String] = []
        var underDungeon = false
        for line in lines.filter({ $0.x < 0.33 && $0.y > 0.3 && $0.y < 0.8 }) {
            let text = line.text.trimmingCharacters(in: .whitespaces)
            let word = text.filter(\.isLetter)
            guard word.count >= 4 else { continue }
            // A header is in capitals — mostly, with the fast recogniser ("NIGFrrMARE"). The
            // dungeon's own header opens the list; the next one (the season's quest box,
            // "CONQUER A NIGHTMARE DUNGEON") closes it.
            if word.filter(\.isUppercase).count * 10 >= word.count * 7 {
                if underDungeon { break }
                underDungeon = !text.uppercased().contains("WORLD MAP")
                continue
            }
            if underDungeon { areas.append(Objective.clean(String(text.drop { !$0.isLetter }))) }
        }
        return (true, areas, bar)
    }

    /// Whether a panel line reads like a place name and not the fast recogniser's rubbish.
    public static func looksLikeName(_ text: String) -> Bool {
        let letters = text.filter(\.isLetter)
        return letters.count >= 4 && text.first?.isUppercase == true
            && letters.filter(\.isUppercase).count * 2 < letters.count
            && text.allSatisfy { $0.isLetter || $0 == " " || $0 == "'" || $0 == "’" || $0 == "-" }
    }
}
