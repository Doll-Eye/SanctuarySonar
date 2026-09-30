# The Kurast Undercity, and how to get a blind player through it

Written 30 Sep 2026 after eleven recorded runs on the CrossOver build. The aim is a run a blind
player can complete alone, on Normal, with no Tribute.

## What the Undercity is

- One **district** per run, chosen at random from three: Cave (boss Longtooth the Wretched),
  Temple (Yoche the Golden), Ziggurat (Alia, Kurast's End). Each district has its own tileset
  and look; the minimap texture differs enough between them that the reader's gates had to
  move (busy 0.15–0.33 in Temple, 0.45–0.48 in Cave).
- **Three timed floors, then an untimed boss arena.** Layouts are assembled at random from the
  district's pieces; the same district plays out differently each run. Sources describe the
  Undercity as "a randomized dungeon"; nothing describes the floors as fixed.
- **The timer starts when you leave the starting bubble.** Every floor begins inside a magic
  circle where the District Timer does not tick. Measured on this account: 150 s on Normal
  with no Tribute (guides quote 100–120 s for other seasons and Tributes; Tributes cut it to
  45–75 s and are not for us).
- **Time is bought by killing Afflicted monsters** (skull with an orange hourglass on the
  minimap): about +8 s for a generic Lesser Afflicted, +14 named, +16 Afflicted, +30 Greater;
  the guide has seen +5 s per pack member and jumps of +37 and +83 s on packs. Killing time
  is the difference between a clear and a fail: this morning's floors were cleared with 60–70 s
  to spare when packs were near the path, and lost when the path was long.
- **Spirit Beacons** spawn waves, give Attunement (rewards) and, on completion, a movement-speed
  and cooldown buff. They are not needed to finish. The owner cannot light them by feel; the
  guide no longer leads to them.
- **The objective marker** ("Reach District Boss before time expires") is pinned to the
  minimap's edge in the boss room's direction from the first second of each floor. When the
  boss room is on the minimap the marker sits on it. Reaching the arena ends the floor. Whether
  a door, stairs or an attunement check stands between the corridor's end and the arena is
  not documented anywhere found; on floor 3 of the 07:29 run the last corridor toward the
  marker ended at a walled courtyard and the way on was unknown to guide and player alike.
- The boss arena is untimed. Attunement rank 1 is needed for the reward upgrades, not for
  finishing.

Sources: Icy Veins, Maxroll, PureDiablo, Fextralife and Game8 Undercity guides (30 Sep 2026;
several refused automated reading, the numbers above are from their summaries).

## What a floor asks of a blind player

Walk from the bubble to the boss room, through two to four rooms and corridors with
level changes, in about two minutes of movement, killing what is in the way and any
Afflicted within reach. A sighted player does this by glancing at the minimap's marker every
few seconds and walking at it, opening the full map when unsure. Nothing in the game speaks
the layout or the direction.

## The original methodology, judged

The guide builds a map from the minimap as you walk (stitching), finds the frontier between
seen and unseen floor (openings), and leads to the opening that best serves the objective —
biased toward the pinned marker in a timed run — with a stereo beacon for the bearing, a
sidestep when you are stalled, and speech for what the beacon cannot say.

What the eleven runs say:

- **When the map is honest it works.** 27 Sep: a clear. 30 Sep 07:29: floors 1 and 2 in 3:30,
  the arrival rule at the boss marker twice; 06:30: floors 1 and 2, floor 3 reached with 71 s.
- **Almost every failure was a reading fault, not a routing fault.** Window at Wine's raised
  layer; the reveal-disc mismatch ("New map" every 3 s); "Monsters" read as the place (run
  untimed, marker never looked for); the busy gate; steering gated on "no marks anywhere";
  the floor-change fade seeding a map of phantom floor; the text recogniser wedged by Apple's
  Neural Engine compiler; the grey arrow in the starting bubble. Each was one gate tuned on a
  video stream meeting a natively rendered game. All are fixed; more may be waiting.
- **The routing's real weakness is the frontier.** An opening is where seen floor meets
  unseen. Ledges, parapets, thresholds and stairs are drawn on the minimap as thin lines the
  reader cannot tell from floor, so it leads through walls (the 29 Sep parapet) — and it
  treats a doorway it cannot see through as a wall, so it turns you round (the 07:29
  courtyard). It leads *away* from the marker whenever the only openings left are behind.
- **The beacon is followed within one or two seconds.** Measured across three runs: median
  reaction 1–2 s, so every wrong swing costs real distance. A steady lead beats a precise one.

Verdict: the approach can finish the Undercity — it has — but it stakes everything on a map
built from a picture that hides exactly the features that stop you, and on a chain of
readers that the native render has broken seven times in two days.

## Alternatives, compared

### A. Marker-first with wall-following ("bug" navigation)

Walk straight at the pinned marker; when stopped by something, follow the obstacle's edge
(the steering already swings the beacon to the side with floor) until the marker's bearing is
clear again; the map is used only to notice a closed pocket and to avoid re-entering it.

- For: this is what a sighted player does. It needs no frontier, so ledges and thresholds
  stop mattering unless they are truly impassable. Bug algorithms are provably complete for a
  known goal direction in a bounded world; the marker is exactly that.
- Against: wall-following can be long in a maze; dead-end pockets need the map to escape;
  the marker gives a bearing, not a distance, so "arrived" comes only from the marker
  appearing on the minimap (which the guide already detects).
- Compared with the original: same sensors, far less reliance on the map's correctness.
  Today's "do not turn your back on the marker" rule is the first step toward it; the
  courtyard on 07:29 is the case it must get right (was the way on truly behind?).

### B. Read the full map in the bubble

The timer is stopped inside the starting bubble. Opening the game's map there costs nothing.
If the Undercity's map shows the whole floor's layout (many Diablo dungeons show their
skeleton once entered; the Undercity's behaviour is unknown), the route can be planned before
the first step, and the frontier problem disappears.

- For: a plan from a complete drawing, taken at leisure; the map-screen reader already exists.
- Against: unknown whether the layout is revealed; the map has no ledge information either.
- Needs one thing from the owner: see below.

### C. Layout memory across runs

Three districts, randomised from a small pool of pieces. Recording many runs would let the
app recognise pieces (a corridor shape, a room) and remember where their exits were.

- For: a sighted player's own memory is this. Against: speculative, needs dozens of runs and
  a piece library; the randomisation may defeat it. Not first.

### D. Buy time instead of precision

The floor is lost on the clock, not on distance. Movement speed (Beacon completion grants it;
gear and elixirs too), a build that kills Afflicted packs on the way (+8 to +30 s each), and
no Tribute give the largest budget the game allows. The guide can help by calling packs only
within a short detour of the marker's line, as it does.

- For: every second here buys tolerance for every other error. Against: needs the owner's
  build decisions; the guide cannot fight.

### E. Read the world, not the minimap

Detect doors, stairs and ledges from the game view itself. The one sensor that would see what
the minimap hides. Against: hard vision work, a moving camera, three tilesets; months, not
days. Not now.

## Recommendation

Marker-first (A), with the map demoted to a safety net and B tested immediately because it
could make A trivial:

1. In a timed run with the marker pinned, the lead is the marker's bearing. Walls are the
   steering's job; a stall longer than the steering can solve calls a map-based escape (the
   nearest opening on the marker's side, never one behind unless the pocket is closed).
2. The frontier and its openings remain for ordinary dungeons and for the boss floor.
3. Speech shrinks to what changes the plan: floor, marker found/arrived, "Enemies close",
   "Afflicted pack near the way", the countdown.
4. The starting bubble is used: the guide says "Ready" only when it has the marker, the floor
   number and the timer read; the player leaves on "Ready", not before.

## What the owner can provide

1. **One run, three floors, opening the map (Start) inside the starting bubble for four
   seconds before moving**, recorded. This decides B in one recording.
2. **One run played marker-first by hand**: ignore the beacon's openings, walk at the marker's
   direction, stop when blocked and say aloud what stopped you. The recording shows how often
   walls truly block and what the steering must solve.
3. **Keep every Undercity recording**; say which district and floor a stall happened on.
4. The character's movement speed, and whether an Evade or dash is bound — the guide can
   time "push on" calls to it.
5. At any moment the guide seemed lost, a word in the recording ("wall", "monsters", "door")
   — the log has the guide's side, never yours.
