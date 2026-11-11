# OpenPlane

OpenPlane is a free, open-source app for Nuclear Option planes. It does three things:

1. **Convert** planes you built in Flyout into Nuclear Option planes.
2. **Build** your own planes for Nuclear Option, no Flyout needed.
3. **Share** them the way Nuclear Option plane mods are shared today.

> Status: early development (0.1). Expect rough edges, and please report what you find.

## What you need

- **Windows 10 or 11 (64-bit).** The 0.1 release of the app is for Windows only. It runs on the Microsoft Edge
  WebView2 Runtime, which Windows 10 and 11 normally have; if the window stays blank, install the WebView2 Runtime
  from Microsoft.
- **Nuclear Option** (Steam). The app installs the OpenPlane mod into it.
- **Steam Deck, SteamOS and Linux:** the app itself is untested there. The mod works under Proton: install it with the
  separate mod download (or copy it from a Windows PC), copy your `.oplane` files into the game's planes folder (below),
  and add this to Nuclear Option's launch options in Steam once so mods load under Proton:
  `WINEDLLOVERRIDES="winhttp=n,b" %command%`

## Download and install

- **The app (Windows):** download `OpenPlane-<version>-win-x64.zip` from the Releases page, unzip it anywhere (for
  example your Documents folder) and run `OpenPlane.exe`. Nothing else to install: .NET is included. The app isn't code
  signed yet, so Windows SmartScreen may say "Windows protected your PC": choose **More info**, then **Run anyway**. Each
  release lists the SHA-256 of its files in `SHA256SUMS.txt`, so you can check your download.
- **Starter planes without the app:** `OpenPlane-starter-planes.zip` holds the three starter planes; unzip it into the
  game's planes folder (below).
- **The mod only (NOMM, Steam Deck, servers):** with NOMM, install OpenPlane from its list once OpenPlane is in its
  registry (NOMNOM); NOMM adds Blueprinter too and uses `OpenPlane-nomm.zip` by itself. By hand: unzip
  `OpenPlane-mod.zip` into the Nuclear Option folder (the one with `NuclearOption.exe`) and install
  [Blueprinter](https://github.com/nikkorap/NOBlueprinter-Releases) 2.1.1 or newer. Use one or the other: the OpenPlane
  app sees a copy NOMM installed and leaves it to NOMM.

## For players

1. Open the OpenPlane app. On first run it finds Nuclear Option and offers to install the OpenPlane mod
   (it installs BepInEx 5 and Blueprinter if you don't have them, or works alongside NOMM if you use it). A "Getting
   started" checklist on My planes walks you from there to your first flight and your first shared plane.
   No Flyout planes? **Add starter planes** gives you three ready-made ones (the OP-1 Wren trainer, OP-2 Kite fighter
   and OP-3 Heron bomber). They are the same for everyone, so they also match in multiplayer.
2. **Convert:** pick Flyout planes and press Convert. Each one gets a short report of what changed. Converting a craft
   again makes a new plane, unless you tick "Replace": then the plane converted from it before moves to OpenPlane's
   "Deleted planes" folder (you can bring it back). Planes you built are never replaced.
3. **Build:** start from a template or a blank plane, shape it in 3D, and watch the weight, balance and stability
   readouts. Press **Test fly** to save and start Nuclear Option straight into Free Flight on Heartland, already flying
   your plane (in the air over an airbase, or in a hangar: choose in Settings). Unsaved work is kept as a draft for each plane, listed
   under My planes and Build, so you can continue it later.
4. Your planes appear in the hangars that offer their base plane (for example, jets where the FS-12 Revoker is
   offered). The base plane is picked by size: the stock plane closest in length and weight to yours.
5. **Share:** export a plane as one `.oplane` file and send it to friends; they import it with the app (drop it on
   OpenPlane, use Import, or double-click it once "Double-clicking a .oplane file opens it in OpenPlane" is ticked in
   Settings). Tick several planes on the Share screen to send them as one **plane pack** (a `.zip`). Importing a plane
   you already have changes nothing; a plane pack also updates older versions of the same planes (the old ones go to
   "Deleted planes"). Liveries and missions for OpenPlane planes can go on the Steam Workshop through Nuclear Option's
   own uploader, tagged "Mod Required". Uploading plane files themselves to the Workshop will come once the game's
   developers agree.

Nuclear Option loads planes when it starts: after saving or importing a plane, restart the game to fly it.

### The builder

The builder works like a 3D modelling program (Blender's keys, if you know them), but every action also has a labelled
button, so a mouse or the Steam Deck's trackpads are enough. Press `?` in the builder for the full list.

- **Select:** click a part, Shift+click to add, drag on empty space for a box. The outliner on the right lists every
  part with eye (hide) and lock buttons; double-click a name to rename it.
- **Move, rotate, scale:** G, R and S (or the tools on the left). While moving, X, Y or Z locks an axis, typing a number
  sets an exact value, Ctrl snaps to the grid, Enter or a click confirms, Esc or a right-click cancels.
- **Edit mode (Tab):** reshape a fuselage ring by ring or vertex by vertex (extrude a ring with E, add one with Ctrl+R,
  proportional editing with O, cross-section presets), edit a shape's vertices, edges and faces (extrude, inset, loop
  cut, merge, subdivide), or drag a wing's corners and set its sweep, dihedral and twist. Edits mirror across the
  centre line while X mirror is on.
- **Shapes:** boxes, cylinders, spheres, cones, capsules and glass canopies, or an imported `.obj` model. They count as
  part of the body for weight and drag. A fixed body from a converted plane can be made editable too.
- **Reference images:** add a top, side or front picture (or drop it on the builder), set its scale by clicking two
  points a known distance apart, and trace over it. Pictures are saved with the plane but left out when you share it,
  unless you tick "Include reference images".
- **Views:** Top, Front, Side and 3D buttons (number pad 7, 1, 3), perspective or orthographic, quad view, X-ray,
  wireframe and a measuring box. Middle-drag or Alt+drag orbits, Shift+middle-drag or right-drag pans, the wheel zooms.
- **Flight numbers** (weight, balance, neutral point, stability margin, thrust to weight, stall speed) update as you
  work; the flight check offers one-click fixes such as moving the wing or placing landing gear. Its **Takeoff** panel
  shows the run the plane needs against every Heartland runway, whether its elevator can lift the nose, and fixes
  for both (a bigger elevator, more thrust).
- **Paint:** one-click schemes (air-superiority grey, navy, desert, olive, arctic, bare metal, trainer red and more),
  or any colour, finish and glass of your own.

In the game your plane looks as you made it: its own colours, finishes and glass (or the stock plane's livery, if you
tick "Use the stock livery"), its cockpit interior, working instruments (gauges, horizon and compass read the real
plane), its lights (strobes and beacons blink), and you fly from its own cockpit, at the eye position you set.
Converted Flyout planes keep their colours, glass, cut-outs, shape parts, instruments and lights; OpenPlane draws its own
instruments and cockpit seat in place of Flyout's models.

Weapons hang where you put hardpoints: the stock plane's weapon stations move onto them, stations with no hardpoint
aren't offered (a plane without hardpoints carries only its guns), and stores too heavy for your plane's weight aren't
offered either. The stock guns fire from your guns' positions.

The stock plane's landing-gear legs stand under your wheels: moved sideways, and the nose leg also along the plane; the
wheels keep the stock plane's size and height.

Some settings are saved with the plane but the OpenPlane mod doesn't use them in the game yet: control-surface travel
and input gains, the angle-of-attack limit, wheel size and height, gear steering, brakes, retraction and travel, which
weapon kinds a station allows, gun calibre and rate of fire, and sensors (the stock plane's are used). The builder marks
them "not in game yet".

Known limitation: an OpenPlane plane keeps its stock plane's hit boxes and damage parts, so in combat it is hit where
the stock plane's wings, tail and engines would be, not exactly where its own shape is.

### Multiplayer

- **Everyone in a game needs the same OpenPlane planes**, because the game sends planes over the network by number.
  OpenPlane adds a fingerprint of your planes to the game version, so the server browser only shows lobbies where
  everyone has the same set. Joining by IP address (direct connect) skips that check, so make sure everyone has the
  same planes before joining that way.
- **Getting the same planes:** on the Share screen, "Playing together" shows your plane set (as of the game's last
  start) and **Share all as a pack** saves every plane in one `.zip`. Friends drop it on OpenPlane and restart the game.
- **Host settings** (Settings, "Multiplayer: games you host"): a host can switch OpenPlane planes off, or keep out
  planes with more thrust for their weight or more weapon hardpoints than a limit. They apply to the games your computer
  runs (single-player, a lobby you host, or a dedicated server started from that game folder): the planes are hidden
  in every hangar for everyone, using the game's own mission restrictions. A mission that bans a stock plane also bans
  the OpenPlane planes built on it.
- The host's weapon check counts what an OpenPlane plane can really carry, so players aren't charged for, or flagged
  over, weapons that don't fit.

## When something goes wrong

Settings, "Help and problems", answers the common questions (a plane missing in the game, no lobbies, SmartScreen, a
blank window) and has **Copy a problem report**: versions, what OpenPlane found, what the game did with each plane and the
mod's log lines, with your Windows user name and folders replaced so it's safe to post. Paste it into a GitHub issue.

## How it works

- **The OpenPlane app** is the only thing you open. It converts and builds planes, and installs the mod for you.
- **The OpenPlane mod** runs inside Nuclear Option. It is a [Blueprinter](https://github.com/nikkorap/NOBlueprinter-Releases)
  add-on (BepInEx 5) and needs Blueprinter 2.1.1 or newer. At startup it loads every `.oplane` file from the game's
  planes folder, and from the planes folder you chose in the app, if you chose one (the app writes it into the mod's
  settings as `ExtraAircraftFolder`). The game's planes folder is
  `%USERPROFILE%\AppData\LocalLow\Shockfront\NuclearOption\OpenPlane\Aircraft` on Windows and
  `~/.local/share/Steam/steamapps/compatdata/2168680/pfx/drive_c/users/steamuser/AppData/LocalLow/Shockfront/NuclearOption/OpenPlane/Aircraft`
  under Proton. Each plane is built on a copy of a stock airframe: the stock cockpit, systems, gear and weapons stay;
  the outer shape is replaced by the design's, and every lifting part, the thrust, weight, fuel and balance are retuned
  to match the design. Each plane is known in the game by its plane id, so a shared plane is the same plane on every
  computer.
- **OpenPlane.Core** is the shared code: the [`.oplane` format](docs/FORMAT.md), the Flyout converter, flight
  estimates and the three-view preview.

## Repository layout

| Path | What |
|---|---|
| `src/OpenPlane.Core` | Shared library (.NET Standard 2.1) |
| `src/OpenPlane.App` | The desktop app |
| `src/OpenPlane.Cli` | Command-line tool: `openplane convert <Flyout craft folder> -o plane.oplane`, `openplane inspect plane.oplane` |
| `src/OpenPlane.Mod` | The Nuclear Option mod (BepInEx 5 plugin, Blueprinter add-on) |
| `tests/` | Tests |
| `docs/FORMAT.md` | The `.oplane` file format |
| `licenses/` | Licence texts the release ships (the rest are copied from the packages by `tools/package.ps1`) |
| `tools/package.ps1` | Builds the release zips |

## Building

You need the .NET 10 SDK. Building the mod also needs references from your own copy of Nuclear Option:
copy `Assembly-CSharp.dll`, `Mirage.dll`, `UniTask.dll` and `Newtonsoft.Json.dll` from
`<game>\NuclearOption_Data\Managed` into `lib/game`, and the Blueprinter DLL into `lib/blueprinter`.
These files belong to their owners and are never committed.

```
dotnet build OpenPlane.sln -c Release
dotnet test
```

The version is set once, in `Directory.Build.props`. `tools/package.ps1` builds the release zips into `out/release`; it
refuses to make them if a shipped file would contain debug symbols or a path from your computer, or if a licence text
is missing.

Run the app from the build output: `src/OpenPlane.App/bin/Release/net10.0/OpenPlane.exe` opens the window.
`OpenPlane.exe --serve --port 5173` serves the same page to your browser instead (handy for development).
Other options (they override Settings for that session only): `--game-dir`, `--planes-dir`, `--flyout-dir` (repeat for
more than one), `--data-dir` (settings, drafts, deleted planes), `--downloads-dir`, `--mod-dir` (the folder with the
mod files to install; default: `mod` next to the app), and `--no-external` (don't start Steam or open folders; log
those actions instead). `--help` lists them. Without the game's DLLs in `lib/game` the app still builds, but it has no
mod files to install and Settings says so.

Options in `BepInEx/config/com.meta.openplane.cfg`: `[Multiplayer]` `AllowOpenPlanePlanes`, `MaxThrustToWeight` and
`MaxHardpoints` (host settings, 0 = no limit; the app's Settings screen sets them), `[Paths]` `ExtraAircraftFolder` (a
second planes folder; the app sets it), and for developers `SelfTest` (spawns every OpenPlane plane in a running
single-player mission and logs how it flies) and `DumpPrefabs` (writes a stock plane's structure to a text file).

Files can be passed on the command line to import them: `OpenPlane.exe "Kite.oplane" "Host planes.zip"`. When an
OpenPlane window is already open, a second start hands the files to it.

## Credits and notes

- Made by Meta. Built with AI assistance (Claude), as the Nuclear Option and Flyout modding communities ask creators to disclose.
- Not affiliated with Shockfront Studios (Nuclear Option) or Stonext Games (Flyout). OpenPlane ships no game files.
- Converted Flyout planes belong to the people who built them. OpenPlane keeps the original builder's name in every
  converted plane; please credit them when you share.
- Third-party software: see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Licence

MIT. See [LICENSE](LICENSE).
