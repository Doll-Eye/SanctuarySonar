import AppKit
import ScreenCaptureKit
import Cartography
import Keel

/// Leads to the nearest unexplored opening by sound, while the game is in front.
///
/// Speech only for changes nobody could otherwise know about: the guide going on or off,
/// the map being lost for more than a moment, a new map, nothing left to explore in sight.
/// The beacon carries everything continuous. Silence whenever the minimap cannot be read —
/// a wrong direction is worse than none.
@MainActor
final class Guide: ObservableObject {
    @Published private(set) var isOn = false
    @Published private(set) var status = "Guide off."

    private let reader = LiveReader()
    private let beacon = Beacon()
    private let haptics = Haptics()
    private let objectives = ObjectiveWatcher()
    private let mapScreen = MapScreenWatcher()
    /// The dungeon's objective as last read from the tracker.
    private(set) var objective: Objective?
    /// The area name above the minimap, as last read.
    private var area: String?
    private var wasOnCourse = false

    /// The owner's vibration switch, mirrored from Haptics so the window can bind to it.
    @Published var vibration: Bool {
        didSet {
            haptics.enabled = vibration
            if vibration && isOn { haptics.start() }
            log("Vibration \(vibration ? "on" : "off")")
        }
    }
    private var lastLegible = Date()
    private var lostAnnounced = false
    /// Whether any frame has been readable since the guide started. If none has, the likely
    /// cause is the game's Map Display set to the overlay, and saying so beats "lost".
    private var everLegible = false
    private var noOpeningSince: Date?
    private var noOpeningAnnounced = false
    private var latest: LiveReader.Snapshot?
    /// The opening being led to, and how near it was on the last snapshot — when it vanishes
    /// while near, it was a dead end, and the owner needs to hear that.
    private var leadingTo: (target: CGPoint, distance: Double, epoch: Int)?
    private var lastDeadEnd = Date.distantPast
    private var lastNoOpening = Date.distantPast
    private var ledToMark = false
    private var ledToArch = false
    private var ledToWell = false
    private var ledToArea = false
    private var ledToMarker = false
    private var markerWasInView = false
    private var ledToBeacon = false
    /// Not moving, while facing along the route: since when. Standing still with the stick
    /// pushed the right way is a wall, a crate, a ledge — "something blocking my path,
    /// couldn't hear footsteps" (the owner, 27 Sep). Said after `blockedAfter`, then every
    /// `blockedRepeat` while it lasts.
    private var pushingSince: Date?
    private var pushingFacing: Double?
    private var mapBegan = Date.distantPast
    /// Steering round what the minimap cannot show. Stopped for `steerAfter` while pushing
    /// along the route, the beacon itself swings to one side — the side the map says, else
    /// 50° off the route — and stays there until the player has been moving again for
    /// `steerRelease`; still stuck after `steerSwap`, it tries the other side. The overworld's
    /// own beacon walks the game's navigation mesh round every crate; this is the cane
    /// version of that (the owner, 27 Sep: "it needs to do it on the micro scale").
    private var steerBearing: Double?
    private var steerSide = 1.0
    private var steerSince = Date.distantPast
    private var movingSince: Date?
    static let steerAfter: TimeInterval = 1.0
    static let steerSwap: TimeInterval = 2.0
    static let steerRelease: TimeInterval = 0.6
    static let steerAngle = 40.0 * .pi / 180
    /// A red mark nearer than this is a fight, not a wall: no steering, no "blocked". It was
    /// "no marks anywhere on the minimap" until 29 Sep 2026, when the native render showed a
    /// mark on nearly every frame of the Undercity and the steering never once engaged —
    /// the owner stood against something for two minutes ("that one got me stuck").
    static let enemyNear = 60.0
    /// The spoken "Blocked. Try …" — off while the beacon steers (the owner, 27 Sep: "I don't
    /// need the voice announcing"); the map is no longer painted from it either, since a
    /// wall painted during a fight or a pick-up is a phantom the route then avoids all floor.
    static let speakBlocked = false
    private var lastBlockedSaid = Date.distantPast
    /// Standing still with an enemy mark close by is a fight or a body-block, and a blind
    /// player cannot tell either from a wall: said once, then every `enemiesGap`.
    private var enemiesCloseSince: Date?
    private var lastEnemiesSaid = Date.distantPast
    static let enemiesAfter: TimeInterval = 4
    static let enemiesGap: TimeInterval = 20
    static let blockedAfter: TimeInterval = 2.5
    static let blockedRepeat: TimeInterval = 6
    /// When the marker was last announced, either way: the pinned icon and the in-view
    /// state both flicker, and "Objective marker on the map" was said three times in five
    /// seconds on the 08:15 run.
    private var lastMarkerSaid = Date.distantPast
    static let markerGap: TimeInterval = 20
    /// The lead's compass word as last spoken, and the one waiting to be: said when it has
    /// held for `directionHold` and the last was `directionGap` ago ("it should be telling me
    /// directions more verbosely", the owner, 27 Sep).
    private var spokenDirection: String?
    private var spokenBearing: Double?
    private var directionCandidate: (word: String, since: Date)?
    private var lastDirectionSaid = Date.distantPast
    static let directionHold: TimeInterval = 2
    static let directionGap: TimeInterval = 5
    /// A new word is spoken only when the bearing has moved this far from the one spoken
    /// last: on the 07:25 run the lead sat on the west / north-west boundary and the guide
    /// said "West… North-west… West…" every few seconds.
    static let directionTurn = 60.0 * .pi / 180
    /// Area names the tracker has shown this dungeon, for correcting the map panel's reading.
    private var knownAreas: [String] = []
    /// The Undercity's countdown as last read, for saying the thresholds once each.
    private var lastTimer: Int?
    /// When the timer was last read. Once the run is over the badge goes ("Time expiration
    /// is imminent!" replaces it), and on 28 Sep 2026 the Windows build said "Objective
    /// south, close. 10 seconds" twice in town from a reading half a minute old. A reading
    /// older than `timerStale` counts for nothing.
    private var lastTimerRead = Date.distantPast
    static let timerStale: TimeInterval = 6
    private var freshTimer: Int? { Date().timeIntervalSince(lastTimerRead) <= Self.timerStale ? lastTimer : nil }
    /// When the timer last went up — a kill or a beacon paying. "This fight is not paying"
    /// needs it to have stood still for `notPayingAfter` with the clock short.
    private var lastTimerRise = Date.distantPast
    private var lastNotPaying = Date.distantPast
    static let notPayingAfter: TimeInterval = 15
    static let notPayingGap: TimeInterval = 15
    /// Gap between two time-target announcements: at 8 s a pack moving round the player was
    /// called eight times in 70 s (Windows run 1, 28 Sep 2026).
    static let timeTargetGap: TimeInterval = 20
    /// Whether beacons are time targets (the window's switch); afflicted packs always are.
    var leadToBeacons: Bool {
        get { LiveReader.leadToBeacons }
        set {
            objectWillChange.send()
            LiveReader.leadToBeacons = newValue
            UserDefaults.standard.set(newValue, forKey: "leadToBeacons")
            log("Lead to beacons \(newValue ? "on" : "off")")
        }
    }
    /// A travel objective just began: say "Looking outside …" with the first lead, unless
    /// that lead is straight to the area itself.
    private var leaveToSay: String?
    /// While leading back: when there stopped being a route to the spot, if there is none.
    private var spotRouteMissingSince: Date?
    /// Within this many minimap pixels of the spot, the owner is back.
    static let arrived = 45.0
    private var announcedLandmarks: [Landmark: Date] = [:]
    private var announcedObjectives: [(kind: String, at: Date)] = []
    static let announceGap: TimeInterval = 60
    /// When red marks were last being led to — "Marked enemies" is said only after a gap.
    private var lastMarkLead = Date.distantPast
    /// When guidance last existed; a gap shorter than `holdGap` keeps the beacon going, so
    /// an opening flickering at the edge of the map does not stutter the sound.
    private var lastGuidance = Date.distantPast
    private var lastLog = Date.distantPast

    /// How long the map may be unreadable before it is said out loud — menus, the full
    /// map and loading screens are usually shorter than this.
    static let lostAfter: TimeInterval = 3
    static let noOpeningAfter: TimeInterval = 3
    static let noOpeningRepeat: TimeInterval = 30
    static let holdGap: TimeInterval = 2
    /// An opening that disappears while its route is shorter than this was a dead end.
    static let deadEndNear = 150.0
    /// How closely the direction of travel must match the route to count as on course.
    static let onCourseAngle = 25.0 * .pi / 180
    static let nearAngle = 60.0 * .pi / 180

    /// The beacon's volume, 0…1, for the window's slider and the louder/quieter keys.
    var beaconVolume: Double {
        get { beacon.volume }
        set { beacon.volume = newValue; objectWillChange.send() }
    }

    func louder() { changeVolume(by: 0.1) }
    func quieter() { changeVolume(by: -0.1) }

    private func changeVolume(by step: Double) {
        beaconVolume = (beaconVolume + step).clamped(0, 1)
        tell("Beacon \(Int((beaconVolume * 100).rounded())) percent.", sound: "Pop")
    }

    var onChange: (() -> Void)?

    /// For `--guide-test`: no speech, no sounds, no beacon — everything else as live.
    var silentTest = false

    init() {
        vibration = true
        vibration = haptics.enabled
        reader.onSnapshot = { [weak self] snapshot in self?.handle(snapshot) }
        objectives.onObjective = { [weak self] new in self?.objectiveChanged(new) }
        objectives.onArea = { [weak self] name in self?.areaChanged(name) }
        objectives.onCount = { [weak self] count in self?.countChanged(count) }
        objectives.onTimer = { [weak self] seconds in self?.timerChanged(seconds) }
        objectives.onFloor = { [weak self] number, of in self?.floorChanged(number, of: of) }
        mapScreen.onMapOpened = { [weak self] areas in self?.describeMap(areas: areas) }
        objectives.onTextTrouble = { [weak self] in
            self?.tell("The objective text can't be read at the moment, so the guide is exploring by itself.", sound: "Basso")
        }
        reader.onStopped = { [weak self] error in
            guard let self, self.isOn else { return }
            self.finish(saying: "Guide stopped: \(error?.localizedDescription ?? "the window went away").")
        }
    }

    func start(_ target: (GameWindow, SCWindow)?) async {
        guard !isOn else { return }
        guard let (chosen, window) = target else {
            tell("Choose a window first.", sound: "Basso")
            return
        }
        do {
            let where_ = try await reader.start(window: window)
            log("Guide on: \(chosen.label); \(where_)")
            objective = nil
            do { try await objectives.start(window: window) } catch {
                log("Objective watcher did not start: \(error.localizedDescription)")
            }
            do { try await mapScreen.start(window: window) } catch {
                log("Map-screen watcher did not start: \(error.localizedDescription)")
            }
            beacon.startEngine()
            // The controller is left alone entirely unless vibration is on.
            if haptics.enabled { haptics.start() }
            wasOnCourse = false
            isOn = true
            lastLegible = Date()
            lostAnnounced = false
            everLegible = false
            leadingTo = nil
            // Until the tracker is read, lead to openings; the first objective sets this.
            ledToMark = false
            reader.intent.withLock { $0 = .none }
            reader.leadToSpot.withLock { $0 = false }
            ledToArch = false
            ledToWell = false
            ledToArea = false
            ledToMarker = false
            ledToBeacon = false
            pushingSince = nil
            reader.timeLeft.withLock { $0 = nil }
            spokenDirection = nil
            spokenBearing = nil
            directionCandidate = nil
            knownAreas = []
            lastTimer = nil
            lastTimerRead = .distantPast; lastTimerRise = .distantPast; lastNotPaying = .distantPast
            floorSeen = nil
            leaveToSay = nil
            announcedObjectives = []
            noOpeningSince = nil
            noOpeningAnnounced = false
            status = "Guide on, reading \(chosen.appName)."
            tell("Guide on.", sound: "Tink")
        } catch {
            tell("Guide could not start: \(error.localizedDescription)", sound: "Basso")
        }
        onChange?()
    }

    func stop() {
        guard isOn else { return }
        finish(saying: "Guide off.")
    }

    private func finish(saying text: String) {
        reader.saveMap(to: AppLog.directory.appendingPathComponent("guide-map-\(Self.stamp()).png"))
        reader.stop()
        objectives.stop()
        mapScreen.stop()
        beacon.silence()
        isOn = false
        status = text
        tell(text, sound: "Bottle")
        onChange?()
    }

    /// Left grip, right grip, then the on-course glide, so the owner can check the
    /// vibration by hand. Opens the controller first if the guide has not.
    func testVibration() {
        let wasStarted = haptics.available
        haptics.start()
        DispatchQueue.main.asyncAfter(deadline: .now() + (wasStarted ? 0 : 2)) { [weak self] in
            guard let self else { return }
            if self.haptics.available {
                self.haptics.test()
            } else {
                self.tell("No vibration: the controller needs to be plugged in by USB.", sound: "Basso")
            }
        }
    }

    /// Marks where the owner is standing, to be led back to later.
    func markSpot() {
        guard isOn else { tell("Guide is off.", sound: "Basso"); return }
        reader.markSpot { [weak self] marked in
            self?.tell(marked ? "Spot marked." : "Can't mark it yet: the map isn't read.", sound: marked ? "Pop" : "Basso")
        }
    }

    /// Leads back to the marked spot; pressed again, goes back to what it was doing.
    func takeMeBack() {
        guard isOn else { tell("Guide is off.", sound: "Basso"); return }
        if reader.leadToSpot.withLock({ $0 }) {
            reader.leadToSpot.withLock { $0 = false }
            tell("Stopped leading back.", sound: "Pop")
            return
        }
        reader.hasSpot { [weak self] has in
            guard let self else { return }
            guard has else { self.tell("No spot marked.", sound: "Basso"); return }
            self.reader.leadToSpot.withLock { $0 = true }
            self.spotRouteMissingSince = nil
            self.tell("Leading back to the marked spot.", sound: "Pop")
        }
    }

    func newMap() {
        guard isOn else { tell("Guide is off.", sound: "Basso"); return }
        reader.newMap()
        tell("New map.", sound: "Pop")
    }

    /// A new objective kind is said; a changed count is only logged (it is there on
    /// request). The first objective after starting is said too, so the owner knows what
    /// the guide believes it is.
    private func objectiveChanged(_ new: Objective) {
        // A boss's name shown above the tracker pushes every line down one: "Blood Magus"
        // read as the place and "Forbidden City" as the objective (25 Sep). An objective
        // that is the dungeon's own name is that, not an objective.
        if let place = objective?.place, new.text == place {
            log("Ignored tracker reading \(new.place) → \(new.text): shifted by a line above")
            return
        }
        let previous = objective
        objective = new
        log("Objective: \(new.place) → \(new.text)")
        // Said when it is a different objective from the last, and from any said in the
        // last five minutes. The same words give or take a few letters are the same
        // objective: "Seaborn", "Seabom", "Gpddess" and "Goildess" were each announced on
        // the 26 Sep Mariner's Refuge run, fourteen times for one objective.
        let differs = previous.map { !(Objective.alike($0.kind, new.kind) && Objective.alike($0.place, new.place)) } ?? true
        let saidLately = announcedObjectives.contains { Objective.alike($0.kind, new.kind) && Date().timeIntervalSince($0.at) < 300 }
        if isOn, differs, !saidLately {
            announcedObjectives.append((new.kind, Date()))
            tell("Objective: \(Self.spoken(new, count: new.count)).", sound: "Glass")
        }
        updateIntent()
    }

    /// The tracker's count moved: "Destroy the Seaborn Goddess: 3" became 2. The green
    /// number is the one piece of progress a sighted player watches.
    private func countChanged(_ count: Int) {
        log("Count: \(count)")
        guard isOn, count > 0 else { return }
        tell("\(count) to go.", sound: "Glass")
    }

    /// The Undercity's countdown moved. Said at a minute, thirty seconds and ten, once each
    /// on the way down, and whenever it jumps up — time bought by igniting a beacon or a
    /// kill. Sighted players glance at the badge; nothing else says it.
    private func timerChanged(_ seconds: Int) {
        defer { lastTimer = seconds }
        log("Timer: \(seconds)")
        reader.timeLeft.withLock { $0 = seconds }
        lastTimerRead = Date()
        if let prev = lastTimer, seconds > prev { lastTimerRise = Date() }
        guard isOn else { return }
        if let last = lastTimer, seconds >= last + 15 {
            tell("Time \(seconds).", sound: nil)
            return
        }
        for mark in [60, 30, 10] where seconds <= mark && (lastTimer ?? .max) > mark {
            tell("\(mark) seconds.", sound: nil)
            return
        }
    }

    /// The Undercity's floor counter moved: a new floor is a new map (on 27 Sep the second
    /// floor was stitched onto the first as if next door), and worth hearing.
    private var floorSeen: Int?
    private func floorChanged(_ number: Int, of: Int) {
        log("Floor: \(number) of \(of)")
        defer { floorSeen = number }
        guard isOn else { return }
        if floorSeen != nil { reader.newMap(); mapBegan = Date() }
        tell("Floor \(number) of \(of).", sound: "Glass")
    }

    /// The objective's words with its count as progress: "Destroy the Seaborn Goddess, 3 to go".
    nonisolated static func spoken(_ objective: Objective, count: Int?) -> String {
        count.map { $0 > 0 ? "\(objective.kind), \($0) to go" : objective.kind } ?? objective.kind
    }

    /// The area name above the minimap changed. The game's screen reader announces zones,
    /// so nothing is said; the map records it and the intent is worked out again.
    private func areaChanged(_ name: String) {
        log("Area: \(name)")
        area = name
        if !knownAreas.contains(name) { knownAreas.append(name) }
        reader.setArea(name)
        updateIntent()
    }

    /// What the objective asks, given the area: slay → red marks; travel → unused arches;
    /// "… in the <area>" while there → stay; travelling elsewhere → leave this area.
    private func updateIntent() {
        let intent = objective?.intent(currentArea: area, timed: objectives.timedRun.withLock { $0 }) ?? .none
        let before = reader.intent.withLock { $0 }
        reader.intent.withLock { $0 = intent }
        guard intent != before else { return }
        log("Intent: slay \(intent.slay) travel \(intent.travel) stay \(intent.stayIn ?? "-") leave \(intent.leave ?? "-") to \(intent.destination ?? "-") timed \(intent.timed)")
        // Said with the next lead, which knows whether the destination is somewhere already
        // walked through (then "Leading to …" says it) or somewhere still to find.
        if isOn, let here = intent.leave, before.leave != here { leaveToSay = here } else { leaveToSay = nil }
        if intent.leave == nil { ledToArea = false }
    }

    /// Reads out what the map knows: healing wells, arches, the marked spot, marked
    /// enemies — each with its direction and distance from the player. What a sighted
    /// player gets from opening the full map.
    func describeMap(areas: [String] = []) {
        guard isOn else { tell("Guide is off.", sound: "Basso"); return }
        // Never re-announce an objective kind within five minutes; a description is enough.
        // The panel's list, less what the fast recogniser makes of it ("Ho]lowCa rns").
        // The panel's names, corrected to the tracker's spelling where they are close
        // ("Ziggurai Distrirt", 07:25 run); the rest only if they read like names.
        let names = areas.compactMap { read -> String? in
            if let known = knownAreas.first(where: { Objective.alike($0, read) }) { return known }
            return MapScreen.looksLikeName(read) ? read : nil
        }
        let unexplored = names.contains { $0.lowercased().hasPrefix("unexplored") }
        let listed = names.filter { !$0.lowercased().hasPrefix("unexplored") }
        var prefix = ""
        if !listed.isEmpty {
            prefix = "Map. Areas: " + listed.joined(separator: ", ") + (unexplored ? ", and unexplored areas. " : ". ")
        } else if unexplored {
            prefix = "Map. Unexplored areas remain. "
        }
        reader.describe { [weak self] text in self?.tell(prefix + text, sound: nil) }
    }

    /// Reads out the objective, counts included.
    func sayObjective() {
        guard isOn else { tell("Guide is off.", sound: "Basso"); return }
        // The watcher's copy is the best spelling so far and carries the count.
        let (mirror, count) = objectives.current.withLock { $0 }
        guard let objective = mirror ?? objective else { tell("Can't read the objective.", sound: "Basso"); return }
        tell("\(objective.place). \(Self.spoken(objective, count: count ?? objective.count)).", sound: nil)
    }

    /// Says where the beacon is leading, for when the tick alone is not enough.
    func sayWhere() {
        guard isOn else { tell("Guide is off.", sound: "Basso"); return }
        guard let snapshot = latest, snapshot.legible else { tell("Can't read the map.", sound: "Basso"); return }
        guard let g = snapshot.guidance else { tell("No unexplored openings in sight.", sound: "Pop"); return }
        let howFar = Self.howFar(g.distance)
        let also = Self.alsoUnexplored(g.others)
        if g.toSpot {
            tell("Marked spot \(Compass.word(g.bearing)), \(howFar).\(also)", sound: nil)
        } else if g.toArea {
            let place = reader.intent.withLock { $0 }.destination ?? "The area"
            tell("\(place) \(Compass.word(g.bearing)), \(howFar).\(also)", sound: nil)
        } else if g.toWell {
            tell("Unexplored ground by the healing well, \(Compass.word(g.bearing)), \(howFar).", sound: nil)
        } else if g.toArch {
            tell("Arch \(Compass.word(g.bearing)), \(howFar).", sound: nil)
        } else if g.toMark {
            let more = g.marks > 1 ? " \(g.marks) marked." : ""
            tell("Marked enemy \(Compass.word(g.bearing)), \(howFar).\(more)", sound: nil)
        } else if snapshot.toBeacon {
            tell("\(snapshot.timeTarget) \(Compass.word(snapshot.markerBearing ?? g.bearing)), \(howFar). It buys time.\(also)", sound: nil)
        } else if g.markerInView {
            tell("Objective marker \(Compass.word(g.bearing)), \(howFar).\(also)", sound: nil)
        } else if g.beeline {
            tell("Straight on \(Compass.word(g.bearing)), towards the objective marker.\(also)", sound: nil)
        } else if g.toMarker, let b = snapshot.markerBearing {
            tell("Opening \(Compass.word(g.bearing)), \(howFar), towards the objective marker \(Compass.word(b)).\(also)", sound: nil)
        } else {
            tell("Opening \(Compass.word(g.bearing)), \(howFar).\(also)", sound: nil)
        }
    }

    /// The other unexplored openings as words: the nearest in each direction, nearest
    /// first, four at most — "north, close; east, further off".
    nonisolated static func directions(_ openings: [(bearing: Double, distance: Double)]) -> [String] {
        var nearest: [String: Double] = [:]
        for o in openings {
            let word = Compass.word(o.bearing)
            nearest[word] = min(nearest[word] ?? .infinity, o.distance)
        }
        return nearest.sorted { $0.value < $1.value }.prefix(4).map { "\($0.key), \(howFar($0.value))" }
    }

    nonisolated static func alsoUnexplored(_ openings: [(bearing: Double, distance: Double)]) -> String {
        let list = directions(openings)
        return list.isEmpty ? "" : " Also unexplored: " + list.joined(separator: "; ") + "."
    }

    private func handle(_ snapshot: LiveReader.Snapshot) {
        guard isOn else { return }
        latest = snapshot
        let now = Date()

        guard snapshot.legible else {
            beacon.silence()
            // A menu or banner over the screen: the tracker is not to be trusted either, and
            // the full map may be what is up — the map-screen watcher looks while this lasts.
            objectives.mapVisible.withLock { $0 = false }
            mapScreen.armed.withLock { $0 = true }
            if !lostAnnounced && now.timeIntervalSince(lastLegible) > Self.lostAfter {
                lostAnnounced = true
                if everLegible {
                    // Said to nobody: once the minimap has been read, an unreadable one is
                    // almost always a menu the owner opened themselves (the 26 Sep run: the
                    // inventory for two and a half minutes) — they know. Logged only.
                    status = "Minimap covered."
                    log("Minimap unreadable for \(Int(Self.lostAfter)) s — a menu, probably")
                } else {
                    status = "Can't see the minimap. The guide reads the minimap, not the Map Overlay."
                    tell("Can't see the minimap. The guide needs Map Display set to Minimap.", sound: "Basso")
                }
            }
            logOccasionally(snapshot)
            return
        }
        lastLegible = now
        everLegible = true
        objectives.mapVisible.withLock { $0 = true }
        mapScreen.disarm()
        if lostAnnounced {
            lostAnnounced = false
            status = "Guide on."
            log("Map readable again")
        }
        if snapshot.newMap { mapBegan = now; tell("New map.", sound: "Pop") }
        // A timer not read for a while is gone (the run is over, or the badge is covered):
        // forget it, so nothing is said or routed on a stale count.
        if lastTimer != nil, freshTimer == nil {
            log("Timer: stale, forgotten")
            lastTimer = nil
            reader.timeLeft.withLock { $0 = nil }
        }
        // A healing well or an arch coming into view is what a sighted player would notice
        // on the minimap: said once, when first confirmed.
        for (kind, bearing, distance) in snapshot.newLandmarks {
            // The same icon can be confirmed twice when the map drifts a little over a long
            // run (the 26 Sep log said "Arch, south-east" four times): one of a kind within
            // `announceGap` seconds is said once.
            if let last = announcedLandmarks[kind], now.timeIntervalSince(last) < Self.announceGap { continue }
            announcedLandmarks[kind] = now
            tell("\(kind.rawValue), \(Compass.word(bearing)), \(Self.howFar(distance)).", sound: "Glass")
        }

        if reader.leadToSpot.withLock({ $0 }) {
            if let g = snapshot.guidance, g.toSpot {
                spotRouteMissingSince = nil
                if g.distance < Self.arrived {
                    reader.leadToSpot.withLock { $0 = false }
                    tell("Back at the marked spot.", sound: "Glass")
                }
            } else if spotRouteMissingSince == nil {
                spotRouteMissingSince = now
            } else if let since = spotRouteMissingSince, now.timeIntervalSince(since) > 3 {
                spotRouteMissingSince = .distantFuture   // said once
                tell("No way back to the spot on the map yet. Exploring.", sound: "Basso")
            }
        }

        if let g = snapshot.guidance {
            noOpeningSince = nil
            lastGuidance = now
            // A dead end is the navigator saying the opening it led to has closed, with the
            // route to it short — not merely a change of target, which is what the first
            // version tested and why it said "dead end" to a player standing still.
            if let previous = leadingTo, previous.epoch == g.mapEpoch,
               g.previousClosed,
               previous.distance < Self.deadEndNear,
               now.timeIntervalSince(lastDeadEnd) > 8,
               now.timeIntervalSince(mapBegan) > 6 {   // not while a new floor's map is settling (28 Sep: one 2.5 s in)
                lastDeadEnd = now
                haptics.deadEnd()
                tell("Dead end. Next opening \(Compass.word(g.bearing)), \(Self.howFar(g.distance)).", sound: "Pop")
            } else if noOpeningAnnounced {
                tell("Opening \(Compass.word(g.bearing)), \(Self.howFar(g.distance)).", sound: "Pop")
            }
            leadingTo = (g.target, g.distance, g.mapEpoch)
            // Say when the lead changes kind — to marked enemies, or back to exploring —
            // at most every 10 s, so a mark flickering at the edge of the map is not a chatter.
            // "Marked enemies" when the lead turns to them after 20 s without; nothing when it
            // turns back — marks come and go as enemies move in and out of view, and the
            // "no marked enemies left" line was said sixteen times in one run.
            if g.toMark && !ledToMark, now.timeIntervalSince(lastMarkLead) > 20 {
                tell("Marked enemies \(Compass.word(g.bearing)).", sound: "Glass")
            }
            if g.toMark { lastMarkLead = now }
            ledToMark = g.toMark
            if g.toArea && !ledToArea {
                let place = reader.intent.withLock { $0 }.destination ?? "the area"
                tell("Leading to \(place), \(Compass.word(g.bearing)), \(Self.howFar(g.distance)).", sound: "Glass")
                leaveToSay = nil
            }
            ledToArea = g.toArea
            // The objective marker pinned to the minimap's edge: the route now heads its way.
            if g.toMarker && !ledToMarker, let b = snapshot.markerBearing, now.timeIntervalSince(lastMarkerSaid) > Self.markerGap {
                lastMarkerSaid = now
                tell("Objective marker \(Compass.word(b)). Heading for it.", sound: "Glass")
            }
            if snapshot.toBeacon && !ledToBeacon, now.timeIntervalSince(lastMarkerSaid) > Self.timeTargetGap {
                lastMarkerSaid = now
                let short = (freshTimer ?? 999) <= LiveReader.shortTime
                let verb = snapshot.timeTarget == "Beacon" ? "Light it for time." : "Kill it for time."
                tell((short ? "Short on time. " : "") + "\(snapshot.timeTarget) \(Compass.word(snapshot.markerBearing ?? g.bearing)), \(Self.howFar(g.distance)). \(verb)", sound: "Glass")
            } else if g.markerInView && !markerWasInView && !snapshot.toBeacon, now.timeIntervalSince(lastMarkerSaid) > Self.markerGap {
                lastMarkerSaid = now
                tell("Objective marker on the map, \(Compass.word(g.bearing)), \(Self.howFar(g.distance)).", sound: "Glass")
            }
            // The boss room on the map with the clock nearly out: say where it is every ten
            // seconds, so a fight can be broken off for it (08:15 run: out of time 40 px
            // from the door, fighting an event's spirits).
            if g.markerInView, !snapshot.toBeacon, let left = freshTimer, left <= 30,
               now.timeIntervalSince(lastMarkerSaid) >= 10 {
                lastMarkerSaid = now
                tell("Objective \(Compass.word(g.bearing)), \(Self.howFar(g.distance)). \(left) seconds.", sound: nil)
            } else if reader.intent.withLock({ $0 }).timed, let t = freshTimer, t <= LiveReader.shortTime,
                      snapshot.heading == nil, g.marks > 0,
                      now.timeIntervalSince(lastTimerRise) >= Self.notPayingAfter,
                      now.timeIntervalSince(lastNotPaying) >= Self.notPayingGap {
                // A fight that is not paying: clock short, standing still among red marks, and
                // the timer has not gone up for a while. Run 1 of 28 Sep (Windows) lost floor 2
                // to an Executioner elite fought from 67 s down to 10 with the boss marker
                // pinned east the whole time.
                lastNotPaying = now
                let towards = snapshot.markerBearing ?? g.bearing
                tell("This fight is not paying. Objective \(Compass.word(towards)). \(t) seconds.", sound: nil)
            }
            ledToBeacon = snapshot.toBeacon
            markerWasInView = g.markerInView
            ledToMarker = g.toMarker
            if let here = leaveToSay {
                leaveToSay = nil
                // With the objective marker showing, the marker is the answer, not the area.
                if !g.toMarker { tell("Looking outside \(here).", sound: "Glass") }
            }
            if g.toArch && !ledToArch {
                tell("Leading to an arch \(Compass.word(g.bearing)). It may be the way in.", sound: "Glass")
            }
            ledToArch = g.toArch
            if g.toWell && !ledToWell {
                tell("Leading to unexplored ground by the healing well, \(Compass.word(g.bearing)). The way on is usually near one.", sound: "Glass")
            }
            ledToWell = g.toWell
            if noOpeningAnnounced { noOpeningAnnounced = false; status = "Guide on." }
            // How well the character faces the route: from the minimap arrow when it can be
            // read (it turns with the stick even standing still), else the direction of travel.
            var accuracy = Beacon.Accuracy.away
            if let facing = snapshot.facing ?? snapshot.heading {
                var difference = abs(facing - g.bearing).truncatingRemainder(dividingBy: 2 * .pi)
                if difference > .pi { difference = 2 * .pi - difference }
                accuracy = difference < Self.onCourseAngle ? .on : (difference < Self.nearAngle ? .near : .away)
            }
            let onCourse = accuracy == .on
            // The direction in words, whenever it settles on a new one.
            let word = Compass.word(g.bearing)
            if directionCandidate?.word != word { directionCandidate = (word, now) }
            var turned = true
            if let last = spokenBearing {
                var d = abs(g.bearing - last).truncatingRemainder(dividingBy: 2 * .pi)
                if d > .pi { d = 2 * .pi - d }
                turned = d >= Self.directionTurn
            }
            if word != spokenDirection, turned, let since = directionCandidate?.since,
               now.timeIntervalSince(since) >= Self.directionHold, now.timeIntervalSince(lastDirectionSaid) >= Self.directionGap {
                spokenDirection = word
                spokenBearing = g.bearing
                lastDirectionSaid = now
                tell(word.prefix(1).uppercased() + word.dropFirst() + ".", sound: nil)
            }
            // Micro steering: see the properties above. Decided before the beacon points.
            let enemyClose = (snapshot.nearestMark ?? .infinity) < Self.enemyNear
            let stalled = snapshot.heading == nil && accuracy != .away && !enemyClose && now.timeIntervalSince(mapBegan) > 6
            // Still, with a mark close: say so, because the beacon will not steer round it.
            if snapshot.heading == nil && enemyClose {
                if enemiesCloseSince == nil { enemiesCloseSince = now }
                if let since = enemiesCloseSince, now.timeIntervalSince(since) >= Self.enemiesAfter,
                   now.timeIntervalSince(lastEnemiesSaid) >= Self.enemiesGap {
                    lastEnemiesSaid = now
                    log("Enemies close for \(Int(now.timeIntervalSince(since))) s, nearest mark \(Int(snapshot.nearestMark ?? 0)) px")
                    tell("Enemies close.", sound: nil)
                }
            } else {
                enemiesCloseSince = nil
            }
            if snapshot.heading != nil {
                if movingSince == nil { movingSince = now }
            } else {
                movingSince = nil
            }
            // Steer only with the facing held steady: in a fight the character turns.
            var facingSteady = true
            if let f0 = pushingFacing, let f = snapshot.facing {
                var d = abs(f - f0).truncatingRemainder(dividingBy: 2 * .pi)
                if d > .pi { d = 2 * .pi - d }
                facingSteady = d < 25 * .pi / 180
            }
            if stalled {
                if pushingSince == nil { pushingSince = now; pushingFacing = snapshot.facing }
                if facingSteady, let since = pushingSince, now.timeIntervalSince(since) >= Self.steerAfter {
                    if steerBearing == nil {
                        steerSince = now
                        steerSide = snapshot.sidestep.map { side -> Double in
                            var d = (side - g.bearing).truncatingRemainder(dividingBy: 2 * .pi)
                            if d > .pi { d -= 2 * .pi }; if d < -.pi { d += 2 * .pi }
                            return d >= 0 ? 1 : -1
                        } ?? steerSide
                    } else if now.timeIntervalSince(steerSince) >= Self.steerSwap {
                        steerSide = -steerSide
                        steerSince = now
                    }
                    steerBearing = g.bearing + steerSide * Self.steerAngle
                }
            } else if let moving = movingSince, now.timeIntervalSince(moving) >= Self.steerRelease {
                steerBearing = nil
            }
            if !silentTest {
                beacon.point(bearing: steerBearing ?? g.bearing, accuracy: steerBearing == nil ? accuracy : .near)
                // On course: one glide on arriving there, then nothing — the double tick is
                // already saying it. Off course: a tap a second towards the route.
                if onCourse {
                    if !wasOnCourse { haptics.onCourse() }
                } else {
                    haptics.direction(bearing: g.bearing)
                }
            }
            wasOnCourse = onCourse
            // Blocked: still, facing within 45° of the route, no enemies marked close by.
            // …and not in the first seconds of a map, standing at the entrance (09:36 run).
            let pushing = stalled
            // …and facing steadily: in a fight the character spins, and that is not a wall.
            var steady = true
            if let f0 = pushingFacing, let f = snapshot.facing {
                var d = abs(f - f0).truncatingRemainder(dividingBy: 2 * .pi)
                if d > .pi { d = 2 * .pi - d }
                steady = d < 25 * .pi / 180
            }
            if pushing && steady {
                if let since = pushingSince, now.timeIntervalSince(since) >= Self.blockedAfter,
                   now.timeIntervalSince(lastBlockedSaid) >= Self.blockedRepeat {
                    lastBlockedSaid = now
                    log("Blocked for \(Int(now.timeIntervalSince(since))) s, steering \(steerBearing.map { Compass.word($0) } ?? "-"); nearest mark \(snapshot.nearestMark.map { "\(Int($0)) px" } ?? "none")")
                    if Self.speakBlocked {
                        if let side = snapshot.sidestep {
                            tell("Blocked. Try \(Compass.word(side)).", sound: nil)
                        } else {
                            tell("Blocked. Step back and try again.", sound: nil)
                        }
                    }
                }
            } else if !pushing {
                pushingSince = nil
                pushingFacing = nil
            }
        } else {
            steerBearing = nil
            pushingSince = nil
            if now.timeIntervalSince(lastGuidance) > Self.holdGap { beacon.silence() }
            if noOpeningSince == nil { noOpeningSince = now }
            if !noOpeningAnnounced, let since = noOpeningSince, now.timeIntervalSince(since) > Self.noOpeningAfter {
                noOpeningAnnounced = true
                status = "No unexplored openings in sight."
                if now.timeIntervalSince(lastNoOpening) > Self.noOpeningRepeat {
                    lastNoOpening = now
                    let wasNear = (leadingTo?.distance ?? .infinity) < Self.deadEndNear
                    if wasNear { haptics.deadEnd() }
                    tell(wasNear ? "Dead end. No other openings in sight." : "No unexplored openings in sight.", sound: "Pop")
                }
                leadingTo = nil
            }
        }
        logOccasionally(snapshot)
    }

    /// One line a second: enough to reconstruct what the guide did, not a flood.
    private func logOccasionally(_ s: LiveReader.Snapshot) {
        let now = Date()
        guard now.timeIntervalSince(lastLog) >= 1 else { return }
        lastLog = now
        let heading = s.heading.map { Compass.word($0) } ?? "still"
        if let g = s.guidance {
            // What the lead is, not just where: the 28 Sep runs could only be read by inference.
            let kind = g.toSpot ? "spot" : s.toBeacon ? (s.timeTarget == "Beacon" ? "beacon" : "pack")
                : g.markerInView ? "marker on map" : g.toMarker ? (g.beeline ? "marker beeline" : "opening to marker")
                : g.toMark ? "mark" : g.toArea ? "area" : g.toWell ? "well" : g.toArch ? "arch" : "opening"
            log(String(format: "Guide: %.0f ms, contrast %.1f, floor %.0f %%, busy %.2f, moving %@, lead %@ (%.0f°) %.0f px, %d openings, to %@%@",
                       s.milliseconds, s.contrast, s.floorFraction * 100, s.busyFraction, heading,
                       Compass.word(g.bearing), g.bearing * 180 / .pi, g.distance, g.openings, kind,
                       g.marks > 0 ? ", \(g.marks) marks" : ""))
        } else {
            log(String(format: "Guide: %.0f ms, %@, contrast %.1f, floor %.0f %%, busy %.2f, moving %@", s.milliseconds,
                       s.legible ? "no opening" : "illegible", s.contrast, s.floorFraction * 100, s.busyFraction, heading))
        }
    }

    /// The sound plays only when VoiceOver is off — it is the channel for a sighted player.
    /// With VoiceOver on, speech is the whole message: a chime before every line was heard
    /// as an error bonk, then as a chime that "shouldn't be there" (the owner, 26 Sep).
    private func tell(_ text: String, sound: String?) {
        log("Say: \(text)")
        guard !silentTest else { return }
        if let sound, !Announcer.isVoiceOverRunning { NSSound(named: sound)?.play() }
        Announcer.say(text)
    }

    nonisolated static func howFar(_ distance: Double) -> String {
        distance < 120 ? "close" : (distance < 250 ? "a little way" : (distance < 600 ? "further off" : "a long way back"))
    }

    private static func stamp() -> String {
        let formatter = DateFormatter()
        formatter.dateFormat = "yyyy-MM-dd-HHmmss"
        return formatter.string(from: Date())
    }
}

extension Double {
    func clamped(_ low: Double, _ high: Double) -> Double { Swift.max(low, Swift.min(high, self)) }
}
