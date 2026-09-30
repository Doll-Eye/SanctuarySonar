# D4 Dungeon Guide — research and design

Written 25 September 2026. Working folder name only; rename it when the project has a name.

## The problem

Diablo IV's Audio Navigation Assistance (Season 6, October 2024) leads you to a map pin with
spatial pings, but it runs on the overworld navigation mesh, and dungeons do not have one.
Blizzard's accessibility lead, Drew McCrory, said so in June 2025: "we don't have the same nav
mesh in dungeons. So far, I haven't managed to get it to work there." Nothing in the 3.0–3.2.1
patch notes says it has changed. So in a dungeon a blind player has door names from the screen
reader, targeting sounds, the traversal cue — and no sense of where to go.

The game is streamed (Shadow PC, GeForce NOW), so the aid has only what reaches the Mac: the
video of the stream window and its sound. No memory reading, no mods, nothing on the remote
machine. It reads pixels and never sends input to the game — that keeps it a screen reader,
not a bot, and it should stay that way.

## How sighted players find their way

From gameplay footage (Forbidden City run, YouTube p7YnPmVwcDY) and guides:

- **They read the minimap constantly.** Top-right rectangle, roughly 15 % of the screen width.
  The player is always at the centre and the map scrolls. Explored floor is a lighter
  grey-brown with a thin light outline; walls are dark diagonal hatching; corridors run
  diagonally because of the isometric camera.
- **They go where they have not been.** The minimap draws a trail of dots behind you, and
  unrevealed parts of the dungeon are simply absent. "Head for the edge of what is drawn,
  avoid the corridors with dots in them" is most of dungeon navigation.
- **They follow icons when there are any.** Red dots for enemies an objective wants dead
  (shown map-wide during "Slay all enemies"), skulls for elites and Animus carriers, an urn
  icon, the dungeon exit, door icons, and quest markers clamped to the minimap's edge pointing
  at something off it. A door opening pings the minimap.
- **They read the objective tracker** under the minimap: "Slay the Enraged Spirits: 2",
  "Travel to the Ghastly Depths", "Slay all enemies in the Ghastly Depths". Large clean text.
- **They open the full map** when lost. It is opaque in dungeons and shows only what has been
  explored.
- **They learn dungeons.** Layouts are hand-built with some variation; objective positions
  vary. The Pit and the Undercity are fully random. Sources disagree on how much ordinary
  dungeons vary — see open questions.

## Facts that change the design

- **The camera never rotates** (fixed isometric; Blizzard forum requests for rotation remain
  requests). So screen-up is always the same way in the world, the map never turns, and
  **the direction you hear can be the direction you push the stick.** That removes the
  hardest problem in stick-controlled guidance. Worth confirming on the owner's stream.
- **Patch 3.0 (28 April 2026) added a Map Overlay** in the centre of the screen, instead of
  the minimap. Zoom, opacity and **colour** are set in Options > Gameplay; there are hotkeys
  to switch and adjust. A big map in a colour chosen to be easy to separate is a far better
  thing to read from compressed video than a 190-pixel corner. Unverified: how much the game
  shows through it at full opacity.
- **Patch 3.0 also added Pathfinder**, which draws a chosen route on the ground and on the
  overlay. Whether it works in dungeons is **not settled**: launch-era guides say routing
  does not work in dungeons, but patch 3.0.2 fixed "dungeon minimap paths and objective
  markers could point to invalid or unreachable locations", which suggests some path exists.
  **This is the first thing to settle**, because if the game draws a line to the objective,
  the job becomes "follow the line", which is far simpler and more reliable than exploring.
- **The minimap is partly transparent.** Bright spell effects show through it, which will
  confuse a naive reader. The reader must know when it is unsure and say nothing rather than
  guess.

## Nobody has built this before

No blind-community tool reads a minimap from screen capture — not for Diablo, WoW or League.
The WoW tools (Sku, BlindSlash) are addons using hand-authored waypoint graphs. The closest
published designs, and what to take from each:

- **Microsoft Soundscape** (open source; its beacon code was read directly). The beacon is a
  looping rhythm that switches sound variant by angle: a bright on-course variant within
  ±15 °, then wider bands, then a **separate "behind" sound**, so front/back never relies on
  HRTF. Variants switch on the beat, so turning never chops the rhythm. Slow and very slow
  versions exist to fight fatigue. On arrival the beacon turns itself off, with hysteresis
  (enter at 10 m, leave at 25 m).
- **Surveyor** (CHI 2024): a grid per room of explored / wall / unknown, with "unexplored
  patches" offered as a list and a beacon to the chosen one. Liked most by 5 of 9 players; the
  complaint was being made to feel out every room, so offer frontiers, never force them.
- **NavStick** (UIST 2021): tilt a stick and hear what is in that direction. It built better
  mental maps than menus did.
- **Diablo IV's own pings**, as SightlessKombat describes them: widely spaced drum hits when
  wrong, closer hits with a click when roughly right, fastest double hit with a ping when on
  heading. The owner already knows this language, so the dungeon beacon should sound related
  but distinct, so the two are never confused.
- **Forza's Blind Driving Assists**: plain stereo panning, every cue switchable with its own
  pitch and volume.

## The design

### Seeing

- **ScreenCaptureKit** captures the stream window alone (Shadow's window, NVIDIA's app, or
  Muteny's own player), even when covered. 10–15 frames a second, cropped on the GPU to the
  map, in the 420v format so brightness can be read directly. Idle frames are skipped.
  Needs Screen Recording permission, and since Sequoia macOS re-asks monthly — the app must
  say so when capture stops, not fall silent.
- **Map source: the overlay if it reads well, the minimap otherwise.** One recording decides.
- **Pixel classification**, brightness first because streaming halves colour resolution:
  floor, wall, unknown, icon. Thresholds come from labelled frames of the owner's own stream,
  not from screenshots on the web.
- **Stitching.** Each frame's floor/wall mask is aligned to the last by phase correlation
  (the map only scrolls, never turns or zooms, so translation is enough) and voted into one
  persistent map of the dungeon. The player arrow, icons and border are masked out first.
  A weak alignment is rejected, never forced. Loading screens and a changed dungeon name
  start a new map.
- **Icons** by colour and shape first; a small Core ML detector only if a labelled set of
  failures shows colour rules cannot cope.
- **Text.** Vision OCR on the objective tracker, only when that region changes, one or two
  times a second. This gives the objective, the counters, and the dungeon name.

### Deciding where to go

In order of preference, and the owner can always choose:

1. **The game's own route**, if Pathfinder draws one in dungeons.
2. **A visible target**: an objective icon, the red dots, the urn, the exit, an edge-clamped
   quest marker.
3. **The nearest unexplored opening**: a frontier on the stitched map (walkable cells next to
   unknown ones), chosen by size and distance, never down a corridor already walked.
4. **Back to a remembered place**: the entrance, the urn or pedestal (the Animus and
   carry-the-stone objectives are mostly backtracking), the last door.
5. **A learned dungeon**: after a full clear, the stitched map is saved under the dungeon's
   name; next time the minimap is matched against it, and the whole layout is known from the
   first step. Only as good as the layouts are repeatable, so it comes last.

Whatever the target, the route is planned with A* on the stitched map, with walls grown
outward so the path keeps off them, and **the beacon aims at a point a short way along that
path**, not at the target itself. It says which way to go now, around the wall, not in a
straight line through it.

### Hearing

- **One beacon**, anchored to the screen (up = stick up). Soundscape's shape: a low rhythmic
  loop, a brighter on-course variant within about ±15 °, a distinct behind sound, variant
  changes on the beat. Stereo panning by default with HRTF as an option, because generic
  HRTF confuses front and back.
- **Distance as tempo**, in a few steps, not pitch. A short spoken "arrived" and the beacon
  stops on arrival, restarting when you leave or ask.
- **The game's own sound is the priority.** The beacon is the quietest layer, off by default
  in combat if that proves better, and every cue can be switched off separately.
- **Speech only for events**, through VoiceOver: the objective changed, a new target, "dead
  end", "that was the last unexplored part", "lost the map". Never for things the game's
  screen reader already says.

### Feeling

- **The stick check.** The app reads the left stick from the DualSense without seizing it,
  compares the stick's direction with the route's, and gives a small tick in the hand on
  crossing into the on-course band. This answers "am I pushing the right way" without
  listening harder. Over USB this uses the audio haptics Keel already drives; over Bluetooth,
  a coarser motor version. Unverified: whether this collides with the streaming client's own
  rumble — test before relying on it.

### Controls

Mute chords, the same language as Muteny: Mute held plus a button. A proposal to try, not
a decision:

- Mute + D-pad left / right: cycle the target type (route, objective, unexplored, back).
- Mute + Cross: where am I — dungeon, objective, what is near and which way.
- Mute + Circle: beacon on or off.
- Mute + D-pad up: repeat the last announcement.

Unverified: whether Shadow or GeForce NOW use the Mute button for anything. The pad is
seized only while Mute is down, as in Muteny, so chords never reach the game. Only one app
can own the chords, so this and Muteny should not run their chord engines at the same time.

### Not getting stuck, not lying

- **Confidence gates every sound.** Map not visible (menu, inventory, full map, loading), or
  alignment weak, or the map shows through a spell effect: the beacon fades to silence and,
  if that lasts, says "lost the map" once. A wrong beacon is worse than none.
- The last good map and route are held through a few bad frames; nothing is wiped on one
  frame.
- Every exit — shake, controller gone, quit — stops sound and releases the pad, as in Muteny.
- **Everything is logged**: each classification decision, alignment strength, target and
  bearing. The log is the owner's eyes on it, as in Muteny.

### Where it lives

A new sibling app on **Keel** (DualSense, Announcer, Sound, Feedback, AppLog are already
there). Muteny's rules carry over: engine and nodes fixed for the app's life, nothing that
touches HID inside a HID callback, `Timer.common`.

## How it gets built, and how each stage is checked

0. **Recorder.** Capture the stream window to a video file, with the tracker text logged
   alongside. Checked by playing back one recording. Everything else is developed against
   these recordings, offline, repeatably — and debug pictures of what the reader thinks it
   sees can be checked from here without the owner looking at anything.
1. **Reader.** Classification, stitching, icons, OCR on recordings. Checked by debug overlays
   of recorded frames and a sighted helper's labels on a few dozen of them.
2. **Beacon to visible targets.** The first thing worth playing with. Checked by ear.
3. **Frontiers, backtracking and the stick check.**
4. **Learned dungeons**, if layouts turn out to repeat.

## Open questions, and what settles each

1. **Does Pathfinder draw a route in dungeons, and to what?** One recording in a dungeon with
   it turned on.
2. **How does the Map Overlay look through the stream,** and which colour and opacity
   separate floor from wall best? One recording, trying two or three colours.
3. **Minimap pixel size, colours and behaviour on the owner's stream** at their resolution,
   through both Shadow and GeForce NOW. Same recording.
4. **Are dungeon layouts repeatable?** Two full runs of one dungeon, stitched and compared.
5. **When do objective icons appear** — always, once discovered, within a radius? Recordings.
6. **Does the streaming client use the Mute button, and does its rumble collide with ours?**
   Try it.
7. **Does the Screen Recording prompt read well with VoiceOver?** Try it.

## Sources

- Blizzard, Vessel of Hatred accessibility: https://news.blizzard.com/en-gb/article/24139219/a-crucible-for-allaccessibility-features-in-vessel-of-hatred
- McCrory interview, June 2025: https://www.gameaccessibilitynexus.com/blog/2025/06/25/diablo-iv-anniversary-interview-with-drew-mccrory/
- Patch 3.0 notes: https://news.blizzard.com/en-us/article/24271857/diablo-iv-patch-notes-3-0
- Lord of Hatred, Map Overlay: https://news.blizzard.com/en-gb/article/24267729/prepare-for-the-reckoning-lord-of-hatred-draws-near
- Pathfinder: https://www.icy-veins.com/d4/news/diablo-4-lord-of-hatred-pathfinding-feature/
- SightlessKombat review: https://www.sightlesskombat.com/diablo-4-retail-launch-accessibility-review/
- Ross Minor review: https://rossminor.com/2023/06/08/diablo-iv-blind-accessibility-review/
- Minimap icons and features: https://mythicdrop.com/guide/diablo-4-minimap
- Dungeon objectives: https://maxroll.gg/d4/resources/general-dungeon-guide
- Layout variation: https://maxroll.gg/d4/dungeons/prison-of-caldeum and https://us.forums.blizzard.com/en/d4/t/why-did-we-ever-give-up-on-randomly-generated-dungeon-layouts/139921
- Patch notes 1.3–1.5 (red dots, icons): https://news.blizzard.com/en-us/article/24140806/diablo-iv-patch-notes-1-3-1-5
- Soundscape source: https://github.com/soundscape-community/soundscape
- Surveyor: https://arxiv.org/html/2403.10512
- NavStick: https://arxiv.org/abs/2109.01202
- Forza blind driving: https://news.xbox.com/en-us/2023/04/27/forza-motorsport-accessibility-features-blind-driving/
- Gameplay footage: https://www.youtube.com/watch?v=p7YnPmVwcDY
- ScreenCaptureKit: https://developer.apple.com/videos/play/wwdc2022/10155/
- Phase correlation: https://docs.opencv.org/3.4/d7/df3/group__imgproc__motion.html
- Frontier exploration (Yamauchi 1997): https://link.springer.com/article/10.1023/A:1008936413435
