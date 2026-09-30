# Working on Dungeon Guide

A navigation aid for Diablo IV dungeons, for a blind player who streams the game (Shadow PC,
GeForce NOW, PS Remote Play) to this Mac. It reads only the stream window's pixels and sound
and never sends input to the game. `DESIGN.md` holds the research and the plan; read it first.
"Dungeon Guide" is a working name.

The owner is blind and uses VoiceOver full-time. Muteny's CLAUDE.md (`../Muteny/CLAUDE.md`)
describes how to work with them, and its rules apply here in full: labels that make sense out
of context, speech only for what VoiceOver cannot already see, headings in prose replies,
never "quietly", say "unverified" when it is.

## Build and run

    ./build.sh

Builds with SwiftPM into `~/Library/Developer/DungeonGuide` (never inside this folder:
`~/Desktop` is synced by iCloud and codesign rejects the bundle), wraps the binary with
`Info.plist` into `Dungeon Guide.app`, signs it with the Apple Development certificate (so the
Screen Recording grant survives rebuilds), installs to `/Applications` and relaunches. Read the
newest log in `~/Library/Application Support/Dungeon Guide/logs/` after every build.

It is a Swift package, not an Xcode project, and depends on `../Keel` by path. Keel's
`AppLog.appName` is set to "Dungeon Guide" as the first line of `main.swift`; that setting was
added to Keel for this app and defaults to "Muteny".

## Stage 0: the recorder (built 25 Sep 2026)

- Choose a window from a list (stream apps first: Shadow PC `com.electron.shadow`, GeForce NOW
  `com.nvidia.gfnpc.mall`, PS Remote Play `com.playstation.RemotePlay`, Muteny's player).
  The choice is remembered by bundle id and title, not window number, and re-picked on every
  refresh; the list refreshes when the app comes forward and before every recording.
- Records the window alone (ScreenCaptureKit, `desktopIndependentWindow`) with that app's
  sound, to HEVC .mov in `~/Movies/Dungeon Guide/`, at the window's pixel size, up to 30 fps,
  0.3 bits per pixel per frame, key frame every 2 s. The high bitrate is deliberate: the video
  has already been compressed once by the streaming service.
- A `.txt` beside each recording: window, app, size, start time, and marks
  ("0:03:12.4 mark") from Control-Shift-Command-M. Pictures (Control-Shift-Command-P) are PNGs
  at full size, noted in the text file if taken during a recording.
- Control-Shift-Command-R starts and stops. Carbon hot keys, no permission needed. **Unverified:
  whether each streaming app lets these through while it has the keyboard** — every press is
  logged as `Hot key: …`, so the log answers it. The menu bar item ("DG", "● DG" while
  recording) does everything too.
- Speech through Keel's Announcer (VoiceOver on only) plus a system sound for each event.
  If no picture arrives in the first 5 s it says so once. Quitting while recording finishes
  the file first. Refuses to start with under 5 GB free.

Known and accepted: AVAssetWriter's `add`, `startWriting`, `append`, `isReadyForMoreMediaData`
and `expectsMediaDataInRealTime` are deprecated in macOS 27 in favour of
`inputReceiver(for:)` / `SampleBufferReceiver.appendImmediately`, which take a
`CMReadySampleBuffer`. Not migrated yet because converting ScreenCaptureKit's `CMSampleBuffer`
to that type was not worked out; the old calls still work. These are the build's only warnings.

## What the first recording showed (25 Sep 2026, Forbidden City, Shadow, windowed)

- **Shadow's stream is a separate app**, `com.blade.shadow-macos`, window "Shadow PC - Display"
  (2646×1720 here, title bar included). The Electron launcher `com.electron.shadow` gave zero
  frames. The list now puts the Display window first and calls the other "Shadow PC launcher".
- Recording works: 115 s, ~28 fps, sound captured, 590 MB, nothing dropped. No `Hot key:`
  lines — the keys were not tried, so whether they pass through Shadow is still unknown.
- **Minimap**: 446×320 px at 2166,135 in that window. Floor ≈ 40, void ≈ 23 (luma, full
  range) — low but constant contrast; Otsu on a radius-4 box blur gives threshold 31–32 on
  every frame. Walls are hatching, removed by the blur.
- **The arrow** is dimmer than it looks: peaks ≈ 145, ~220 px ≥ 100. It sits close to the
  minimap's centre (x 209–236, y 147–173) and turns with the character's facing.
- **The marker clamped to the bottom edge is the tracked quest** ("Conquer a Nightmare
  Dungeon" icon with a pin), not the dungeon objective. No dungeon objective marker appeared.
- **MapLab** (`swift build -c release --product MapLab`, then `MapLab <mov> <out>`) reads
  a recording offline: all 547 samples (every 0.2 s) placed with agreement 0.95–0.98, arrow
  found in all, 115 s of video in ~9 s. `map.png` is a coherent stitched layout with the
  trail along the corridors. Known flaw: streaks at the minimap's right edge — the border
  decoration is read as map; widen the ignored border there.

## Stage 1: the live guide (built 25 Sep 2026)

Package layout: `Cartography` (library: `Gray`, `MinimapLayout`, `MinimapReader`, `Stitcher`,
`Navigator`, `Compass`, `Picture`) is shared by the app and by `MapLab`, so what is checked
offline is exactly what runs live.

- **`LiveReader`** captures only the minimap (`sourceRect`, 10 fps, 420v luma) on its own
  queue, which owns the stitcher and navigator. Title bar: 28 pt unless the window is the size
  of a screen (then full screen, none) — logged as "Guide on: … title bar N px, minimap …".
  A legible minimap that fits nowhere for 2.5 s starts a new map (new dungeon or floor).
- **`MinimapLayout`** anchors the minimap to the game picture's top-right, scaled by picture
  height. Measured on one window size only — unverified for full screen, other sizes, and
  GeForce NOW / PS Remote Play. `MinimapReading.isLegible` (arrow found, contrast ≥ 8, floor
  5–90 %) is how a wrong rectangle, a menu or the full map shows up.
- **`Navigator`**: 4-px grid, openings = floor next to never-seen, Dijkstra with a wall
  penalty, bearing aimed 60 px along the route, sticks with its target unless another is
  40 % nearer. Known bias: the ignored bottom band (quest marker) makes southern openings look
  nearer than they are.
- **`Beacon`**: one AVAudioSourceNode, made once. Stereo pan by bearing; pitch 880 / 587 / 330
  Hz for ahead / beside / behind (screen up = ahead); rate 1.6 / 2.6 / 4 ticks a second by
  route length; a second higher tick when the direction of travel (from the trail, last
  0.6 s) is within 25° of the route. All first guesses for the owner's ears.
- **`Guide`** speaks only: Guide on/off, "Lost the map" (after 3 s unreadable; a Purr sound
  when it comes back), "New map", "No unexplored openings in sight" (after 2 s), and the
  answer to "where". Logs one line a second. On stop it saves `guide-map-<time>.png` and
  `…minimap.png` (the last frame as read) to the log folder.
- Keys (Control-Shift-Command): G guide on/off, W where does it lead, N new map.
- `DungeonGuide --guide-test [seconds]` runs the guide on the remembered window with no sound
  or speech, then saves the pictures and quits — the way to check the live path from here.
  First run, 25 Sep: capture and layout correct (title bar 56 px, minimap at 2165,134), but
  the game was not showing a minimap at the time, so every frame was correctly illegible.

## Second recording (25 Sep 2026, 05:59, Forbidden City) — the Map Overlay

- **Hot keys pass through Shadow**: the owner's Control-Shift-Command-G logged `Hot key: guide`
  with Shadow in front.
- The owner walked the options: Gameplay → Map Options → **Map Display: Minimap / Overlay
  Map**, Overlay Map Opacity (set 100), Overlay Map Zoom (set 50), **Use Custom Overlay Map
  Color** (off) + Overlay Map Color. Gameplay → **Navigation Assist**: Auto-Pin New Quests,
  Pathfinder, HUD Compass (+ High Contrast Arrow, Arrow Color), Audio Navigation Assistance —
  all ticked.
- **Pathfinder drew no route in the dungeon** while ticked (objective "Travel to the Tomb of
  Thazbach", no pin). One dungeon, one objective — evidence, not proof.
- **The overlay replaces the minimap.** It draws the whole explored dungeon, centred on the
  character, far larger than the minimap: a translucent grey fill with a thin light outline and
  hatching outside the walls, over the game world. No player icon — the character is the
  position (picture centre, unverified how exactly). The HUD compass is a green arrow by the
  character pointing at the pinned quest.
- **The grey overlay is hard to separate from the world**: the fill is only slightly brighter
  than the dark world behind it, which varies more than that. Next step for the overlay is a
  recording with **Use Custom Overlay Map Color** on and a saturated colour, then a
  hue-based reader. The minimap reader cannot read the overlay at all — with the overlay on,
  the guide now says "Can't see the minimap. The guide needs Map Display set to Minimap."
  when no frame has been legible since it started.

## First live try (25 Sep 2026, 06:22–06:26) — led into a dead end

Log `dungeon-guide-2026-09-25-061501.log`. The owner followed the beacon east then south for
~70 s (heading matched the lead), the southern opening closed at 06:23:39, and then:
"No unexplored openings in sight" four times in two minutes while an opening ~930 px back
flickered in and out; the owner stood still, not knowing what to do. Causes and fixes:

- **The ignored bottom band** (quest marker) meant the guide saw ~115 px south against
  ~140–200 other ways, so southern corridors stayed "unexplored" until their end. Now only a
  disc (radius 12 % of height) round the marker's bright pixels is ignored.
- **Openings favour width**: score = route cost − 1.5 × width in cells (capped at 25).
- **Dead ends are said**: when the opening being led to vanishes while its route was under
  150 px, "Dead end. Next opening <direction>, <how far>." (at most every 8 s); with none,
  "Dead end. No other openings in sight."
- **No flicker**: "No unexplored openings" only after 3 s and at most every 30 s; the beacon
  holds through gaps under 2 s; when an opening reappears after "none" it is announced.
  "How far" gained "a long way back" (≥ 600 px).
- The log went quiet at 06:25:52 with the guide still on — ScreenCaptureKit sends no frames
  while the minimap does not change (owner standing still). Expected, not a hang.

## Vibration, speed, and a hang (25 Sep 2026, after the first live try)

- **Vibration was never built until now** (the owner felt none). `Haptics` opens the
  DualSense non-exclusively with Keel's `DualSenseReader`, routes Keel's `Chime` to the
  controller's 4-channel audio device (Back L/R = actuators), sends audio path 0x30, and
  plays `Chime.hapticPulse`. Keel gained `hapticPulse(_:balance:)` (equal-power, default 0 =
  Muteny's behaviour unchanged). Vocabulary: a tap a second weighted to the route's screen
  side, 170 / 115 / 70 Hz for up / side / down, only while off course; a rising glide on
  coming on course; a rough falling glide for a dead end. Window: "Vibration" switch
  (UserDefaults `vibration`) and "Test the vibration: left, right, then on course". Log shows
  `Controller connected; vibration through audio: true` over USB. The controller was on USB
  and visible to macOS with Shadow running — Shadow does not take it at USB level here.
  **Unverified by hand**, and unverified whether Shadow's game rumble knocks the actuators out
  of audio mode. Muteny must not run alongside (both would drive the controller's audio).
- **The build was debug** and the reader took ~3 s a frame on a large map: in the dead-end
  session the log's one-a-second lines came every ~3 s, so guidance lagged seconds behind.
  `build.sh` now builds release: 8–26 ms a frame. Every `Guide:` log line now starts with the
  frame's milliseconds.
- **Stopping hung** (sample: main thread in `LiveReader.stop` → `queue.sync` behind a backlog
  of slow frames). `stop()` no longer waits on the reader queue: an unfair-lock flag gates the
  frame handler, and `stream` is main-queue only.
- **The canvas re-centres** when a reading comes within 400 px of an edge (`recentres` counts
  it; `Guidance.mapEpoch` carries it so the navigator and the guide never compare targets
  across a move). Tested with `MapLab <mov> <out> 57 1500`: 2 re-centres, all 547 placed.

## Objectives (25 Sep 2026)

The owner: vibration is annoying, "the sound is enough" — it is now **off by default** (the
saved setting was switched off too) and the controller is not opened at all unless it is
switched on. Then: "What is this actually taking me to? I need to be finding the next
objective." Answer given: the nearest unexplored opening, not the objective — because no
objective marker was on screen in any recording ("Travel to the Tomb of Thazbach").

- **`ObjectiveWatcher`** reads the tracker (`TrackerLayout`: under the minimap, right 700 px,
  600 px tall at 1663 picture height) once a second with Vision (`TrackerReader`, accurate,
  en-GB/en-US), believes a reading when two in a row agree, and `Objective.parse` takes the
  first non-difficulty line as the place and the next as the objective; trailing tokens of
  ≤ 2 characters (icons read as "fI", "H", "1") are dropped. `kind` strips counts, so a count
  changing is not announced. Said: "Objective: <text>." on a new kind (and once at start);
  Control-Shift-Command-O / "Say the objective" reads place and objective with counts.
- Measured: every 5 s of both recordings read correctly (menus and map screens gave none);
  20–100 ms a read; the first read loads Vision's model — 27 s cold once, ~1 s since — so
  the app warms it up in the background at launch ("Text recognition ready"). Live on
  Shadow: objective said 1 s after the guide started.
- `WindowGeometry` (in ObjectiveWatcher.swift) is the one place window size, scale, title bar
  and full screen are worked out, and builds the cropped stream configuration for both.

## The swinging beacon (25 Sep 2026, 06:54–06:59, Ghastly Depths)

Log `dungeon-guide-2026-09-25-065359.log`, recording `2026-09-25 06.57.26`. For five minutes the
lead swung between south (~236 px) and north-east (~205 px) every few seconds while the owner
walked back and forth following it, and it said "Dead end" to a player standing still.

- **Root cause: the arrow finder took the largest bright cluster, and a healing-well icon is
  larger than the arrow.** The player's position jumped to the well, and every route was
  planned from there. Found only by drawing the map every 2 s (`MAPLAB_EVERY=2`) — the green
  position dot moved from the player to the well. Now the arrow is the bright cluster
  nearest the minimap's centre, within 25 % of its height. After: the whole recording leads
  north to north-west, the swings being the route bending.
- **Icons left holes** (ignored pixels never seen), and a never-seen patch beside floor is an
  "opening": the well, a door, and the quest marker's disc (always ~130 px south) were all
  permanent false openings. The reader now fills ignored pixels from the first readable
  pixel in each direction (majority, within 45 px), and the navigator also turns any
  never-seen pocket unreachable from the map's outer edge into floor.
- **Staying with an opening** now matches any of its cells within 60 px of the last target
  (its nearest cell slides as the player moves), and switches only if another scores 30
  cells better or is 40 % nearer.
- **"Dead end"** now needs `Guidance.previousClosed` — the opening led to no longer exists —
  with its route under 150 px; a mere change of target no longer counts.
- Minimap icons seen in Forbidden City: healing well (heart over a bowl), a keyhole-arch
  icon (door or exit, unconfirmed). The top bar shows the current area ("Path of Blood",
  "Ghastly Depths") — readable like the tracker, not used yet.

## Muteny chords (25 Sep 2026)

At the owner's request: **Mute + R3 → Control-Shift-Command-G** (guide on/off) and **Mute + R2
→ Control-Shift-Command-W** (where the guide leads), added to both Muteny templates and the
active copy in `~/Library/Application Support/Muteny/bindings.json` (backup:
`bindings.json.before-dungeon-guide`), written while Muteny was not running. Muteny posts
keystrokes at the HID tap, which Carbon hot keys receive; Muteny seizes the pad only while
Mute is held, so the chord never reaches the game. Needs Muteny running with Accessibility
granted (its logs carried no "not yet granted" line). Unverified end to end. Muteny and
Dungeon Guide must not both drive the controller's audio — fine while vibration is off.

## The full run (25 Sep 2026, 07:35–07:57, Forbidden City restarted, 22 min)

Recording `2026-09-25 07.35.32 Shadow PC.mov` (6.7 GB, no marks), log
`dungeon-guide-2026-09-25-070634.log`. Muteny's chords worked (`Hot key: guide` / `where` all
session). What went wrong, and the fixes:

- **No openings for 18 minutes.** After a restart the minimap draws only floor the player
  has been near; unexplored floor is as dark as rock. Reading every dark pixel as "seen, not
  floor" sealed the explored area on all sides. Now dark counts as wall only where it is
  busy (hatching; `Gray.busyness` — mean |p − 3×3 mean| over 7×7, fade/void 0.3–1.5,
  hatching 2.2–3.3, limit 1.8); smooth dark is unseen. A sharp floor edge (a drop of ≥ 10
  across five pixels) is sealed as wall even without hatching — ledges; fog fades 40→25 over
  ~12 px. Floor patches under 150 px are specks and dropped; the navigator trusts floor only
  with two frames' votes; and **no opening within 60 px of the trail** (`Stitcher.visited`,
  stamped per step) — the game reveals floor round the player, so dark there is not floor.
- The overlap test for placing a frame is now 40 % of the frame's own readable pixels
  (the old "a fifth of the lattice" refused every frame once dark stopped counting).
- **Flicker**: a vanished opening is held for 8 plans before it counts as closed, and a
  better one must stay better for 5 plans before the lead moves. On the 22-minute run:
  reversals > 90° went 109 → 47, flip-flops (back within 3 s) 66 → 14.
- **Objective counts** were stripped with the icon junk; only the title is cleaned now
  ("Slay the Enraged Spirits: 3"). A boss name above the tracker ("Blood Magus") shifts the
  lines; an objective equal to the dungeon's name is ignored.
- **Red marks** (`RedMarks`, chroma plane: Cr ≥ 160, Cb ≤ 122; dots measured Cr 195–200,
  Cb ~107, everything else Cr ~129) are the enemies "Slay all enemies" wants and the skull
  on a target. Remembered on the map (`MarkMemory`, forgotten when their spot is in view
  with no mark, or after 90 s). While the objective contains slay/kill/defeat/destroy the
  navigator leads to the nearest reachable mark before any opening; the guide says "Marked
  enemies <direction>." / "No marked enemies left in reach. Exploring." (at most every
  10 s) and "where" says "Marked enemy …". On the run: led to marks on 2718 samples of the
  slay stretch. Live: untested (Shadow showed nothing at test time).
- **A pulsing ring** appeared on the minimap at the moment the objective became "Travel to
  the Ghastly Depths", over the room the owner then entered — possibly the game pinging the
  new objective's place. Seen once; not seen at "Travel to the Tomb" (destination may have
  been off the minimap). Not used yet.

## TJ the Blind Gamer's guide (25 Sep 2026) and what came of it

The owner found "Diablo IV Guide for the Blind" (TJ the Blind Gamer, edited by
BlindsightBlue; a Google Doc). What matters here: pins can be placed in dungeons but the
pinging does not work there (the game only says when you arrive); blind players find
objective icons with the "Sweep Method" (open the map, zoom out, pan with the left stick,
recentre with L3, pin with Square/X); they drop items as sound markers at altars; enemies do
not respawn in cleared areas, so following enemies leads on; layouts follow a formula — start
in one part, objectives in others, boss room usually near a healing well, often behind a door.

The owner's full map (05:59 recording): explored layout only, an area list (Path of Blood,
Ghastly Depths, "Unexplored Areas"), no marker for the undiscovered Tomb. The Sweep Method
works for objectives that have icons, not for undiscovered areas — and placing a pin needs
sight, so pin-following suits a sighted party member, not the owner alone. Reading the full
map ourselves when the owner opens it is the better route; needs recordings of the map with
objective icons.

Built:
- **Mark this spot / take me back** (Control-Shift-Command-S / B; window buttons and menu
  items). `Stitcher.spot` (canvas pixels, shifted on re-centre, cleared on a new map);
  `Navigator.plan(toSpot:)` routes there before marks and openings; "Back at the marked
  spot." within 45 px; "No way back to the spot on the map yet. Exploring." after 3 s with
  no route; B again stops. Tested offline (`MAPLAB_SPOT=10,100`).
- **Onward preference**: openings score 0.3 cell per cell farther from the map's first trail
  point than the player now is (capped ±60). Long run: 44 reversals, 13 flip-flops.
- **Pocket fill limited to icon size** (≤ 100 cells): with smooth dark unseen, the rock
  between two corridors was an enclosed pocket and was being filled as floor.

Not built yet: healing wells as landmarks (needs icon recognition — well vs door vs arrow),
reading the full map.

## Landmarks: healing wells and arches (25 Sep 2026)

The owner: the map's area list cannot be reached with the controller, the game's screen
reader says nothing on the map, and "the dungeon map opens empty until I find something like
a healing well or barrier". The cursor snaps to named things and Square pins them; pinning a
named thing makes the game's beacon work "for the most part". So landmarks are read off the
minimap instead, as they come into view:

- `LandmarkReader` (bright ≥ 100 blobs, arrow and quest-marker ring excluded — the marker sits
  anywhere when its target is in view, and its pin inside its ring read as an arch):
  **healing well** = heart ~26×20 (180–380 px) with a flat rim stroke just below;
  **arch** (keyhole) = an outline ~22×24 with a shape inside. `LandmarkMemory` keeps each
  after 3 sightings; `Stitcher.newLandmarks` are announced once: "Healing well, north-west,
  close." Found on the recordings exactly where they are, and nothing false after the ring fix.
- **The arch looks like an area's entrance**: on the 22-minute run "Travel to the Ghastly
  Depths" became "Slay all enemies in the Ghastly Depths" at 6 min 10 s, as an arch came into
  view beside the owner (with a healing well). One case. While the objective contains
  travel to / enter / go to / reach, `Navigator.plan(toArches:)` leads to the nearest arch not
  within the visited radius of the trail, before any opening: "Leading to an arch <dir>. It
  may be the way in." Offline (`MAPLAB_ARCHES=1`) it led to each arch until walked through.
- Priority of leads: marked spot > red marks (slay) > unvisited arch (travel) > openings.
- Not yet: leading to healing wells for the boss (the guide's formula says the boss room is
  usually near one); other icons (shrines, chests, the gate icon near the entrance).

## Second full run (26 Sep 2026, 04:20–05:11, Forbidden City again)

Recording `2026-09-26 04.25.23 Shadow PC.mov` (46 min, 14 GB — it was still recording when
the owner asked for it to be looked at; quitting the app finished the file, which had no
index until then: "moov atom not found"). Log `dungeon-guide-2026-09-25-220153.log`. The
owner: beeps too quiet; the sound unlike the game's nav assist is distracting; "it tried to
take me out of the area I needed to be in"; "there is exploration and then there is
intention" — at the end they had to retrace their steps to find the Tomb while the guide
looked for unexplored corners.

Findings and changes:
- **Layouts differ between runs** of the same dungeon (yesterday's loop vs today's branches),
  so learned maps are out, and other people's videos cannot show where the Tomb is.
- **Area name** above the minimap ("Path of Blood © | 4:26 AM", picture y 0–70) is read with
  the tracker in one pass (`TrackerLayout.hudRect`, `AreaName.parse` — drops the time, icon
  junk and words in capitals such as "TAB" or a menu's "GAME"/"SHOP"). `Stitcher.setArea`
  stamps each visited cell with its area (`areaAt`) and records a **gateway** (the arch within
  150 px, else the spot) on every change.
- **Intent** (`Objective.intent(currentArea:)`, shared by the app and MapLab): slay → red
  marks; travel → arches; "… in the <area>" while there → `stay` (openings outside the area
  dropped, gateways out walled off); travelling to somewhere not here → `leave` (openings in
  this area +400 cells, and arches count even if walked past, unless already a gateway).
  Said: "Looking outside <area>." when leaving starts. The zone itself is not announced —
  the game's screen reader does that.
- **Beacon rebuilt in the game's style**: a drum hit panned to the route; two slow hits when
  facing away, closer hits and a click when within 60°, a fast double hit and a ping within
  25°. Distance left to "where". Volume: slider "Beacon volume" and Control-Shift-Command-=
  / - (announces the percentage); default 70 %, gain up to 1.0 (was fixed at 0.22).
- **Facing from the minimap arrow** (`MinimapReader.arrowFacing`): the farthest bright pixel
  from the arrow's centre is its *tail*; reversed, it agrees with the direction of travel
  within 45° 78 % of the time (91 % within 90°). Used for the rhythm, falling back to travel.
- **Less talk**: "Lost the map" is no longer said once the minimap has been read (it was the
  inventory, 2½ min); "No marked enemies left" is gone and "Marked enemies" waits for 20 s
  without; a landmark kind is announced at most once a minute.
- **Tracker junk**: a shrine's banner over the tracker read as "TION. → e nearby enemies.".
  Objectives are read only while the minimap is legible, the place must look like a title,
  and a new place needs five agreeing readings.
- **Inventory frames stitched junk into the map**: `busyFraction` (minimap 0.13–0.17,
  inventory 0.38) must be under 0.28 to count as legible.
- **Recorder**: stops itself under 3 GB free and says "Still recording, N minutes" every 15.

**Vision text recognition can fail system-wide** (26 Sep, ~05:40): the accurate recogniser
threw `CRImageReaderError.e5rtError("e5rt_execution_stream_operation_create_precompiled_
compute_operation_with_options call failed", 13)` on every read — in MapLab and in the
older `--text` path alike, with no lines returned; earlier the same day it returned empty
results with no error. Setting the compute device to the CPU (`supportedComputeStageDevices`
has only `.main`) made no difference. The fast recogniser still works and reads the tracker
and area correctly, so `TrackerReader` falls back to `.fast` whenever `.accurate` throws.
`TrackerReader.lastError` holds the last failure; `MAPLAB_DEBUG=1` prints it with each read.
MapLab also gained `MAPLAB_UNTIL=<s>` to stop early. **A restart of the Mac cleared it**
(the owner restarted at ~05:50; the accurate recogniser read the HUD with no error straight
after). If the tracker goes unread for a whole session, that is the fix; the fast fallback
keeps the guide working meanwhile. Dungeon Guide does not reopen itself after a restart.

The busy limit went to 0.34: a real minimap in a tangle of hatched corridors measured
0.28–0.31 (14 min into the 26 Sep run).

## The Tomb was beside the healing well (26 Sep, from the replay)

With the area and objective read cleanly (after the restart), the replay of the 46-minute run
showed: the Tomb of Thazbach's entrance was a plain unexplored corridor running north from the
healing well at the Ghastly Depths' doorway — **no icon of its own** — and the guide, leading
to arches first, pointed at an unused arch 700–1000 px away for the whole 4½ minutes of
"Travel to the Tomb". TJ's rule ("the boss room is usually near a healing well") was exactly
right here. So, while travelling:
- arches are candidates weighed by route like openings, not a priority (they did work for
  "Travel to the Ghastly Depths" — that entrance has an arch, seen ~3 min before entry);
- unexplored ground within 220 px of a known healing well is exempt from the leave penalty
  and scores 150 cells better (`Navigator.wellRadius/wellBonus`, `Guidance.toWell`);
  said: "Leading to unexplored ground by the healing well, <dir>. The way on is usually near one."
- entering an area re-stamps the ground round the player as the new area, so its own
  openings are not dropped as "outside" (30 s of "no opening" on entering the Ghastly Depths).
- mark-leading sticks to the mark being led to (40 px) unless another is 40 % nearer for 5
  plans: 102 of the run's 148 direction swings were in combat, the nearest enemy changing.
- a stray single letter after the objective ("Dungeon cleared v") is dropped.

Replay readings now match the run: Path of Blood → travel (leave) → Ghastly Depths, slay,
stay → travel to the Tomb (leave) → Tomb of Thazbach → Defeat the Resurrected Malice →
Dungeon cleared. "You have reached your destination" appears bottom-left when a pin is reached.

## A dead end worth not repeating: votes that forget (26 Sep, afternoon)

To clear the glow junk, the stitcher was changed to let smooth darkness vote "seen" and to
halve a pixel's votes at 20 (so recent frames win). It worked on the junk but the fog's edge
flickered with it: direction swings on the 22-min run went 29 → 84–88, flip-flops 4 → 49–53,
whatever the thresholds (floor at ⅗, ½, ⅓; wall at ⅓; a fourth "edge" state; holding a
vanished target 2.5 s). Reverted. **The fix that held: the game reveals new floor only within
~150 px of the player**, so `Stitcher.vote` refuses a *first* floor vote for a pixel farther
than `revealRadius` from the arrow (the glow by the well was 130–300 px from the trail); known
floor keeps voting at any distance. 22-min run after: 30 swings / 3 flip-flops, 53 no-opening
samples — the pre-change numbers. Large enclosed dark pockets are now wall (were: left
unknown, making false openings); the gateway the player stands in is not walled.
Measure `reversals` (>90° between consecutive leads) and flip-flops (back within 3 s) on the
22-min run after any change to the reader, the stitcher or the navigator.

## The owner's third run (26 Sep, 06:36–07:18, 42 min) and what changed

- The recorder stopped itself at 3 GB free. The 04.25 recording (14 GB) is to go once its
  replays are done; the owner has to empty the Trash for the space to come back.
- The live log has **no objective change for 40 minutes** while marks were being led to by
  the dozen — the tracker reader had stopped reading (Vision returning nothing, as before).
  `ObjectiveWatcher` now logs after 60 empty reads with the minimap readable and the guide
  says once that the objective text can't be read.
- "40 marked": `MarkMemory.forgetAfter` 90 → 30 s. Enemies move and die; stale marks were
  the "wrong direction".
- The "error bonk" before a spoken answer: the system sound played before every `tell`.
  Answers to a request (where, objective, describe) now play none — VoiceOver is the reply.
- Beacon drum: 330 → 160 Hz knock with a short noise transient instead of the 120 → 60 Hz
  thump ("too much bass").
- **Describe what's on the map** (Control-Shift-Command-D, menu, window): every known
  landmark with direction and distance from the player, the marked spot, marked enemies,
  areas seen — what a sighted player gets from the full map. Not yet triggered by the map
  opening; on request only.
- **Muteny chords** (Desktop template; Media where free): Mute + L2 objective (O), Square
  describe (D), D-pad left mark spot (S), D-pad right take me back (B), L1 quieter (-), R1
  louder (=). Backup `bindings.json.before-dungeon-guide-2`. Written with Muteny quit.

## Where the Tomb corridor stands (26 Sep, end of day)

With the reveal rule on floor and wall and the walked-past zone at 30 px, the corridor stub
north of the well is on the map — but the glow the owner stood in for 30 s at the Ghastly
Depths' doorway (a fight at the objective change) was *within* 150 px, so it is floor with a
busy-textured rim read as wall, and it still seals the corridor's end. Measured and ruled out
as separators for a glow seen through the minimap: **colour** (saturation ≤ 7 on clean
frames, the glow adds 4–7 over a wide area, no clean split), **texture** (bright glow and
bright floor have near-identical busyness distributions), and **time** (votes that forget —
see the dead end above). Unsolved. The `MAPLAB_CANDIDATES_FROM` / `well-N.png` tooling is the
way to look at it again. The 22-min run now measures 33 swings / 5 flip-flops.

The third run's live log had no objective for 40 min because Vision returned nothing; the
replay read every objective. So the "wrong direction at the end" was stale marks and plain
exploring with no intent, not the well rule (which never ran live).

## Fourth run, in progress (26 Sep, 07:53–, Mariner's Refuge / Hollow Caverns)

- A different dungeon reads fine: contrast 15–20, 12 illegible frames of 257. The reader is
  not tied to Forbidden City's colours.
- "Destroy the Seaborn Goddess" was announced every few seconds: the recogniser alternated
  "Seaborn" / "Seabom" ("rn" as "m"). Objectives (and places) within an edit distance of 2
  are now the same, and a kind is never announced again within five minutes.
- **The full dungeon map** (what the owner meant by "describe the map"): `MapScreenWatcher`
  captures the whole window at half size once a second, only while the minimap is
  unreadable, and recognises the map screen by its bottom bar ("Pin Location" / "Center on
  Player"). It reads the area list from the left panel (title-case lines under the dungeon's
  name in capitals, after "WORLD MAP") and the guide says "Map. Areas: … ." followed by the
  landmark description. Once per opening; closing the map re-arms it. **Untested live** —
  built while the owner was recording, so not installed until they stop.

## Fourth run, replayed (26 Sep, 08:25–09:05, 40 min, Mariner's Refuge, 12.5 GB)

- **The tracker's text is over pale mottled stone in this dungeon** and half the readings
  were rubbish ("Goddess: 3 '•*", "' estroy the Seabom Goddéss"). Fix: `Objective.parse` takes
  only the line directly under the dungeon's name, and only if it starts with a verb (the old
  fallback to "the first two lines" made every corrupted frame a verb-less new objective,
  flipping the intent between marks and openings; and the seasonal quest lower down, "Slay
  Harbingers", was read when the dungeon's line failed). Preparing the crop for text
  (`Gray.textEnhanced`) was tried and dropped: the objective line is dimmer grey than the
  title and came out too faint to read. The watcher now captures 420v luma, unprepared.
- **Marks blinked the beacon**: one mark present one frame and gone the next swung the lead
  between it and an opening the other way. A mark that vanishes (or loses its route) is now
  held for `holdPlans` like an opening, and a mark beyond the fog is routed to the nearest
  reachable floor within 60 px (`nearestReachableFloor`). Measured on the first 15 min of
  this run: swings 75 → 8, flip-flops 56 → 4, intent changes 1; the 22-min run unchanged at
  33 / 5. Full run before these fixes: 160 / 107.
- Areas alternated "Hollow Caverns" / "Siren's Chamber" every 10–30 s — adjoining areas the
  owner crossed between, probably; "Hollow Cavims" (3 edits) is now matched to Caverns
  (tolerance max(2, letters/5)).
- Sounds with speech: `tell` plays its sound only when VoiceOver is off (the owner: a chime
  with every VoiceOver line "shouldn't" be there). Same in the recorder.
- Two landmarks in 41 min (an arch at 20 min, a well at 30); "describe" says "No healing
  well or arch seen yet" when there are none. 545 illegible frames of 11578; 6 re-centres.
- The owner confirmed the map they mean is the full one on Start; `MapScreenWatcher`
  covers it (auto on opening) and Mute + Square / Control-Shift-Command-D on request.

## Fifth run (26 Sep, 13:07–13:44, 37 min, Mariner's Refuge again, 11.5 GB) — "okay, but how can it help more"

The owner: "it was… okay, I guess, but we need to work out how it can help more." Twenty-nine
of the thirty-seven minutes went on finding three Seaborn Goddesses in Hollow Caverns, which
never show on the minimap until they are in view — exploration, unavoidably. Then "Travel to
the Siren's Chamber" (3 min, to a place walked through at minute 2), "Defeat the Drowned
Seahag", cleared. 17 presses of "where", 25 "dead end" announcements, 14 announcements of
one objective. What the log and the replay showed, and what changed:

- **One objective, fourteen announcements.** The tracker's text in this dungeon reads as
  "Seabom", "Gpddess", "Seabom'Goildess", "Godde3s", "Destroy'tlie" — three or four edits
  from the truth, past the fixed tolerance of two, and each one keyed its own five-minute
  limit. It is the accurate recogniser doing this (the replay reproduces every reading), not
  the fast fallback. Now `Objective.alike`: the same words within max(2, letters/5) edits are
  the same objective, in `HUDState` and in the guide's announcement rule (which also checks
  every objective said in the last five minutes, not just the last). The spelling kept is
  the one read most often (`HUDState.spellings`), so "say the objective" improves over time.
  Leading rubbish before the verb ("1;; Destroy") is dropped.
- **The count is progress.** "Destroy the Seaborn Goddess: 3" → 2 → 1 is the one thing a
  sighted player watches; the green "1" reads as "l" or "I", accepted as 1. `Objective.count`
  and `HUDState.count` (two readings running); the guide says "2 to go." when it moves after
  the objective was first read, and the objective is announced as "…, 3 to go".
- **The full map was opened twice and the watcher said nothing** (13:08:59 and 13:31:38,
  frames checked). It logged nothing either, so the cause is unproven; the suspects were the
  exact-phrase test ("pin location" / "center on player" — the fast recogniser reads the bar
  as "Centeron Playpr • Pini&Kation O Close") and two recognitions running at once. Now
  `MapScreen.read` (Cartography) matches the bar by a majority of its ten words, the first
  two non-map frames after arming are logged with the bar's text, the watcher logs its size
  on start, and **all text recognition in the process is serialised** on one queue
  (`TrackerReader.serial`, an `NSLock` — a serial `DispatchQueue.sync` deadlocked MapLab at
  170 s, its async main having been resumed on Vision's own queue). Checked offline with `MapLab --screen <png>…` on three map frames
  and two game frames, accurate and fast: all six right. The panel's list stops at the next
  header (the season's quest box, "CONQUER A NIGHTMARE DUNGEON", read as a title by the fast
  recogniser); `MapScreen.looksLikeName` keeps garbage ("Ho]lowCa rns") out of speech.
- **"Healing well east, further off" twice in one description; six wells at 13:41.** The
  map drifts over a long run and the same well was confirmed four times, 90 px apart (the
  final map shows them in a line). `LandmarkMemory.same` is 120 px for wells, 50 for arches,
  and `LiveReader.merged` folds what is left when describing.
- **"Where" says what else there is.** "23 openings in sight" told the owner nothing. Now:
  "Opening north, close. Also unexplored: east, a little way; south-west, further off." —
  `Guidance.others` (every other usable opening: straight-line bearing, route length),
  `Guide.directions` (nearest per compass word, nearest first, four at most). The map
  description ends the same way ("Unexplored: …"), and "Map. Areas: Hollow Caverns, and
  unexplored areas." when the panel says so.
- **"Travel to X" when X has been walked through leads straight to it.** The Siren's Chamber
  was known from minute 2, and at 13:40 the guide still hunted for new ground for three
  minutes with the border 200 px away. `Intent.destination` (the words after the travel
  verb, "the" dropped), `Stitcher.areaMatching` (fuzzy), `Navigator.AreaRule.goTo(area:from:)`:
  the nearest walked cell stamped with the area that a route reaches, else the old leave rule.
  Said as "Leading to Siren's Chamber, north-west, a little way."; "Looking outside …" is now
  said with the first lead, and only when that lead is not straight to the place.
- **The accurate recogniser fails under contention.** With MapLab (with `MAPLAB_INTENT=1`)
  running, a fresh process got `e5rtError(… 13)` on its first recognition; with MapLab paused
  it worked (27 s cold, then ~100 ms). **Do not run MapLab with `MAPLAB_INTENT=1`, or
  `--screen`, while the owner has the guide on** — check the newest log for a "Guide on:"
  without a following "Guide off" first. The failure did not persist this time.
- The dead-end announcements and the exploration itself were not changed: the final replay
  map shows the same rooms walked many times, but a third of the frames were "moving still"
  and the tangles are where the fights were, which the map cannot tell from wandering.
- Steadiness of the old code on this recording: reversals 51, flip-flops 10 in 37 min
  (in line with the 22-min baseline's 33 / 5).
  **The replay with the new code was not finished**: the owner started playing with the guide
  on, and the replay's recognition would have competed with it. Rerun when the guide is off:
  `MAPLAB_INTENT=1 MAPLAB_MARKS=1 MAPLAB_UNTIL=2100 MapLab <13.07.16 mov> out` and look for
  COUNT lines (3 → 2 at ~464 s), one OBJECTIVE line per objective, and "TO AREA" leads after
  1992 s; steadiness should stay near 51 / 10.
- `build.sh` was run at 14:19 while the owner had the guide on (a check that only counted
  "Guide on:" lines and did not stop): they lost the guide for fifteen seconds and turned it
  back on. Gate on the log, not just read it.

## The goal posts: the Undercity (27 Sep 2026)

The owner: "My end goal is being able to do an under city run which is timed, that's the
goal posts." And: a Windows version later ("easier if we're using Steam on Windows as can
use the steam shortcuts instead of Muteny") — the Cartography library ports as arithmetic;
the shell (Windows Graphics Capture, Windows OCR, NVDA controller client, WASAPI, Steam
Input chords) is a rewrite, to be done once the Mac version is dialled in.

**A failed Undercity run, recorded** (27 Sep, 05:59, 3 min 9 s, Temple District, floor 1/3).
What the guide did: said nothing useful for three minutes. What the frames show:

- **The tracker's objective sits four lines under the dungeon's name**: "Kurast Undercity",
  a tier bar (I–IV), "Reward Upgrades (0/4)", "Attunement: Ignite Beacons, Slay Monsters",
  "Time Bonus: Slay Afflicted Monsters", "Reach District Boss before time expires",
  "District Run". The "line directly under the title" rule read nothing. Now `Objective.parse`
  takes the first verb line within five of the title, and its place is the nearest
  title-looking line above it (so a season quest lower down still gets its own title and the
  five-reading guard).
- **A countdown badge** under "FLOOR 1/3" (150 at the start, red by 10, "Time expiration is
  imminent!" at the end) — in the HUD column between the area band and the tracker, unused
  until now. `HUDState.timer`: a bare number of up to three digits there, two readings
  running within 3. The guide says "60 seconds", "30 seconds", "10 seconds" once each on the
  way down, and "Time N" when it jumps up by 15 or more (a beacon lit, a kill).
- **Icons on the minimap**, measured in YUV from the recording: the **objective marker** is a
  bright cyan icon (Y ≈ 188, Cb ≈ 136, Cr ≈ 98) *pinned to the box's edge*, half outside it,
  in the direction of the district boss. Skull-with-blue-drop and brazier-with-blue-flame
  icons (Y ≈ 108, Cb ≈ 157) are attunement monsters and beacons. The healing well's icon is
  neutral (Cb 124, Cr 129) so a bright-cyan test is specific. `ObjectiveMarker.find`
  (Marks.swift); the capture is the box plus `MinimapLayout.margin` (30 px) with the reader
  and the red marks still on the box (`MinimapLayout.outer`, `LiveReader.inset`).
- **Routing towards the marker**: `Navigator.plan(marker:)` adds `markerWeight` × (1 − cos)
  to every opening's score — 200 cells between straight towards and straight away — and the
  switch no longer needs the cost ratio while a marker shows. `Guidance.toMarker`; the guide
  says "Objective marker north. Heading for it." once, "where" adds "towards the objective
  marker north", describe adds "Objective marker north, beyond the map". Held 3 s after it
  vanishes. **Untested live and unproved on the replay at the time of writing.**
- **MapLab lacked the live "fits nowhere for 2.5 s → new map" rule**, so this replay
  stitched the Helltide's round minimap first and refused 806 of 865 legible frames. Added.
- **Replayed with the marker (27 Sep, after the fixes above):** 851 of 901 frames placed
  (the new-map rule), marker found at the box's edge ("west of the player") from 19 s,
  TIMER 150, OBJECTIVE "Reach District Boss before time expires" at 24 s, 786 plans "TO
  MARKER". The first version flickered — from 35 s on, 9 reversals / 8 flip-flops against
  3 / 0 with the marker switched off (`MAPLAB_NOMARKER=1`). Not the marker weight itself:
  `MAPLAB_CANDIDATES_FROM=38 MAPLAB_CANDIDATES_STEP=0` (every plan, with a CHOICE line —
  chosen, best, previous target, pending, missed) showed the lead switching to the west
  opening and then walking back along a chain of neighbours: **"the same opening" was the
  *first* usable opening with any cell within 60 px of the last target**, and where openings
  sit shoulder to shoulder round a small explored patch that is a neighbour. Now the
  *nearest* (`usable.min(by: gap)`). Result: 0 / 0 with the marker, 3 / 0 without, 590 of
  786 marker plans within 90° of the marker. Two other guards were added on the way and
  kept: the marker bearing is a circular mean over the last 10 plans, and under a marker an
  opening must have been about for 5 plans (`settledPlans`, `seenOpenings`) before it may
  be chosen; the switch under a marker needs a 120-cell margin and no cost ratio.
- **Mariner's Refuge (13.07.16) replayed with everything above:** one OBJECTIVE line per
  objective (fourteen before), COUNT 2 at 465 s and COUNT 1 at 1446 s, three wells
  remembered (four before), "Travel to the Siren's Chamber" → 513 plans TO AREA from
  1992 s, route 510 → 193 px by 2100 s. Reversals 55 / flip-flops 13 over 2100 s against
  51 / 10 over 2233 s before — the same within noise. **22-min regression (07.35.32) with
  the nearest-opening fix: reversals 35, flip-flops 6** (baseline 30–33 / 3–5): kept.
- **Second Undercity run, live, with the 06:14 build** (27 Sep, 06:56–07:00, Cave District,
  4 min 1 s, recorded): it worked as designed — "Objective: Reach District Boss before time
  expires", "Objective marker north-west. Heading for it.", "where" answered "Opening north,
  close, towards the objective marker north-west. Also unexplored: …". Three faults seen:
  (1) the objective's place read "Reward Upgrades" (the `(0/4)` was cleaned off before the
  title test; now tested on the raw line); (2) "Looking outside Cave District" was said under
  a marker (now not); (3) **the full map was opened at 06:59:48 and missed** — the bar read
  "OpenPartyFiThkr • CerteT(thPkn)tT • Pan • 7M • O Ck*e", three words of ten. My replays
  were running at the time (fast fallback, most likely), but the test must survive that:
  `MapScreenWatcher.redTabFraction` measures the MAP tab's red box (mean RGB 103/50/49, 64 %
  of the region reddish on map frames, 0 % on game frames) and two bar words plus the red
  tab is the map. Also: only one "Timer:" line live (113) although the replay of the same
  recording reads it every second (150 … 6) — probably the same contention; unproved.
- **Third Undercity run, live** (27 Sep, 07:08–07:13, Ziggurat District, 4 min 53 s,
  recorded): the map description worked on Start ("Map. Areas: Sanctuary. … Objective marker
  west, beyond the map. Unexplored: …" — red tab 0.32 carried it, the bar still garbled),
  the timer spoke ("60 seconds", "Time 113"), the marker moved west → north-west → north as
  they closed in, and the run ended by time expiry with the marker *inside* the minimap,
  north and close — the boss room in view. Two dead ends on the way. Time bought twice
  (+45, +53 s). Replay: 1307 of 1412 placed, 19 reversals / 7 flip-flops in 5 min.
- **The owner, after it:** "am I actually getting any closer? I feel it should be telling
  me directions more verbosely, the trouble is the thump sound is the same for north and
  south too." True: the pan was `sin(bearing)`, so north and south were both dead centre.
  Now the drum's **pitch** is `2^(cos(bearing)/2)` — north ×1.41, east/west ×1, south ×0.71,
  an octave between up and down (`Beacon.Params.pitch`). And the lead's **compass word is
  spoken** whenever it settles on a new one for 1.5 s, at most every 4 s
  (`Guide.directionHold` / `directionGap`). Both untested by ear.
- **A marker inside the minimap is a place, not a direction:** `Navigator.plan(markerPoint:)`
  routes straight to the nearest reachable floor within 20 cells of it (`Guidance.markerInView`;
  "Objective marker on the map, north, close."). The frame sits on the canvas with its arrow
  at the player, so the canvas point is player + (marker − arrow). Inside means 40 px clear
  of the box's edge (the pinned icon sits up to ~20 px inside it, ring and all).
- **Fourth run (07:25, two minutes, "a complete bust", not recorded — under 5 GB free):**
  "West… North-west… West…" every few seconds (the lead on the boundary between two
  compass words; now a new word needs a 60° turn from the one last spoken, 2 s hold, 5 s
  gap), the timer read once and never again, the panel read "Ziggurai Distrirt" (now
  corrected to the tracker's spelling with `Objective.alike`). **The cause of the garbled
  text all morning:** a MapLab replay started at 06:20 had hung (the Mariner's Refuge run
  with the queue-based lock — it stopped at 143 s and never exited) and was still alive at
  07:30; while it lived, a fresh process's accurate recognition failed with
  `e5rtError(… 13)` and only the fast recogniser answered. Killing it did **not** restore
  recognition, nor did quitting the guide app: the failure lives in the root-owned
  `ANECompilerService` (45 min of CPU by then) and `aned`, which only a restart — or
  `sudo killall ANECompilerService aned`, the owner's password — clears. The same as
  26 Sep. **Rules: `pgrep -f release/MapLab` after every replay and kill stragglers; if the
  probe (`scratchpad/run5/ocr2 <png>`, or a fresh `MapLab --screen`) shows e5rt, tell the
  owner to restart before the next run.** The timer, the map bar and the area list all read
  correctly offline with the accurate recogniser; every live garble on 27 Sep was this.
- **Fifth run (07:35, 3 min 13 s, recorded, Cave District, "taking me around in circles"),
  still with the broken recogniser (no restart yet):** the area read as "Cave Distri",
  "District" and "C ve District" — three areas, flipping every few seconds — and the
  destination "District Boss" *contained* the fragment "District", so `areaMatching` made it
  a `goTo` and the guide alternated "Objective marker north. Heading for it." with "Leading
  to District Boss, south-west, close." every five seconds, with a false "Dead end" at each
  switch. Four fixes: `areaMatching` no longer accepts a destination that merely contains
  the area's name; `HUDState` folds a reading that is a piece of a known name into it
  (`contains`, six letters or more); a "Reach …" objective sets no `leave` (it names a thing,
  not a place); and `Navigator.lastKind` clears the target when the lead comes back to
  openings from an area, the marker on the map, a mark or the spot, so that is a change of
  target and never a dead end.
- **Is the Undercity a different methodology? (the owner, 27 Sep.)** Yes in emphasis, and
  the guide tells it apart by what it reads, not by a mode: the tracker's title contains
  "Undercity" → `Intent.timed`. Under it: no arch-as-way-in and no well-side bonus
  (`Navigator.plan(timed:)`, `toWell` false), "Reach …" sets no `leave`, the marker rules
  above do the leading. What is *not* built yet for it is time-awareness (a beacon when the
  timer is short) and the floor change. Ordinary dungeons are unchanged.
- **Replay fidelity:** the recording's H.264 texture put the Cave District's fog at busy
  0.35–0.39, over the 0.34 gate, so the replay dropped 594 of 929 frames the live guide read.
  `MinimapReading.busyLimit` is settable; `MAPLAB_BUSY=0.42` for Undercity replays. The live
  gate stays 0.34 (the inventory).
- **The recogniser recovered by itself** about fifteen minutes after the stuck MapLab was
  killed — a fresh probe read the map bar (27 lines) at 07:45 — so the note above is
  amended: kill stragglers, wait, probe; restart only if it stays broken.
- **Sixth run (07:46, 3 min 35 s, recorded, Temple District, recogniser working):** the
  first one that went somewhere — **the district boss was reached and Floor 2 of 3 begun**;
  time ran out on the second floor. The timer read every second, "60 seconds", "Time 78",
  "30 seconds", "10 seconds" were said, ten direction words in three and a half minutes,
  "Objective marker on the map, south-west, further off" when the boss room came into view.
  Replay: 984 of 1028 placed, 3 reversals / 0 flip-flops. Two things seen: the second
  floor's minimap was stitched onto the first's as if next door (no "new map" — it fitted
  somewhere), and "Descend into the Undercity and end the" was announced as the objective at
  the start (the season quest's block, before the dungeon's own appeared). Now: the floor
  counter ("FLOOR 1/3", the band under the minimap) is read (`HUDState.floor`), a change is
  spoken ("Floor 2 of 3.") and starts a new map; and a line starting with a verb is never a
  place.
- **Seventh run (08:00, 3 min 34 s, recorded, Temple District):** floor 1 cleared in 2:27;
  floor 2 began with 51 s (21 left plus the boss's 30), no beacon lit, out of time after a
  minute spent fighting with the marker north-east and the lead steady north. "Floor 1 of 3"
  / "Floor 2 of 3" spoken, the second floor a new map. One false "Time 213" during the floor
  change (a jump up now needs three readings). Replay 963 of 1019 placed, 13 / 5 over all,
  0 / 0 on floor 2.
- **Time-awareness, built from it:** `BlueIcons.find` (Marks.swift, with `ObjectiveMarker`
  now on the shared `IconFinder`) finds the dim blue brazier and attunement icons (Y < 150,
  Cb ≥ 150, Cr ≤ 115). In a timed run with `timeLeft` ≤ 40 s (`LiveReader.shortTime`, fed by
  the guide from the timer), the nearest blue icon stands in for the marker — pinned to the
  edge it is a bearing, inside the box a place — unless the objective marker is itself on the
  minimap (the boss room in reach beats a beacon; floor 1 of the 08:00 run was won with 40 s
  left that way) — and the guide says "Short on time. Beacon
  north-west." once, "where" says "Beacon north-west, close. Time is short." MapLab prints
  BEACON lines. **Unheard live.** The cost: the blue icons are also read from the margin, and
  the "Temple District" text over it is neutral, so no false blue there.
- **Eighth run (08:15, 6 min 8 s, recorded, Cave District): floor 1 in 1:38 with 209 s
  banked; floor 2 lost 40 px from the boss room's door.** The boss room came onto the
  minimap at 280 s (78 px) and the player stood 40–60 px from it from 283 s to the end at
  325 s, "moving still", with 2–6 red marks round them: a "Grand Spirit Beacon" event (its
  own tracker block, "Slay 5 Wrathful Spirits: 0/5") spawned spirits at the door and the
  fight ate the last forty seconds. Steadiness with the marker and no forced marks: floor 1
  3 / 2, floor 2 4 / 2 (a replay with `MAPLAB_MARKS=1` shows 27 / 21 on floor 1 — that flag
  forces mark-leading the live guide never does in the Undercity; do not use it for Undercity
  replays). "Objective marker on the map" was said three times in five seconds (in-view
  flicker; `Guide.markerGap` 20 s now covers both marker announcements). Added: with the
  boss room on the map and ≤ 30 s left, "Objective north-east, close. 20 seconds." every
  ten seconds, so a fight can be broken off for the door.
- **Ninth and tenth runs (08:39, one recording, 7 min 51 s): floor 1 in 1:55 and 1:46,
  arriving on floor 2 with 71 and 79 s; both floor 2s lost in 1:28.** The owner: "This is
  impossible, I just can't seem to get through fast enough, am I doing something wrong?"
  Measured: standing still 61 % / 34 % of floor 1, 53–63 % of floor 2 (fights). And the
  time economy: **the 08:15 run's floor 2 began with 209 s because at 86–89 s a pack of
  Undead Stygian Dolls died in a burst of "+5 seconds" each, 112 → 194** ("Time Bonus:
  Slay Afflicted Monsters"). The afflicted packs are marked on the minimap with small
  **orange** icons the guide had never looked for; beacons (brazier, white flame) and
  attunement (skull, blue drop) are the blue icons. Blue icons were inside the minimap box
  for fifteen seconds of floor 1 with the marker 300 px off and the guide led past them.
  Changed: in a timed run a blue icon *inside the box* is a target at any time (a pinned one
  still only under 40 s), said as "Beacon north-east, close. Light it for time."; replay of
  floor 1 shows the lead going to them at 20–30 s and 75–90 s.
- **Orange icons, measured and built:** Y ≈ 110, Cb ≈ 103, Cr ≈ 149 — `OrangeIcons.find`
  (Cr 140–159, Cb ≤ 115, Y 80–210; the red marks sit at Cr ≥ 160, the floor at ≈ 130).
  Hourglasses, one per afflicted monster, so a pack is 5–36 blobs. In a timed run the
  **time targets** are the blue and orange icons together: inside the box at any time,
  anywhere under 40 s, never over a marker on the map; the nearest to the player, **held**
  while any icon is within 50 canvas px of the last one (`LiveReader.lastTimeTarget`,
  `timeTargetHold`) — without the hold, the nearest hourglass of a moving pack changed every
  second (floor 1 of the 08:15 run: 10 / 4 against 3 / 2). Said as "Afflicted pack north,
  close. Kill it for time." / "Beacon …, Light it for time."; `Snapshot.timeTarget` carries
  the name. MapLab: `TIME TARGET` lines. **Unheard live.**
- **A replay can hang at its first recognition** (27 Sep, 08:56: ten minutes, no output,
  process sleeping, started seconds after `build.sh` relaunched the app — two processes
  compiling the same Vision model at once is the suspicion; killed, the same command ran
  fine twice). Run every replay under a watchdog: `perl -e 'alarm 400; exec @ARGV' MapLab …`,
  and not within a minute of a relaunch.
- **Eleventh run (09:16, 7 min 34 s, Temple District, on Normal): Floor 3 of 3 reached**,
  with 35 s; floor 1 took 4:44 (222 s banked by 20 s in, then spent), floor 2 1:52, floor 3
  47 s. The owner: "was easier on normal, but I think I was getting stuck on places … couldn't
  hear footsteps." Still 47 % / 66 % / 48 %, longest still 13 / 24 / 5 s. **The first time
  targets misbehaved:** 44 reversals / 27 flip-flops on floor 1 — a lit beacon keeps its icon,
  so the guide led to the one at the player's feet (targets at the box centre for a minute),
  and a beacon 60 px away across a wall had a 424-px route ("TO MARKER IN VIEW 424 px").
  Fixes in `LiveReader`: a time target the player reaches (35 px) is **done** and nothing
  within 120 px of it counts again for the floor; an afflicted pack is a target only within
  120 px (they come to you) unless the clock is short; and a time target whose route is
  over 250 px or 2.5× the straight distance is dropped and the plan redone for the
  objective. MapLab mirrors the first two (not the replan).
- **"Blocked."** Still (no heading) for 2.5 s while facing within 60° of the route and no
  red marks about → "Blocked. Try around to the left/right." (`Stitcher.sidestep`: the side
  with more floor 25–60 px out, perpendicular to the facing; "Step back and try again" when
  neither has any), repeated every 6 s while it lasts. **Unheard live.**
- **Twelfth run (09:36, 7 min 12 s, Temple District, Normal): floor 1 in 1:23 (the best),
  floor 2 in 2:12, floor 3 reached with 55 s and survived three minutes on +3/+5 s kills;
  out of time with the marker still pinned north-east.** Still 58 % / 60 % / 71 %. Lead
  steady: 8 / 4, 9 / 4, 5 / 2. Seven "Blocked" call-outs, the first 1.5 s into floor 1 at the
  entrance (now none in a map's first 6 s, floor changes included). The owner: "I don't
  understand the go left a bit, do you mean left for me or for my character?" — it was the
  character's left; **`Stitcher.sidestep` now returns a bearing and the guide says "Blocked.
  Try north-west."**, the same compass as everything else. One time target lead on floor 1,
  two afflicted packs called on floor 3 ("Time 65", "Time 41").
- **Three runs in one recording (09:53, 15 min 32 s): "it's getting worse, the sound
  beacon should be guiding me through stuff, not running me into walls."** 16 "Blocked"
  call-outs, 50 reversals / 21 flip-flops over the recording, 1773 plans routing straight at
  a marker on the map. The frame at 10:06:32 (Ziggurat District) is the case: the boss
  portal 140 px west across water; `nearestReachableFloor(within: 20)` found "reachable floor"
  80 px from it on the near side, the route to that ran south along a walkway rail that
  the minimap paints like floor, and the guide said "Blocked. Try south-east" twice while
  the lead stayed "south 207 px". Three fixes: (1) a marker on the map is routed to directly
  only with reachable floor within **4 cells** of it; otherwise the openings are scored by
  their distance to the marker's point (½ cell per cell) — the unexplored opening nearest
  the boss room is its door; (2) **the map learns walls from being blocked**:
  `Stitcher.markBlocked(facing:)` votes 12–40 px ahead of the player wall, hard, on every
  "Blocked" call-out, so the route stops running at it; (3) "Blocked" needs the facing to
  have held within 25° since the standstill began — the 10:04:14 one fired in a fight with
  the character spinning. Also in the Ziggurat District the walkways' rails and the water
  read as floor on the minimap; nothing yet tells them apart except being blocked.
- **10:33 run (2 min 49 s, Ziggurat District, floor 1 not cleared): "even worse. I don't
  think it was taking me in the right direction at all."** Measured against the marker
  (MapLab now prints `OBJECTIVE MARKER` before any time-target substitution — the old
  `MARKER at` line was the substitute): the lead was within 60° of the marker's bearing on
  319 of 800 plans. Two causes. (1) **Time targets pulled it off**: with under 40 s the
  pinned-icon rule sent the lead to beacons in every direction (east, south-east, north-east
  in turn), and an afflicted pack 200 px round was taken at 49–57 s. Time targets are now
  **inside the box and within 100 px only**, no pinned-icon rule at all, detour cap 180 px /
  1.8×; eight target plans in the run instead of 81. (2) **Standing at the fog's edge pushing
  towards the marker, the new ground is within the walked radius and is no opening**, so the
  nearest opening was north while the marker was west (49–65 s, and again 82–98 s where a
  wall really did stand west — frame-004). Now **`beeline`**: with no opening within 50° of
  the marker's bearing and no wall cell within 60 px that way, the lead is the marker's own
  bearing ("Straight on west, towards the objective marker"); a wall ahead falls back to the
  openings, and "Blocked" paints one. Agreement 319 → 421 of 800; steadiness 16 / 6 in the
  run. What remains is geometry: the boss west behind a wall with the way round unknown is
  a case where the right lead points away from the marker, and the words now say which it
  is ("Straight on …" against "Opening north, towards the objective marker west").
- **The owner's requirement, stated (27 Sep):** "the audio beacon should be guiding me
  around objects, the same as it does for the above-world built-in version … it needs to
  do it on the micro scale to get to the macro destination." The overworld beacon walks
  the game's navigation mesh; the minimap draws rooms and walls, not crates, rails or
  pillars, so the micro scale has to come from feedback. **Steering** (`Guide`): stopped
  for 0.7 s while pushing along the route (no heading, facing within 60°, no red marks, not
  the first 6 s of a map), the beacon itself swings 50° to one side — the side the map's
  `sidestep` names, else the last side tried — and stays there until the player has been
  moving for 0.6 s; still stopped after 1.5 s it swaps sides. The beacon plays the "near"
  rhythm while steering. The spoken "Blocked. Try …" and the wall painting stay at 2.5 s.
  Live only; nothing in MapLab can exercise it. If it feels like it hunts, the numbers to
  turn are `steerAfter`, `steerSwap`, `steerAngle`.
- Not done yet: the floor counter's total is not used for pacing; the event blocks ("Grand
  Spirit Beacon") are not read; nothing yet says "this fight is not worth it" when the
  player stands in a non-afflicted fight with the clock short; and the real answer to the
  micro scale — obstacles read from the main view, or a Windows build with more to read —
  is open. (the icons are
  detectable — dim blue); the floor counter ("FLOOR 1/3"); what the marker looks like when
  the boss is *inside* the map (never seen yet).

## The regression of 27 Sep, found by the table

The owner, after 11:05: "It seems to be getting worse. I was getting better earlier … what
change have we made that has broken things?" Every run of the day, from the logs (floor
counter and "Purge" as the end):

    start     floor1   floor2   floor3   reached
    08:01:01  2:19     0:57     -        floor 2
    08:15:38  1:38     3:38     -        floor 2
    08:39:27  1:55     1:28     -        floor 2
    08:43:33  1:46     1:28     -        floor 2
    09:16:19  4:45     1:53     0:46     floor 3
    09:36:31  1:23     2:12     3:00     floor 3
    09:53:43  1:48     3:33     1:29     floor 3
    10:01:37  1:01     2:20     -        floor 2
    10:05:39  2:31     0:50     -        floor 2
    10:33:29  2:40     -        -        floor 1
    10:51:18  2:43     -        -        floor 1
    11:00:57  2:37     0:54     -        floor 2
    11:05:09  2:42     -        -        floor 1

Good from 09:16 to 10:05 (the 09:30 and 09:45 builds: time targets with done/detour rules,
the spoken "Blocked", compass sidestep). Broken from 10:33 on — the **10:15 build**: (1) a
marker on the map routed to only with floor within 4 cells, else the openings biased by
distance to the marker's point; (2) walls painted on every "Blocked"; (3) steady-facing.
Then 10:45 (beeline, time targets cut back) and 10:55 (steering) on top. Replaying the 09:36
recording under the 10:45 code: floor 3 went 5 / 2 → 10 / 6, so the routing itself had
regressed, not only the live-only painting. **Do not judge a change by the run that prompted
it; replay the good recordings (09:36, 09:53) before installing.** Reverted at 11:15: the
in-view rule is `within: 20` again with a detour guard (route ≤ max(120, 2.5× straight)),
the score bias is the angle weight only, `beelineEnabled = false` (kept, flagged), wall
painting off, the spoken "Blocked" off (`speakBlocked`); steering kept at 1.0 s / 40° with a
steady-facing condition — the one live-only thing still in, and the first suspect if the next
run is not back to the 09:36 shape. **Second step, 11:20:** with the detour guard and the
10:45 time-target cut-back still in, the 09:36 recording replayed at 9 / 5, 9 / 3, 10 / 6 —
still not the 8 / 4, 9 / 4, 5 / 2 of the day. Both reverted too (in-view `within: 20` with no
guard; time targets inside the box at any time, all of them under 40 s, packs within 120 px,
detour 2.5× / 250). Replay: **8 / 4, 9 / 4, 5 / 2 exactly.** That is the installed state.
Lesson kept: "it only removes behaviour" was wrong — fewer time targets changed which
opening won on floor 3.

## The goal, reached (27 Sep 2026, 11:33–11:41)

Recording 11.21.37 (20 min 41 s) holds three runs on the reverted build. Run A: floor 1 in
1:44, floor 2 in 3:18, floor 3 out of time with the marker in view. Run B: floor 1 in 2:01,
floor 2 lost. **Run C, 11:33:47: floor 1 in 1:35, floor 2 in 2:31 (two afflicted packs and a
beacon called and taken — 273 → 350 s), floor 3 in about 1:50 with 333 s banked, "Objective
marker on the map, north, a little way" at 11:39:37, and at 11:40:47 the frame shows "FLOOR
4/3", "LONGTOOTH THE WRETCHED" with its health bar, the timer frozen at 222 in The Nest; at
11:41:07 the boss is dead, loot on the ground, the tracker reads "District Run" complete; at
11:41:27 the map is open with the Stash beside the player.** The Kurast Undercity, cleared,
with 222 seconds to spare — the goal posts the owner set two days earlier ("being able to do
an under city run which is timed"). On Normal, with the 09:30-state routing restored, the
cane steering (1.0 s / 40°) live for the first time: 13 steering events across the three
runs, none spoken. Standing still 44–66 % per floor; the time economy carried it.
Replay steadiness of the recording, floor by floor: run A 7 / 3, 5 / 1, 7 / 1; run B 7 / 2,
7 / 2; run C 3 / 0, 7 / 4, then 13 / 5 over a stretch that includes the boss arena and the
map screen afterwards. In line with the 09:36 numbers; no stray MapLab left running.

What this says about the afternoon: the build that reached floor 3 three times before 10:05
was the right one; the changes made between 10:15 and 10:55 in response to single runs cost
an hour of regression, and the fix was the table and the replay, not another idea.

## On GitHub, as Sanctuary Sonar (27 Sep 2026, midday)

The owner named it **Sanctuary Sonar** and asked for it on GitHub for the blind Diablo IV
community, with a Windows version to follow. Repository: **github.com/Doll-Eye/SanctuarySonar**
(MIT). The folder, the app bundle, the log folder and the Muteny chord labels still say
"Dungeon Guide"; rename them together, in one change, when the Windows port names its
executable — not piecemeal. `Package.swift` now fetches **Keel from GitHub**
(`Doll-Eye/Keel`, public, MIT, branch main) instead of `../Keel`, so a clone builds on its
own (proved with a fresh clone before the repository went public). **Consequence: a change
to Keel takes effect here only once it is committed and pushed** — `swift build` resolves
the branch, and `swift package update` picks up a new commit. Muteny still uses the local
path. The README is written for players, not developers; keep it that way and keep the
findings in this file.

Windows plan (agreed): port `Cartography` and MapLab to C# first and require the same
replay numbers on the same recordings (8 / 4, 9 / 4, 5 / 2 on 09:36); then the shell —
Windows Graphics Capture, Windows.Media.Ocr, NVDA controller client, WASAPI, Steam Input
chords. Build on the Mac with the .NET SDK (`brew install dotnet`), copy over the Shadow
PC's C: drive, which mounts at `/Volumes/C$` over Tailscale (NVDA, JAWS, Steam, .NET 8 and
10 runtimes are already on it), and read the log back the same way.

## Nightmare and ordinary dungeons re-checked (27 Sep, after the Undercity)

The owner asked whether it still works in nightmare dungeons. Replaying the 22-min Forbidden
City run under the installed build: 35 / 7 against the 33 / 5 baseline, and **484 plans "to
marker"** — a cyan **chest icon at the dungeon entrance** (visible on the minimap in the first
minute and the last) read as the objective marker and weighed the openings towards it. Two
attempts that did not work and were reverted: a cap on the blob's size and ignoring cyan
within 50 px of the player (neither removed it, and together they took the 09:36 Undercity
run's floor 2 from 9 / 4 to 15 / 9). What worked: **the marker is Undercity-only**
(`LiveReader`: `if !wantNow.timed { marker = nil }`; the same in MapLab) — no ordinary
dungeon has yet shown a real cyan objective marker. Result: the 22-min run **30 / 6, marker
plans 0**; the 09:36 run **8 / 4, 9 / 4, 5 / 2**. Installed. Everything else that applies to
ordinary dungeons — fuzzy objective identity, counts, the map description, north/south
pitch, spoken directions, steering — replays at or better than baseline.

## The C# port (started 27 Sep 2026, afternoon) — `CSharp/`

`CSharp/SanctuarySonar.slnx`: `Cartography` (net10.0 class library, the port of
`Sources/Cartography`) and `MapLab` (console, the port of `Sources/MapLab/main.swift`). The
Windows shell comes after, as a third project. Rules of the port:

- **Line for line, same names, same casing.** Types and members keep their Swift names
  including lower camel case (`stitcher.add`, `reading.isLegible`, `navigator.plan`), so the
  two code bases can be read side by side and a fix can be carried across by name.
  `Pt` stands in for CGPoint (fields `x`, `y`), `Rect` for the `(x, y, width, height)` tuples,
  `RectD` for CGRect (`CSharp/Cartography/Core.cs`). Image output (PNG pictures) is not
  ported. `TrackerReader` (Vision OCR) is not ported: text lines come from the shell.
- **The test is a diff.** The C# MapLab prints the same lines as the Swift one, character
  for character (Swift `%.0f` rounds half to even, so `F0()` in Program.cs does too). It
  decodes with ffmpeg (`-fps_mode passthrough -pix_fmt nv12`, NV12 being the same plane
  layout as Apple's 420v: luma, then Cb/Cr interleaved at half size), takes frame times from
  `ffprobe -show_entries packet=pts_time` sorted (the recordings have duplicate timestamps,
  hence the muxer's dts warnings — harmless), crops to the HUD column (x from an even
  `width − 760`) and applies the Swift sampling rule (a frame at or after `nextSample`, then
  `nextSample = time + 0.2`). The HUD text comes from a log the Swift MapLab writes with
  `MAPLAB_HUDLOG=<file>` ("time\ty\ttext" per line, "time\t-" for none) and the C# reads with
  `MAPLAB_HUD=<file>`, keyed by the same "%.2f" time. Golden outputs for the two reference
  recordings live in the session scratchpad (`golden/0936.txt`, `golden/0735.txt`, and the
  `.hud` files); regenerate them with the Swift tool when it changes.
- **Result, 27 Sep 2026, evening: the C# MapLab prints exactly the Swift MapLab's output on
  both reference recordings — 3312 of 3312 lines on 09:36 (8 / 4, 9 / 4, 5 / 2) and 8382 of
  8382 on the 22-min Forbidden City run (30 / 6), zero differences.** One trap on the way:
  `ffprobe -show_entries packet=pts_time` rounds to six decimals, so frames on the 1/600 s
  grid fell a hair short of `nextSample` and the two tools sampled different frames from
  34.51 s on; `packet=pts` (integer ticks) with `stream=time_base` gives the same doubles as
  CMTime's `value / timescale`. Four translators ported the files in parallel under a
  same-names rule; the build needed two fixes (a shadowed local, an alias for the nested
  `AreaRule`). Run with `DOTNET_ROOT=$(brew --prefix dotnet)/libexec dotnet MapLab.dll …`
  — the apphost cannot find Homebrew's runtime on its own. Speed: the 22-min recording in
  3 min 53 s, the 7-min one in 66 s.
- Not ported (by design): PNG pictures, Vision OCR, and the pictures' `Picture` code. The
  Windows shell must supply text lines from Windows.Media.Ocr in the same `(text, y)` /
  `(text, x, y)` shape, and everything else is shared.
- Build: `cd CSharp && dotnet build -c Release`; run: `dotnet run --project MapLab -c Release
  -- <mov> <out>` with the same environment variables as the Swift tool plus `MAPLAB_HUD`.
  .NET SDK 10.0.401 via Homebrew (`brew install dotnet`); the Shadow PC has the 10.0.8
  runtime, so a framework-dependent `dotnet publish -r win-x64` will run there.

## The Windows shell (27 Sep 2026, evening) — `CSharp/SanctuarySonar/`

Built on the Mac (`net10.0-windows10.0.19041.0`, `EnableWindowsTargeting`, win-x64,
framework-dependent), published with `dotnet publish -c Release -r win-x64 --self-contained
false -o CSharp/publish/win-x64` and copied to the Shadow PC at
`C:\Users\Shadow\SanctuarySonar\` over the mount (`/Volumes/C$`). **Untested on Windows**
at the time of writing; the first run needs the owner at the Shadow.

- `Contracts.cs`: `ISpeech`, `IBeacon`/`BeaconAccuracy`, `Frame` (NV12 of the minimap's outer
  rectangle with `box`/`inset`/`time`), `Log`, `Settings`. One worker thread runs capture →
  `LiveReader.process` → `Guide`; hot keys queue onto it; only the audio thread is separate.
- Ports, same names as Swift: `Guide.cs`, `LiveReader.cs` (`process(Frame)` instead of a
  capture stream; `Snapshot` nested), `ObjectiveWatcher.cs` (`update(lines)`),
  `MapScreenWatcher.cs` (`check(lines, redTab)`), `BeaconSynth.cs` (the render block; `new
  Params()` not `default`). Haptics dropped; sounds for sighted players not done.
- Windows layer: `Capture.cs` (BitBlt of the client area from the screen DC — windowed and
  borderless fullscreen only; BGRA → NV12 BT.601 video range; `redTabFraction`;
  `grayBgra`), `Ocr.cs` (`HudTextReader` on Windows.Media.Ocr, en-GB/en-US/user profile,
  serialised), `Speech.cs` (NVDA controller client DLL beside the exe if present and NVDA
  running, else SAPI), `BeaconOut.cs` (NAudio WasapiOut shared 40 ms, 48 kHz stereo float),
  `HotKeys.cs` (Control-Shift-Alt + G W D O S B N = - Q on a message-loop thread),
  `Program.cs` (`--window "Diablo IV"`, `--list`; log at `%LOCALAPPDATA%\SanctuarySonar\logs`).
- Known gaps: exclusive fullscreen (needs Windows Graphics Capture); the NVDA controller DLL
  is not on the Shadow (`nvdaControllerClient64.dll` from NV Access's controller client
  package — the owner to fetch or approve a download); OCR quality of Windows' engine on
  the game's serif font is unmeasured — if the tracker reads badly, the HUD-log route lets a
  Windows recording be compared line by line against the Swift reader; no recorder yet.
- The Mac app still says "Dungeon Guide"; the Windows one says Sanctuary Sonar. Rename the
  Mac side in one change when the two are next touched together.

## First runs on Windows (27 Sep 2026, 13:15 and 13:54, the Shadow PC, Diablo IV 2560×1609 windowed)

- **Works:** NVDA speech through the controller client, hot keys, BitBlt capture of the
  client area, the minimap reader (leads, "New map", directions spoken, the Undercity marker,
  beacons and afflicted packs called), and **Windows.Media.Ocr reads the tracker cleanly** —
  "Kurast Undercity", "Reach District Boss before time expires", the timer every second,
  "FLOOR 1/3", "Ziggurat District" — better than Vision's fast recogniser and on a par with
  its accurate one. Pictures the shell wrote (`frame-*.bmp`, `hud-*.bmp` in the log folder)
  confirm the capture is the minimap box plus margin and the HUD column, correctly placed.
- **Broke, fixed:** (1) the OCR's `IMemoryBufferByteAccess` cast fails under CsWinRT
  ("Invalid cast from WinRT.IInspectable") — `SoftwareBitmap.CreateCopyFromBuffer` instead;
  (2) **the busy gate**: natively rendered frames show the fog's texture crisply, busy
  0.38–0.40 against the 0.34 limit tuned on Shadow recordings, so after the first minutes
  every frame was "illegible" and the guide fell silent ("it kinda worked, then it stopped
  working"). The shell now sets `MinimapReading.busyLimit = 0.45` (`--busy N` to override)
  and logs an "Illegible: …" line every 5 s while frames are rejected, so the inventory's
  busy value can be learned and the gate set properly.
- The very first frames after "guide on" are black (contrast 0.0) — the loading screen.
- Delivering: copying over a running exe fails with "Resource temporarily unavailable";
  the owner quits with Control-Shift-Alt-Q first, or the build is staged in `next\`.

## Diablo IV local, through CrossOver (29 Sep 2026)

The owner got Diablo IV running on the MacBook Air itself in a CrossOver bottle (Steam in the
"Steam" bottle, launched from the CrossOver GUI). No stream any more: the guide reads the game's
own window. What that changed in the guide:

- **A Wine game has no bundle identifier and is named by its executable**, "Diablo IV.exe".
  `GameApps` (Windows.swift) treats any `.exe` process that is not a known bottle helper
  (steam.exe, steamwebhelper.exe, explorer.exe …) as a game; it is listed first, shown without
  the ".exe", and its window is accepted **with or without a title** when it is at least
  800×600 points — the game window lost its title for three refreshes in a row on the first
  morning and vanished from the list, which is why the owner "couldn't see it to select". The
  same process also owns untitled 1470×33 and 500×500 helper windows, which the size rule drops.
- **`pick` prefers a running game over everything**, then the remembered choice (matched by
  app name when there is no bundle id), then the stream apps — and **auto-picks are no longer
  remembered** (`RecorderModel.autoPicking`): the auto-pick after every refresh used to be
  stored as the owner's choice, which is how Muteny became the remembered game.
- Measured after the change: the game listed first and picked at launch
  ("Picked window: Diablo IV, 2940 by 1912 []"). The window is 1470×956 points = the whole
  screen at 2×, so the minimap layout takes the full-screen path.
- **Second morning fix (07:00): the game window is at layer 26 while the game is in front.**
  Wine lifts a full-screen window above the menu bar for exactly as long as its app is
  active — measured 30 s straight with `CGWindowListCopyWindowInfo` — and the list kept only
  layer 0, so every refresh made *while playing* (the chord starts one) lost the game and
  fell back to Muteny. A game window is now accepted at any layer up to 30.
- **"New map" every three seconds, on the right window.** Every placement after a reset
  scored −2: `agreement` counted the whole reading as readable but `vote` records only the
  disc within `revealRadius` (150 px) of the player, so with the guide turned on mid-floor
  and the explored patch reaching 300 px, under 40 % of the readable pixels could ever be on
  the map. `agreement` now counts readable only within the same disc (Swift and C#). A
  whole-frame first vote was tried and reverted in the same hour: it gives every pixel of the
  first frame a seen vote, after which far speckle votes wall at any distance — the Tomb
  problem back. The guide line now carries `floor %` and `busy` per frame, and the
  fits-nowhere line says what the reading looked like.
- **First full run on CrossOver (07:09, 2 min 50 s, Temple District, recorded):** the guide
  read the native minimap from start to finish — floor 13–59 %, **busy 0.15–0.33** (the live
  gate is 0.34; the streamed minimap read 0.13–0.17, so native sits much closer to the gate
  and the inventory's native value is still unknown — have the owner open the inventory for
  five seconds with the guide on), contrast 15–18, 5 illegible frames in 170 s, no resets,
  and the stitched map (`guide-map-…-071210.png`) is one coherent shape with a clean
  outline. Floor 1 was not cleared in the 150 s: the lead sat north / north-east for two
  minutes while the trail looped round the southern rooms. **The recording is the new
  reference** (`~/Movies/Dungeon Guide/2026-09-29 07.09.16 Diablo IV.mov`, 1.0 GB — do not
  delete it); its replay lives in `Reference/2026-09-29-0709/` (replay.txt, hud.tsv, pictures)
  and `Tools/steady.py` gives the steadiness numbers. MapLab is run with title bar **0** for a
  full-screen recording; its first lead matched the live log to the pixel.
- **Timer misread "173" at 26 s left** (07:11:30, three consistent reads while the area
  banner said Egg Cluster), announced as "Time 173", after which "60 seconds" was said at 14 s.
  A jump of more than a minute now needs six consistent reads (Swift and C#) — and that
  alone would not have caught it: the replay's HUD log shows the recogniser read the floor
  counter "1/3" as **"173" for eight seconds straight** while an Afflicted monster's
  nameplate ("Egg Cluster / Afflicted") sat above the tracker, and the timer parser took the
  *first* bare number in the band, which is the floor line, above the real "26". The timer is
  now the **lowest** bare number in the band. The "Egg Cluster" area change was the same
  nameplate read as a place; the area rule needs the same care if it recurs.

- **07:45 run (3 min 27 s, Ziggurat District, recorded): "it didn't find where I was
  supposed to be going" — and it could not have.** All 190 leads were `to opening`; not one
  marker line. `Intent.timed` came from the objective's *place* containing "Undercity", and
  in both CrossOver runs the place read **"Monsters"** (the wrapped line above the objective,
  "…Ignite Beacons, Slay / Monsters"), so the run counted as untimed, `marker = nil`, the
  blue and orange icons were never searched, and the guide explored frontiers ("Dead end"
  five times). The 27 Sep Shadow runs read "Kurast Undercity" as the place by luck of the
  line layout. Now `HUDState.timedRun` is set when **any** tracker line contains
  "undercity" (the header is always there), the watchers mirror it, and
  `intent(currentArea:timed:)` ORs it in — Swift and C#. The `Intent:` log line and MapLab's
  `INTENT` line now end with `timed`. The frame at 60 s shows the cyan marker at the bottom
  edge and a blue flame icon on the native render, so the finders have something to find;
  whether their colour thresholds hold natively is what the replay of this recording says.

- **07:54 run (3 min 48 s, Cave District, recorded): "it kept dropping out."** 148 of 168
  frames illegible: busy 0.45–0.48 on nearly every frame against the live gate of 0.34 — the
  same numbers the Shadow PC read natively on 27 Sep, where the gate went to 0.60. Temple
  District had read 0.15–0.33, so the texture depends on the district. **`busyLimit` is 0.60
  in both cores now** (the app used the core default; the Windows shell already set 0.60).
  The other legibility tests still stand. The inventory's native busyness is still
  unmeasured: ask the owner to open it for five seconds with the guide on and read the
  `illegible` line's busy value.
- **MapLab now leaves beacons out unless `MAPLAB_BEACONS=1`**, as the live switch does; the
  first replay of 07:45 (beacons in) led to a beacon for its first 40 s exactly as the owner
  complained of, and read 17 / 7. The reference replays live in `Reference/2026-09-29-0745`
  and `-0754`, both at busy 0.60 and beacons off.

- **08:07 run (3 min 18 s, Ziggurat District, recorded): "that one got me stuck… significant
  issues now."** The reading was fine (191 legible of 196, timed, marker found and pinned
  north-west all run, 38 marker lines on replay) and the lead was sound: `to opening to
  marker` north-west at 150–220 px for two minutes. The trail on the replay's map pictures
  (`Reference/2026-09-29-0807/map-004…009.png`) is a knot at one pinch between two rooms — the
  owner stood against something for two minutes with the beacon pointing through it, then got
  past at ~150 s with the clock gone. **The steering never engaged, in any run today**: its
  condition was `g.marks == 0`, "no red marks anywhere on the minimap", and the native render
  has a mark on 70–80 % of frames (0709: 149 of 763 frames mark-free; 0745: 195 of 942; 0754:
  221 of 743). So on CrossOver the cane was off. Now: steering is withheld only when the
  nearest mark is within `enemyNear` (60 px) of the player (`Snapshot.nearestMark`, Swift and
  C#), and standing still with a mark that close says **"Enemies close."** after 4 s, at most
  every 20 s — a fight or a body-block in a doorway, which a blind player cannot tell from a
  wall. MapLab's placed line now carries `(nearest N px)` so a stall can be read either way
  from a replay. The replay answered: **no mark within 60 px at any point of the stall** (0 of 378
  frames with marks; nearest 128–193 px), so it was an obstacle, not a fight, and the new rule
  would have steered. The screen at 95 s (`ffmpeg -ss 95` on the recording) shows what it was:
  the character on a **raised stone walkway with a parapet**, a pool with roots below to the
  north-west, and the route pointing north-west straight over the parapet — the minimap draws
  the level change as a thin line and the reader took both levels as one floor. The steering's
  sidestep looks for floor 25–60 px to either side, which along a walkway is along the
  walkway, so it is the right mitigation; a ledge detector tuned on native frames (the
  `sharpDrop` rule was tuned on Shadow's video) is the proper fix and is not written. 124 of
  the stall's 426 frames were "moving still", the rest the owner trying directions — the
  steering only fires on the still ones, so it will feel intermittent until the player stops
  dead when the beacon does not pay.

- **The replay gate could not be run:** `~/Movies/Dungeon Guide` is empty on 29 Sep (the
  recordings are gone — not in the Trash) and the session scratchpad with the golden outputs
  was cleared. These two changes are installed on reasoning and the live log only. The next
  Undercity recording is the new reference; record one before tuning anything else.

- Not yet seen: whether the window id survives a display-mode change in the game (Wine may
  recreate the window). If the guide goes silent after a settings change, re-find the window
  by app name rather than by id.

## The Windows work brought back to the Mac (29 Sep 2026, 07:30)

The owner worked on the Shadow PC on 28 Sep (its clone at `C:\Users\Shadow\SanctuarySonar-src`,
**uncommitted** there — the Windows session's changes are in its working tree only; every file
also shows modified because of CRLF, so diff with `-w --ignore-cr-at-eol`). The shared C# core
was untouched there; the Swift files were untouched. What was ported to the Mac, from its
CLAUDE.md sections "The Windows window", "Two runs on the window build…", "The recorder's
first run…" and "Map points":

- **Guide rules** (Guide.swift): a timer reading older than 6 s counts for nothing
  (`freshTimer`; "Timer: stale, forgotten" in the log); "This fight is not paying. Objective
  east. 35 seconds." when the clock is short, the player is still among red marks and the
  timer has not risen for 15 s, at most every 15 s; time-target announcements at most every
  20 s (`timeTargetGap`, was 8); no dead end in a map's first 6 s; the `Guide:` line ends
  with `to <kind>` (spot / beacon / pack / marker on map / marker beeline / opening to marker /
  mark / area / well / arch / opening) and `, N marks`.
- **Beacons are off by default** (`LiveReader.leadToBeacons`, UserDefaults `leadToBeacons`,
  the window's "Lead to beacons for time" switch): the blue icons leave the time-target list;
  afflicted packs stay. Not replay-tested either way — the 07:09 reference had no time
  targets at all (no TIME TARGET lines), so it cannot tell; the first Undercity run with
  packs about will.
- **Map points** (`MapPoints.swift`: `MapIcons.find`, `Pointer`, `MapPointsController`; keys
  L / K / J and four buttons in the window): a straight port of MapPoints.cs / Pointer.cs /
  Shell.cs. Capture is `Recorder.picture(of:)` (the window itself, so nothing on top of the
  game matters); the tooltip is read with `TrackerReader.placedLinesXY` on a crop above the
  icon; the pointer is moved with a warp plus a posted mouse-moved event and clicked with
  posted CGEvents, which need **Accessibility** — the first J prompts for it and says so.
  Verified offline: `DungeonGuide --map-points <png>…` on five frames pulled with ffmpeg from
  the Windows map recording (`recordings\run-2026-09-28-071226`, 2560×1608) finds 18 / 9 /
  25 / 26 / 36 points in ~150 ms, the zoomed-out frame giving 9 waypoints, 6 capstones,
  3 strongholds, 2 dungeons — the same shape as the Windows numbers; ringed copies confirmed
  by eye. **Untested live on the Mac:** whether Wine's Diablo shows a tooltip for a warped
  pointer and takes the posted click; the log's "Pointer on" / "Tooltip text" / "Click" lines
  answer it.
- Not ported: the Windows Forms window (the Mac has its own), the Windows recorder (the Mac
  had one first), PrintWindow capture (ScreenCaptureKit already reads the window itself).

## Working on the Shadow PC (from 28 Sep 2026)

The owner moved the work onto the Windows machine itself so Sanctuary Sonar is built, run and
debugged where it runs. State at the hand-over:

- **Build here:** `cd CSharp` then `dotnet publish SanctuarySonar -c Release -r win-x64
  --self-contained false -o publish\win-x64`. Needs the .NET 10 SDK and nothing else; the
  Windows SDK projection comes from NuGet. Copy `publish\win-x64\*` over
  `C:\Users\Shadow\SanctuarySonar\` **while the app is not running** (Ctrl-Shift-Alt-Q first;
  a running exe locks its DLLs). Keep `nvdaControllerClient64.dll` there — it is not in the repo
  (LGPL, from NV Access's controller client zip; see `publish/win-x64/README-Windows.txt`).
- **Logs and pictures:** `%LOCALAPPDATA%\SanctuarySonar\logs\` — `sonar-*.log`, plus
  `frame-`/`luma-`/`hud-*.bmp` for the first frames after Guide on. Read the newest log after
  every run; filter out `Timer: `, `Map-screen check`, `HUD text`, `Guide: ` lines to see the
  decisions. Crashes: `%LOCALAPPDATA%\CrashDumps\` and WER under `C:\ProgramData`.
- **Untested since the last change:** the SAPI speech path (SpVoice) — rename the NVDA DLL away
  for one run to exercise it; the 0.60 busy gate on a full floor; the inventory screen's busy
  value natively (the gate exists to reject it — log an `Illegible:` line while it is open).
- **Unexplained:** two Windows system chimes at the start of the 27 Sep 14:07 run. Nothing in
  the toast database, Defender log, WER or NVDA log matched. Ask when they occur.
- **The Swift side stays on the Mac.** The `Sources/` tree and the golden-replay rule (09:36
  recording at 8/4, 9/4, 5/2) need macOS and the recordings in `~/Movies/Dungeon Guide`; the C#
  core is a verified zero-diff port, so a cartography change made here must be mirrored in
  Swift and replayed on the Mac before it is trusted — or the recordings copied over and
  replayed with `CSharp/MapLab` (ffmpeg on PATH).
- **Muteny is irrelevant here:** on Windows the chords are Steam Input (or any macro tool)
  sending Ctrl-Shift-Alt-<key>; Muteny only ever mattered when the guide ran on the Mac.

## Next

1. Hold the line: no change is installed until the 09:36 and 11:21 recordings replay at
   least as well (8 / 4, 9 / 4, 5 / 2 on 09:36's floors).
2. Ask the owner whether the cane steering felt like a lead or a fidget; it is the one
   live-only thing not measured.
3. Then, one at a time with replays: "not worth it" for non-paying fights when the clock is
   short; the event blocks in the tracker; the beeline (flagged off) if a recording shows
   the fog-edge case again; the glow within reach of the player in ordinary dungeons.
4. The Windows version, once the Mac one has held for a week.

Ruled out since: learned dungeons (layouts differ between runs of the same dungeon), and
icon detection is done for red marks, healing wells and arches.


**The SAPI fallback crashed the process (27 Sep 2026, 13:07, the very first Windows run).**
The log had one line — the header — because `new System.Speech.Synthesis.SpeechSynthesizer()`
died with an access violation (read at 0, KERNELBASE via System.Speech → SAPI) before it
could log. Found from `%LOCALAPPDATA%\CrashDumps\SanctuarySonar.exe.*.dmp` and the WER report
under `C:\ProgramData\Microsoft\Windows\WER\ReportArchive`, read with `scratchpad/minidump.py`
(module list, exception record, crude stack from module hits). Loaded in the process at the
time: `NaturalVoiceSAPIAdapter.dll` and Microsoft's CognitiveServices speech libraries — the
adapter many NVDA users install for natural voices; NVDA logged a UIA "Catastrophic failure"
the same second. Speech now uses SAPI's own `SpVoice` late-bound (`SAPI.SpVoice`,
`Speak(text, SVSFlagsAsync)`), which is what NVDA's sapi5 driver drives on the same machine, and
`System.Speech` is gone from the project. Every speech step logs *before* it runs. Untested on
the Shadow since the change: the owner has NVDA running, so his runs never take this path —
rename `nvdaControllerClient64.dll` away for one run to exercise it.

**Windows crash forensics from the Mac:** the mount at `/Volumes/C$` gives the WER archive,
`CrashDumps`, NVDA's own log (`%LOCALAPPDATA%\Temp\nvda.log`, timestamps and tracebacks), the
toast database (`%LOCALAPPDATA%\Microsoft\Windows\Notifications\wpndatabase.db`, sqlite) and
Defender's `MPLog`. The "two Windows chimes at the start" the owner heard on 27 Sep matched
nothing in any of them — no toast, no detection, no crash at that time, no bell character in
the log — so their source is unknown; the candidate to test is the modifier: on a Mac keyboard
through Shadow, Cmd is the Windows key, and Ctrl-Shift-Win-B restarts the graphics driver with
a system beep, while the hot keys here are Ctrl-Shift-**Alt**.


## The Windows window (28 Sep 2026, on the Shadow PC) — `CSharp/SanctuarySonar/MainWindow.cs`

The owner asked for "a better GUI for Windows rather than having to run from terminal". The
shell is now a Windows Forms app (`OutputType` WinExe, `UseWindowsForms`): double-click the exe
and a window opens. Plain native controls, so NVDA reads every one by its label with nothing
extra; checked by dumping the window's UI Automation tree from PowerShell (29 elements, every
button, edit, combo box and slider named, the slider reporting its value) and invoking the
Guide button through it — it went through the worker queue and spoke "No window called Diablo
IV. Is the game running?".

- **`Shell.cs`** holds what `Program.cs` used to: the objects, the worker loop (now on its own
  background thread, each frame in a try/catch that logs `Frame failed:`), `post(action)`,
  `toggleGuide`, `makeHotKeys`, `quit`, and `status` ("Guide on, reading Diablo IV."). The
  window and the hot keys both post onto the same queue, so the ported code still has no locks.
- **`MainWindow.cs`**: Status (read-only edit); Game window (an editable combo box of the
  visible windows' titles, part of a title is enough, remembered as `window=` in
  `settings.txt` — `Settings` now stores strings too); one button per function with the hot
  key in its text (`Guide on  (Ctrl+Shift+Alt+G)` becomes `Guide off …` while on); Beacon
  volume slider 0–100 in steps of 10 (NVDA reads the value, so the slider does not speak —
  the hot keys still say "Beacon 80 percent"); Recent log (the log with `Timer:`,
  `Map-screen check`, `HUD text`, `Guide:` and `Frame` lines left out, seeded with what was
  logged before the window opened, 400 lines kept); Open the log folder; Quit. Alt+letter
  mnemonics within the window. A 300 ms timer copies the guide's state back (hot keys change
  it too). Closing the window quits, as Ctrl-Shift-Alt-Q does.
- `--list` still works from a command prompt (a WinExe attaches to the parent console) and
  shows a message box otherwise. `README-Windows.txt` now lives in the project and is
  published beside the exe.
- **Installing here**: the old install is copied to `C:\Users\Shadow\SanctuarySonar.previous`
  first, then `publish\win-x64\*` over `C:\Users\Shadow\SanctuarySonar\`. The NVDA DLL was also
  copied into `publish\win-x64` (gitignored) so a build can be run from there for a test.
- Untested by the owner's ear: whether NVDA's reading of the window is pleasant, whether the
  startup "Sanctuary Sonar ready" line is now redundant with the window's title being read.

## Two runs on the window build, beacons off, and a Windows recorder (28 Sep 2026, 05:36–06:24)

Log `sonar-2026-09-28-052839.log`. **Run 1 (05:36): floors 1, 2, 3 and the boss arena — a full
clear.** Run 2 (05:47): out of time on floor 1 with the boss room 40 px away. The timer
explains both: floor 1 of each run fell 150 → ~40 s with nothing gained; run 1 then jumped
39 → 216 in one reading (+177 s — a big afflicted pack, ~35 kills at +5, called "Afflicted
pack north, close" 45 s earlier) and run 2's floor had no afflicted pack in reach. Run 2's
guide called a beacon three times, led to two (route down to ~55 px, then "reached" at 35 px
and dropped, silently), and the clock never moved: **the owner: "it did tell me about the
beacon but they're too fiddly to light really."** The routing itself was as in the good runs:
lead within the marker's half for two thirds of the floor, 53 % standing still (56 % in
run 1), no dead ends. One wobble: at "10 seconds" with the boss room north-east, the lead
went east for 6 s before coming back.

- **Beacons are off by default:** `LiveReader.leadToBeacons` (static, from the setting
  `leadToBeacons`; the window's "Lead to beacons for time" checkbox) drops the blue icons
  from the time-target list; afflicted packs are unchanged. **Mirrored in Swift on 29 Sep 2026;
  not replayed** — this PC has no ffmpeg and no recordings; the notes' own warning
  applies (fewer time targets changed floor 3's numbers on 27 Sep). Replay the 09:36 and
  11:21 recordings on the Mac with the flag both ways before trusting it.
- **The per-second `Guide:` line does not say what the lead is** (marker, beacon, pack,
  opening); the beacon reading above was inferred from the spoken lines and route lengths.
  Worth one word.
- **The Windows recorder** (`Recorder.cs`, Ctrl-Shift-Alt-R / M and the window's buttons):
  the game window's client area, BitBlt at 10 fps, BGRA into a `MediaStreamSource` pulled by
  `MediaTranscoder` into H.264 MP4 at 12 Mb/s (~80 MB/min) — Windows' own encoder, nothing to
  install. One folder per run under `%LOCALAPPDATA%\SanctuarySonar\recordings\run-<stamp>\`:
  the `.mp4`, a `.txt` (window, size, start, marks, frames written/dropped) and, on stop, a
  copy of the run's log and of `settings.txt` — everything a replay needs, in one folder to
  send. Refuses under 5 GB free, stops under 3 GB, "Still recording, N minutes" every 15.
  Quit finishes the file first. Tested on the Claude window: 91 frames in 9 s, 0 dropped,
  the MP4 readable by `MediaClip`. **Trap found and fixed: Media Foundation reads a BGRA
  buffer bottom-up**, so the first recording was upside down; `ScreenGrabber(bottomUp: true)`
  captures the recording frames with a positive `biHeight` and costs nothing. Sizes are
  rounded down to even (2560×1609 → 2560×1608), so a replay's layout is scaled by 1608:
  under a pixel of difference. **The BitBlt records whatever is on screen in that
  rectangle**, so a window on top of the game ends up in the video (the test frame had the
  game over the Claude window).
- `SanctuarySonar.exe --frame <mp4> <seconds> <out.bmp>` saves one frame of a recording
  (`RecordingFrame`, MediaComposition thumbnail) — the way to look at a recording here
  without ffmpeg. A Windows MapLab that decodes MP4 without ffmpeg is not written; the C#
  MapLab still wants ffmpeg on PATH.
- The window now sizes itself to its content (`AutoSize`, fixed border): at the Shadow PC's
  150 % the fixed 660×720 window cut off the log box and the bottom row. Wrapping rows are
  capped at 700 logical px so the buttons wrap.
- The owner's wish for shipping: "an option to record the run and send it to me". Recording
  is done; sending (an upload to somewhere the owner chooses) is not — the folder is what to
  send for now.

## The recorder's first run, and what it showed (28 Sep 2026, 06:28–06:40)

`recordings\run-2026-09-28-062823\`: 12 min, 1 GB, 6829 frames, 0 dropped, with the log and
settings beside it — the first Windows recording of real play, and `--frame` reads it. Two
runs in it. **Run 2 (06:33): floors in 1:51, 1:29, 1:54, cleared with ~160 s spare**, time
gained 134 / 84 / 150 s per floor, all from afflicted packs (beacons off). Run 1 (06:28):
floor 1 in 1:10 (the fastest yet) but floor 2 began with 108 s and was lost to an Executioner
elite ("Nangari Oracle", frame at 137 s: 67 s left, the elite at a third of its health, the
boss marker pinned east throughout, five orange icons about) — the "not worth it" fight.
After the clock ran out the timer badge vanished and the guide used its last reading for half
a minute: "Objective south, close. 10 seconds" twice, and "Arch, east, close", in town.

Changes (speech and logging only, no routing; the beacon switch is the only routing change
awaiting its replay):
- **Stale timer**: `Guide.freshTimer` — a reading older than `timerStale` (6 s) counts for
  nothing; the countdown call-outs and `reader.timeLeft` go with it ("Timer: stale, forgotten").
- **"This fight is not paying. Objective east. 35 seconds."** — timed run, fresh timer ≤ 40,
  standing still with red marks about, the timer not risen for `notPayingAfter` (15 s), at
  most every 15 s (`lastTimerRise`, `lastNotPaying`). Unheard live.
- Time-target announcements at most every `timeTargetGap` (20 s; was 8: a pack shifting
  round the player was called eight times in 70 s). Dead ends not in a map's first 6 s.
- The `Guide:` line ends with `to <kind>` (spot / beacon / pack / marker on map / marker
  beeline / opening to marker / mark / area / well / arch / opening) and `, N marks`.
- **Capture reads the game window itself**: `ScreenGrabber.beginFrame(window)` renders the
  client area with `PrintWindow(PW_CLIENTONLY | PW_RENDERFULLCONTENT)` (DWM's copy of the
  window) once per frame and `grab` copies rectangles out of it; if PrintWindow fails it
  reads the screen as before and the log says so once ("Capture: from the screen …").
  Proved with Diablo IV (D3D12, windowed): `--picture <out.bmp>` with the Sanctuary Sonar
  window moved over the minimap gave the game's minimap untouched. The recorder uses it too.
  Exclusive fullscreen is still untested (PrintWindow may give black there; the fallback
  reads the screen). **Why it mattered:** at 06:32:17 the app's window was in front of the
  game without the owner bringing it up — most likely the game window lost activation on the
  loading screen out of the dungeon and Windows activated the next window, ours. The window
  now also opens bottom-left (away from the minimap and the tracker column) in case.
- The owner's next idea: **a menu of the world map's points of interest** (waypoints,
  Whispers, dungeons, Tormented gifts) that puts the pointer on the chosen one, because the
  2D map has to be swept blindly. That needs icon recognition on the map screen and moving
  the pointer — the first thing that sends anything to the game (only the pointer; the
  owner clicks). Not built; discussed with the research on other blind players' complaints.

## Map points: the world map as a list (28 Sep 2026, 07:12 recording → `MapPoints.cs`, `Pointer.cs`)

The owner: "the map you pull up is a real pain to navigate, it's a 2D map that you have to
randomly move around until you find places; what would be great is a menu system that comes
up that allows us to select waypoints, whispers, dungeons, tormented gift of mysteries etc,
puts the pointer on it so we can click it." Research the same day (Blizzard forums, wiki,
GitHub; Reddit and audiogames.net unreachable from here): no such thing exists, official or
community; blind players "zoom and sweep"; a list view was asked for in July 2025 and never
answered; Blizzard's 2026 additions are Pathfinder (a route to a pinned overworld point) and
small screen-reader fixes, nothing for dungeons or the map.

- **The recording** (5 min, town map, world map at three zooms, a controller-selected dungeon
  with its tooltip) showed: icons are fixed sprites at constant screen size whatever the
  zoom; the controller cursor is a hollow white diamond that stays at the screen's centre
  while the map pans under it; selecting an icon opens a tooltip with the type, the name and
  the action ("Capstone Dungeon / Den of the Apostate / Travel"); the mouse pointer coexists
  with the controller cursor on the map.
- **`MapIcons.find`**: colour masks (cyan, red, orange, green, black, white; thresholds
  measured on the 2560×1608 frames), 8-connected blobs, size rules scaled by picture height.
  Black-outlined blobs are dungeons (white gate), dungeons with a Whisper (green leaf),
  strongholds (orange flame), capstones (red padlock) or "Marker"; cyan blobs are waypoints
  (≤ 48 px) or towns; red alone is a Whisper; green alone a "Green marker". The screen's
  furniture is masked out (top 200, bottom 200, right 200, the panel bottom-left); duplicates
  within 22 px are merged. Bearings and distances are from the screen's centre — the cursor,
  which is the player after L3 "Center on Player"; the diamond finder exists but the
  centre is what matters. `--map-points <frame.bmp> [ringed.bmp]` runs it on a saved frame:
  43 / 38 / 35 / 9 points on the recording's four map frames in ~90 ms, every waypoint,
  dungeon, stronghold and Whisper ringed on inspection, the misses being UI chrome now masked.
- **Hot keys L / K / J** (`Shell.scanMap`, `nextPoint`, `previousPoint`, `goToPoint`): L scans
  and says "43 points: 14 Markers, 12 Waypoints, … 1 of 43. Capstone dungeon, north-west,
  near." then steps; J puts the pointer on the point (`Pointer.moveTo`, `SetForegroundWindow`
  first if the game is not in front), waits 650 ms, OCRs a 1120×820 region above the icon and
  speaks the tooltip's lines (all-capitals lines dropped — region names) and "Press again to
  click."; J again within 15 s sends one left click (`SendInput`). The window has the same as
  buttons and a list box. **The first thing Sanctuary Sonar sends to the game** — the owner has
  no mouse within reach ("no mouse in vicinity"), so the click comes from here; the README's
  promise is amended. Untested live: whether a mouse hover opens the tooltip while the game is
  in controller mode, whether the click travels/pins, and whether the OCR region catches the
  tooltip (it is logged as "Tooltip text: …" every time, so the next run answers all three).
- Not done: filtering by kind (all points are one list, nearest first); the map must be
  zoomed so icons do not overlap; the Tree of Whispers / Tormented gift icons are not yet
  identified by kind (they will read as Green marker or Marker and the tooltip names them).

## Hand-over to the Mac session (29 Sep 2026): what to mirror in Swift

Everything above from "The Windows window" on was built on the Shadow PC in C#. The
Cartography core is untouched (still a zero-diff port). The Swift app is behind on these, in
the order they matter to the owner:

1. **Beacons off by default** — `LiveReader.leadToBeacons` (C#: a static bool from the
   setting `leadToBeacons`, default false, a checkbox in the window) drops the blue icons
   from the time-target list before `all` is built; afflicted packs unchanged. **Replay the
   09:36 and 11:21 recordings with the flag both ways before installing**: the notes record
   that fewer time targets once changed floor 3's numbers (8/4, 9/4, 5/2 is the bar).
2. **Stale timer** — `Guide.freshTimer`: a timer reading older than 6 s counts for nothing;
   the ≤ 30 s "Objective … N seconds" call-outs and `reader.timeLeft` use it; when it goes
   stale, `lastTimer = nil`, `reader.timeLeft = nil`, log "Timer: stale, forgotten".
3. **"This fight is not paying. Objective east. 35 seconds."** — timed run, fresh timer ≤ 40,
   `heading == nil`, red marks > 0, timer not risen for 15 s, at most every 15 s.
4. **Time-target announcements** at most every 20 s (was 8); **no "Dead end" in a map's first
   6 s** (`mapBegan`).
5. **The `Guide:` log line** ends `, to <kind>` (spot / beacon / pack / marker on map / marker
   beeline / opening to marker / mark / area / well / arch / opening) and `, N marks`.
6. Windows-only for now, worth porting when the Mac catches up: the recorder is already on
   the Mac; **map points** (`MapPoints.cs` is pure pixel arithmetic on BGRA and ports as is;
   the Mac side needs the map screen captured from the game window, the pointer moved with
   `CGWarpMouseCursorPosition`/`CGEvent` and a click — the first input the Mac app would send)
   and **capture from the window itself** (the Mac's ScreenCaptureKit already does that).

The Windows shell's names are the Swift names, so each item is a grep away: `leadToBeacons`,
`freshTimer`, `timerStale`, `notPayingAfter`, `timeTargetGap`, `mapBegan`.
