# Sanctuary Sonar

An audio navigation aid for blind players of Diablo IV. It reads the game's minimap off
the screen, builds a map of the dungeon as you explore, and leads you by sound: a stereo
beacon that pans left and right and rises in pitch for north, falls for south, plus short
spoken lines through VoiceOver for the things the beacon cannot say — the objective, a
healing well, a dead end, the floor you are on, how many seconds are left.

It was built in two days at the end of September 2026 by one blind player and one AI
assistant, and on the second day it cleared a timed Kurast Undercity run. It is an aid,
not a product. Everything learned along the way, including every dead end, is in
`CLAUDE.md`; read it before changing anything.

## What it does and does not do

- **Reads the screen only.** It captures the game's window (Shadow PC, GeForce NOW, or a
  local game) and reads pixels and on-screen text. It never sends input to the game,
  never reads game memory, never touches game files.
- **Leads to the objective when it can see one**, to unexplored ground when it cannot,
  and around walls in between. In ordinary and nightmare dungeons it follows the
  tracker's objective (slay, travel, enter), healing wells and arches, and the red
  marks of enemies still to kill. In the Kurast Undercity it follows the objective
  marker, reads the countdown, calls the floor, and sends you to beacons and afflicted
  packs for time.
- **Speaks only what a screen reader cannot already tell you.** With VoiceOver off it says
  nothing at all.

## Requirements (macOS)

- macOS 27 on Apple silicon, Xcode 27, and Screen Recording permission for the app.
- Diablo IV with **Map Display set to Minimap** (not the map overlay), shown in a window
  the app can capture. Tested over Shadow PC; GeForce NOW and PS Remote Play should work
  the same, untested.
- VoiceOver for speech. A DualSense and [Muteny](https://github.com/Doll-Eye/Muteny)
  for controller chords are optional; every function also has a keyboard shortcut.

## Building

    ./build.sh

builds a Release app, signs it with your Apple Development identity, installs it to
/Applications as "Dungeon Guide" (the name in code has not caught up with the project's
yet) and launches it. Build outside iCloud-synced folders; the script does. The package
depends on [Keel](https://github.com/Doll-Eye/Keel), fetched automatically.

## Using it

Choose the game's window from the menu bar item, then:

| Key (Control-Shift-Command +) | Does |
|---|---|
| T | Guide on or off |
| W | Where the beacon is leading, in words, and what else is unexplored |
| D | Describe the map: wells, arches, marked spot, marked enemies, unexplored directions |
| O | Say the objective, with its count |
| X / B | Mark this spot / take me back to it |
| N | Start a new map |
| = / - | Beacon louder / quieter |
| F / M / P | Record the window / mark the recording / save a picture |

(T, X and F rather than G, S and R because VOCR, which many VoiceOver users run, takes
Control-Shift-Command with G, S and R for itself; its R starts real-time OCR of the screen.)
| L / K | With the game's map open: read its points (waypoints, towns, dungeons, strongholds, Whispers) as a list, nearest first, and step to the next / previous |
| J | Put the pointer on the chosen point and read the game's tooltip for it; J again within 15 s clicks it (a waypoint travels, anything else is pinned). The only thing the app ever sends to the game; needs the Accessibility permission |

The beacon: a knock every 1.3 s when you face away from the route, a knock and a click
when near it, a knock and a ping when on it. Left and right pan; up (north) is higher in
pitch, down (south) lower. When you stop against something the beacon swings to one side
until you are moving again. Opening the game's full map (Start) reads it out.

## Replaying recordings

`MapLab <recording.mov> <output folder>` runs the whole reader and router over a recording
made with the app's recorder and prints one line per fifth of a second, with pictures of
the map as it grows. It is how every change here was judged: a change is kept only if it
replays at least as well on known-good recordings. Options are documented at the top of
`Sources/MapLab/main.swift`.

"Lead to beacons for time" (in the window) is off unless ticked: the Undercity's beacons
need lighting by hand, which is fiddly without sight. Off, the guide still goes for
afflicted packs, which come to you.

## Windows

`CSharp/` holds the same map reading and routing in C# (`Cartography`, verified against
the Swift replay line for line), a replay tool (`MapLab`, needs ffmpeg) and the Windows
app (`SanctuarySonar`: window capture, the built-in OCR, NVDA's controller client or SAPI,
WASAPI, global hot keys Control-Shift-Alt + the same letters, a recorder, and the map
points). Build with `dotnet publish SanctuarySonar -c Release -r win-x64
--self-contained false`; `CSharp/SanctuarySonar/README-Windows.txt` is the user's guide.

## Licence

MIT. See `LICENSE`.
