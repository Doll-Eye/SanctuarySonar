Sanctuary Sonar for Windows

What it needs
- Windows 10 or 11, x64, with the .NET 10 desktop runtime.
- Diablo IV running in a window or borderless fullscreen (not exclusive fullscreen), Map
  Display set to Minimap. It reads the game window itself, so other windows on top of the
  game (this one included) do not get in the way.
- An English OCR language pack (Windows Settings > Time and language > Language > English).
- Speech: NVDA through nvdaControllerClient64.dll placed beside the exe (from NV Access's
  controller client zip, x64\nvdaControllerClient.dll renamed; LGPL). Without it, or with
  NVDA not running, it speaks through SAPI.

How to run
- Double-click SanctuarySonar.exe. A window opens with a button for every function, the
  game window to read, the beacon volume and the recent log. It also says "Sanctuary Sonar
  ready" so you know speech works.
- Every function is also a hot key (Control + Shift + Alt + key) that works from any app,
  including the game, so the window can stay in the background.
- The "Game window" box holds part of the game window's title ("Diablo IV" to begin with).
  Pick another from the list or type part of a title; it is remembered.
- Closing the window quits. So does Control + Shift + Alt + Q.

Hot keys (Control + Shift + Alt + key), from any app
- G  guide on or off          W  where the beacon leads, and what else is unexplored
- D  describe the map         O  say the objective, with its count
- S  mark this spot           B  take me back to it
- N  start a new map          = / -  beacon louder / quieter
- R  record the run on or off M  mark a moment in the recording
- L  map points: scan the open map, then step to the next point   K  previous point
- J  point at the chosen map point and read its tooltip; J again within 15 s clicks it
- Q  quit

The map's points, as a list (the world map, on Start)
- Open the game's map, then press L: it reads the icons off the screen — waypoints, towns,
  dungeons (with a Whisper or not), strongholds, capstone dungeons, Whisper bounties — and
  says how many of each and the nearest one: "Waypoint, north-west, near." L and K step
  through them, nearest to the centre of the screen first (press L3, Center on Player,
  first and the centre is you). J puts the mouse pointer on the chosen icon and reads the
  game's own tooltip for it (its type, name and action, "Waypoint. Kurast Docks. Travel").
  J again clicks it: a waypoint travels, anything else gets pinned, and the game's audio
  navigation and Pathfinder take you there. The window lists the same points.
- This is the only thing Sanctuary Sonar ever sends to the game: the pointer, and that one
  click when you ask for it. Moving the pointer may switch the game to mouse-and-keyboard
  prompts until you touch the controller again.

Command-line options (for testing; none are needed)
- --list            print the windows it can see (to check the game's title)
- --window "text"   choose the window by part of its title
- --busy 0.60       the minimap's busy gate (rejects the inventory screen)
- --pictures 3      how many of the first frames to save as pictures beside the log
- --no-speech-at-start

Recording a run (to send back, so what worked and what did not can be worked out)
- Control + Shift + Alt + R, or the window's "Record the run" button, records the game window
  at ten frames a second to an MP4 with the run's log and a notes file beside it, one
  folder per run under %LOCALAPPDATA%\SanctuarySonar\recordings\ ("Open the recordings
  folder" in the window). R again, or the button, stops it. M marks a moment in the notes.
  Roughly 80 MB a minute; it refuses to start under 5 GB free and stops under 3 GB.
- Send the whole run folder (video, .txt, .log) back.

Beacons
- "Lead to beacons for time" is off unless ticked: beacons need lighting by hand, which is
  fiddly without sight. Off, the guide still goes for afflicted packs, which come to you.

Where things go
- Log and pictures: %LOCALAPPDATA%\SanctuarySonar\logs\   (read the newest file after a run;
  the window's "Open the log folder" button goes there)
- Settings (beacon volume, game window): %LOCALAPPDATA%\SanctuarySonar\settings.txt

Not yet on Windows
- The picture-saving key, sounds for sighted players, controller chords
  (use Steam Input to send the hot keys), Windows Graphics Capture for exclusive fullscreen.

Everything it does is read the screen and talk, except the map points feature, which moves
the mouse pointer and clicks once when you press J twice. It never presses a key, never
reads game memory, never touches game files.
