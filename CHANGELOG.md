# Changelog

## 0.1.1

**A fourth starter plane: OP-4 Lanner**
- A single-seat, twin-jet multirole strike fighter built on the KR-67 Ifrit: chined forebody with a sunken cockpit,
  bubble canopy, twin engine nacelles, leading-edge root extensions, a 42° wing with slats, flaperons and ailerons, a
  40° all-moving V-tail, two afterburning turbofans, a 30 mm cannon, four internal bays and four wing pylons.
- A full cockpit: ejection seat, consoles, twin throttles, side-stick, instrument panel and glare shield, head-up
  display, three live displays, a full set of gauges and standby instruments, and mirrors; and a full set of lights.
- Also in the builder as the "Strike fighter" template.

**Flies as you set it up**
- Control surfaces move as far and as fast as set in the builder (on the stock surfaces they ride on, within 0.4 to 2
  times the stock travel, so the game's autopilot stays in tune).
- Wings and tails can move as a whole: stabilators, tailerons, all-moving V-tails and turning fins ("Moves as a whole").
- The fly-by-wire keeps to the plane's g limit and angle-of-attack limit.
- Afterburners give the plane's own afterburner thrust (they gave the stock plane's before). A plane without
  afterburners still gets the stock plane's for now (on their real thrust alone some converted WW2 jets can't reach
  takeoff speed on the game's runways).
- Each landing-gear leg of a plane built in OpenPlane steers and brakes as set in the builder (converted Flyout planes
  keep the stock plane's steering and brakes).
- Guns fire at the plane's own rate of fire; guns built into the stock plane's body now fire from the plane's own gun
  position too.

**Cockpits**
- Screens can show a live page: flight (attitude, speed, altitude, heading, g, angle of attack), engine (engine speed,
  throttle, fuel) or navigation (heading, height above the ground, climb rate).
- Levers can move with the throttle, the landing gear or the stick.

**Fixes**
- A plane a little lighter than its stock plane no longer loses the stock plane's heaviest stores (now only planes more
  than 10 % lighter carry lighter stores).
- A stock plane's cannon that is a loadout choice (the KR-67 Ifrit's 27 mm) now sits at the plane's own gun instead of
  being hung on a wing pylon, where it also left the wing pylons paired wrongly; a plane without a gun doesn't get it.
- Engine-speed gauges read jet engines (they showed 0 % on every jet).
- Moving control surfaces: ailerons, flaps, elevators, rudders and all-moving tails now visibly move in the game.

**Known limits (early access)**
- Tested in game with 16 planes: all fly from an air start, and 15 take off from a runway with AI pilots (the
  converted Batwing tipped over taxiing at the desert airbase; it takes off from the city airbase). The OP-4 Lanner and
  the KR-67 Ifrit land, taxi and park with AI pilots; one converted twin-body plane still crashed a landing.
  Multiplayer hasn't been tested with two players yet.
- The OP-4 Lanner's weapons were tested against planes, vehicles and radars: 13 of its 21 weapons destroyed targets
  (every air-to-air missile, the guided missiles and bombs, rockets and cluster bombs). The cannon fires but made no
  kill in the test, and AI pilots don't use nuclear weapons on single targets.
- A plane keeps its stock plane's hit boxes, sensors and gear wheels; gun calibre and gear retraction are saved but not
  used in the game yet (the builder marks them).

## 0.1.0 (first release)

Tested with Nuclear Option 0.34.6, BepInEx 5.4.23.4 and Blueprinter 2.1.1.

**First steps**
- A "Getting started" checklist on My planes: find the game, install the mod, get a plane, fly it, share it.
- Three starter planes in one click (OP-1 Wren, OP-2 Kite, OP-3 Heron, with OpenPlane's red tail), identical for
  everyone; also a separate download for players without the app.

**Convert**
- Converts Flyout craft (save versions 0.2323 to 0.2504) into Nuclear Option planes: exact wing panels and control
  surfaces, exact fuselage shapes, colours, glass, cut-outs, shape parts, lights, cockpit instruments and the eye point.
- Engines, guns, hardpoints and landing gear are swapped for Nuclear Option's own, placed where the craft had them; the
  plane's weight and balance are estimated from its parts.
- Every conversion comes with a report of what carried over, what was approximated and what was left out, plus a
  flight check with one-click fixes.

**Build**
- A 3D builder that works like a modelling program (Blender's keys) but has a labelled button for everything:
  templates (trainer, fighter, bomber), fuselages edited ring by ring, wings by their corners, free-form shape parts,
  `.obj` import, reference pictures, mirror editing, quad view, X-ray, measuring, undo.
- Live weight, balance, stability, thrust-to-weight, stall speed and runway estimates, with a Takeoff panel (the
  run against every Heartland runway, nose lift, one-click fixes).
- One-click paint schemes.
- Test fly saves the plane and opens Nuclear Option straight into Free Flight on Heartland, flying it (in the air or
  from a hangar, set in Settings).

**In the game (the OpenPlane mod)**
- Each plane is built on a stock airframe (picked by size) with its own shape, lift, drag, thrust, weight, fuel and
  balance, its own colours and cockpit, weapons on its hardpoints (no more than it can carry) and landing-gear legs
  under its wheels. It is offered in the hangars that offer its stock plane, at airbases with a long enough runway.
- It looks like your design, not its stock plane: stock canopies, cockpit frames and gun barrels that would show outside
  its shape are hidden, its propellers are drawn where you placed them and turn with the engine (with the game's own
  propeller blur), jet engines show their casing and nozzle as in the builder, and the landing gear disappears into the
  plane once it is stowed.
- AI pilots taxi it to the runway and take off (the game's own taxi check gave up on planes that lean a few degrees in
  a turn).
- Planes are known by a stable id, so a shared plane is the same plane on every computer.

**Share and play together**
- Share one plane as a `.oplane` file, or several as a plane pack (`.zip`); import by dropping files on OpenPlane, with
  Import, or by double-clicking (turn it on in Settings). Importing a plane you already have changes nothing; a pack
  switches your copies of its planes to the pack's version (yours go to Deleted planes, and you're told if yours were
  newer), and a damaged plane in a pack is reported without stopping the rest.
- Every plane has its own Workshop tag, so liveries and missions for it upload with the game's own Workshop uploader.
- Multiplayer: lobbies only match players with the same planes; "Playing together" shows your plane set and sends it
  all as one pack; hosts can switch OpenPlane planes off or limit thrust-to-weight and hardpoints; the host's weapon
  check counts what each plane can really carry.

**Help**
- Settings has answers to common problems and a problem report to copy into a bug report (personal folders removed).

**Known limits (early access)**
- Tested in game with 15 planes: all fly from an air start and take off from a runway with AI pilots. AI landings
  aren't tested yet (one converted twin-body plane crashed a landing), and multiplayer hasn't been tested with two
  players yet. Please report what you find.
- The app is for Windows; on Steam Deck and Linux use the mod download (see the README).
- A plane keeps its stock plane's hit boxes, sensors and gear wheels; control-surface travel, the angle-of-attack
  limit, gear steering and brakes, and gun calibre are saved but not used in the game yet (the builder marks them).
- Uploading plane files themselves to the Workshop waits for Shockfront's approval.
