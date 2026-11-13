# The .oplane file format (version 1)

An `.oplane` file is a zip archive holding one plane. Any tool may read or write it; this page is the contract.

| Entry | Required | What |
|---|---|---|
| `aircraft.json` | yes | The design (below). |
| `meshes.json` | no | Fuselage meshes, a JSON array of `{ name, vertices, triangles, triangle_materials }`. |
| `report.json` | no | What changed when the plane was converted (`{ items: [{ level, message }] }`). |
| `thumb.png` | no | A preview picture. |
| `builder.json` | no | The OpenPlane app's editable version of the plane (`"format": "openplane-builder/2"`, see [builder.json](#builderjson)), so it reopens in the builder exactly as it was made. It records a hash of `aircraft.json`; if another tool changes `aircraft.json`, the app ignores `builder.json` and rebuilds its editable version from the design. The game mod never reads it. |
| `references/<sha1>.png` (or `.jpg`, `.webp`) | no | Blueprint pictures the builder shows behind the plane, listed in `builder.json`. Builder-only; the app leaves them out when sharing unless asked to keep them. |

All JSON uses `snake_case` keys. Units are metres, degrees, newtons, kilograms and seconds. Axes follow Unity:
x right, y up, z forward. Points are `[x, y, z]` arrays in the plane's own space; rotations are `[x, y, z, w]` quaternions.

## aircraft.json

```jsonc
{
  "format": "openplane/1",           // required; readers accept "openplane/1" and "openplane/1.x" and reject the rest
  "id": "3f9c0a7e5b1d2468",           // optional: the plane's stable id (see "Identity" below)
  "name": "Kestrel Mk2",
  "author": "Meta",
  "description": "optional text",
  "origin": {                         // optional: where it came from; keep the original builder's credit
    "source": "flyout",               // "flyout" or "openplane"
    "craft_name": "Kestrel", "builder": "someone", "source_version": "0.2504", "converted_with": "OpenPlane 0.1.0"
  },
  "donor": "Multirole1",              // stock Nuclear Option plane whose cockpit, systems and weapons are reused
  "wings": [{
    "name": "Wing 12", "root_leading": [..], "root_trailing": [..], "tip_leading": [..], "tip_trailing": [..],
    "up": [0, 1, 0],                  // lift direction (unit vector)
    "root_twist_deg": 0, "tip_twist_deg": 0,   // informational: the corners already include any twist
    "thickness_ratio": 0.1, "fuel_tank": false,
    "material": 2,                    // optional: palette index; absent = the plane's paint (surfaces too)
    "surfaces": [{
      "role": "aileron",              // aileron, elevator, rudder, flap, elevon, ruddervator, flaperon, airbrake, slat, fixed, other
      "edge": "trailing",             // or "leading"
      "root_leading": [..], "root_trailing": [..], "tip_leading": [..], "tip_trailing": [..],
      "min_deg": -20, "max_deg": 20, "servo_deg_per_sec": 90,
      "inputs": [{ "axis": "roll", "gain": 20 }]   // pitch, roll, yaw, flaps, brake, throttle, gear
    }],
    "inputs": [{ "axis": "pitch", "gain": 20 }],  // optional: the whole panel turns (all-moving tail, stabilator, turning fin)
    "min_deg": -20, "max_deg": 20,     // its travel; these are left out for a fixed panel
    "servo_deg_per_sec": 60            // optional: how fast it turns (0 or absent: the stock plane's speed)
  }],
  "fuselages": [{ "name": "Fuselage 1", "mesh_index": 0, "bounds_min": [..], "bounds_max": [..],
                  "frontal_area": 1.2, "wetted_area": 30.5, "skin_thickness": 0.006 }],
  "engines": [{ "type": "turbofan", "title": "KRT-100", "position": [..], "thrust_direction": [0, 0, 1],
                "diameter_m": 1.08, "max_thrust_n": 63000, "afterburner_thrust_n": 97700,
                "power_kw": null, "prop_diameter_m": null, "estimated": true }],
  "gear": [{ "position": [..], "strut_length": 0.36, "travel": 0.17, "wheel_radius": 0.2,
             "steerable": true, "brakes": true, "retractable": true }],
  "hardpoints": [{ "position": [..], "rotation": [0, 0, 0, 1], "original_payload": "missile",
                   "allows": ["missile-ir", "missile-radar", "missile-ground", "bomb", "rockets"], "internal": false }],
  "guns": [{ "position": [..], "direction": [0, 0, 1], "caliber_mm": 27, "rounds_per_minute": 1730,
             "muzzle_velocity": 1115, "barrels": 1 }],
  "cockpit": { "position": [..],
               "eye": [..],                 // optional: the pilot's eye; default 0.7 m above position
               "own_view": true },           // optional: fly from this cockpit in the game (default true)
  "sensors": { "radar": true, "irst": false, "targeting_pod": false, "radar_warning": true, "countermeasures": true, "jammer": false },
  "handling": { "g_limit": 9, "aoa_limit_deg": 25 },   // 0 means "use the donor's value"
  "mass": { "empty_kg": 0, "fuel_kg": 0, "center_of_mass": null },   // 0/null means "estimate it"
  "paint": { "base_color": [0.6, 0.62, 0.64, 1], "metallic": 0, "smoothness": 0.3,
             "stock_livery": false },      // optional: true = the stock plane's livery in the game instead of these colours
  "materials": [{                     // optional palette; meshes, panels and surfaces point at it by index
    "id": "m0", "name": "Paint", "kind": "paint",   // "paint", "glass" (see-through: alpha is opacity) or "light" (glows)
    "color": [0.7, 0.7, 0.7, 1], "metallic": 1, "smoothness": 0.15, "finish": null }],   // finish: an original textured finish, drawn as colour
  "instruments": [{                   // optional: cockpit instruments OpenPlane draws and animates
    "name": "Speed", "kind": "gauge", // gauge, attitude, compass, screen, hud, mirror, lever
    "reads": "airspeed",               // gauges: airspeed (km/h), altitude (m), radar-altitude, vertical-speed (m/s), rpm (%), throttle, fuel (0-1), g, aoa (deg), heading, clock;
                                       // screens: the live page they show (flight, engine or nav; absent: a plain display);
                                       // levers: what moves them (throttle, gear or stick; absent: they stay still)
    "position": [..], "rotation": [x, y, z, w],   // the face looks along rotation applied to +y; 12 o'clock is rotation applied to +z
    "size": 0.08, "min": 0, "max": 800, "min_deg": 30, "max_deg": 330 }],   // needle angles clockwise from 12 o'clock
  "lights": [{ "name": "Left wingtip", "kind": "nav",   // nav, strobe, beacon (blinking), landing, strip
               "position": [..], "rotation": [0, 0, 0, 1], "color": [1, 0, 0, 1], "size": 0.12 }],
  "stats": { ... }                    // derived numbers written for convenience; never authoritative
}
```

Engine `type` is one of `turbofan`, `turbojet`, `piston-prop`, `turboprop`, `electric-prop`, `rotor`, `unknown`.

- **Twist.** A panel's four corners are its real shape: any twist is already in them. `root_twist_deg` and
  `tip_twist_deg` are informational and nothing applies them again. OpenPlane's builder writes the twist it built into
  the corners; converted planes keep Flyout's own twist numbers there, which Flyout stores apart from the corners (its
  unit isn't confirmed), so converted wings are untwisted.
- **Stats** are recomputed by every reader from the shape; a file's `stats` are never used.
- **In the OpenPlane 0.1 mod**, a plane is lined up with its stock plane by centre of gravity. The stock plane's
  landing-gear legs move under the `gear` wheels (across, and the nose leg also along the plane) and steer and brake as
  `steerable` and `brakes` say; wheel size, strut length and retraction stay the stock plane's. The stock weapon
  stations move onto the `hardpoints` (stations with no hardpoint aren't offered), and the stock guns fire from `guns`
  positions at their `rounds_per_minute`. The stock control surfaces take the travel (`min_deg`, `max_deg`, input
  `gain`s) and `servo_deg_per_sec` of the design's surfaces and all-moving panels riding on them, within 0.4 to 2 times
  the stock travel; `handling.g_limit` and `handling.aoa_limit_deg` set the fly-by-wire's limits; engines give
  `max_thrust_n` dry and `afterburner_thrust_n` with afterburner. The cockpit eye sets the game's cockpit view (unless
  `own_view` is false), and the plane's colours, glass, interior, instruments and lights are drawn (unless
  `paint.stock_livery` is true).
- Files without `materials`, `instruments`, `lights`, `eye` or `stock_livery` are read exactly as before: those
  fields are left out of a file when unused.

## Identity

`id` names the plane: tools make a new one when a plane is created (a new plane, a conversion, a copy) and keep it when
the plane is edited, renamed, shared or imported. The game mod calls a plane `op.<id>` (lower case; letters, digits,
`-` and `_`; at most 48 characters). A file without an `id` is called `op.h<16 hex digits>`, from the SHA-256 of its
unzipped `aircraft.json` and `meshes.json` (each preceded by its length as 8 bytes, little-endian), so the same file
gets the same name on every computer, however it was zipped. Two files with the same id are the same plane: the mod
loads the first (by file name) and logs a warning.

## Checks and limits

Readers refuse a file (with a message saying why) when:

- a number isn't finite (`NaN`, `Infinity`), a point hasn't exactly 3 numbers or a rotation exactly 4;
- a fuselage's `mesh_index` points past `meshes.json`, a mesh's `vertices` or `triangles` length isn't a multiple of 3,
  or a triangle points at a corner that doesn't exist;
- the zip has more than 512 entries, or an entry is bigger than its limit: `aircraft.json` 32 MB, `meshes.json`
  128 MB, `builder.json` 64 MB, `report.json` and `thumb.png` 8 MB, each reference picture 64 MB (at most 32 of them,
  256 MB in all; extra or larger pictures are left out). Sizes are checked while unpacking, not only as the zip
  declares them;
- the plane has more than 2,000,000 triangles or 2,000,000 corners in all.

A `triangle_materials` list of the wrong length is read as all skin. The builder keeps planes it saves within these
limits (a shape is smoothed less if smoothing would take it over 500,000 triangles).

## Meshes

`vertices` is a flat array `x, y, z, x, y, z, ...` in plane space. `triangles` holds three vertex indices per triangle,
wound so normals point outwards. `triangle_materials` has one material slot per triangle, and the optional `materials`
list maps each slot to a palette index (`-1` or out of range is the plane's paint). Without that list (older files),
slot 0 is the paint and any other slot is glass. `role` is `"interior"` for cockpit interior pieces (seats, consoles):
the game shows them from the cockpit and the flight estimates leave them out of drag and weight.

## builder.json

The builder's own description of the plane: the parts a player edits. `aircraft.json` and `meshes.json` are always
made from it by OpenPlane.Core (`DesignGenerator`), so other tools only need `aircraft.json`.

```jsonc
{
  "format": "openplane-builder/2",
  "id": "3f9c0a7e5b1d2468",           // the same id as aircraft.json (the app keeps them equal)
  "name": "Kestrel", "author": "Meta", "description": null, "origin": null, "donor": "auto",
  "fuselages": [{                     // "loft": rings; "mesh": a fixed shape kept from a converted plane (meshes.json)
    "id": "fus1", "name": "Fuselage", "kind": "loft", "position": [0, 0, 0], "mirror": false, "sides": 24,
    "rotation_deg": [0, 0, 0],        // Euler degrees in Unity's order: about z, then x, then y (q = qy * qx * qz)
    "smooth": 0,                      // 0-3: Catmull-Rom rings added along the body (2^level - 1 in every gap)
    "rings": [{ "z": 4.2, "y": 0, "width": 0.8, "height": 0.8, "roundness": 1,
                "points": null }],    // optional cross-section: [dx, dy, dz] per point from (0, y, z), top first, then towards +x
    "mesh_index": 0, "scale": 1       // mesh kind: index into meshes.json; scaled and turned about the mesh's bounds centre
  }],
  "shapes": [{                        // free-form body parts: canopies, intakes, pods, spines, fairings
    "id": "shape-ab12cd", "name": "Canopy", "position": [0, 0.8, 2], "rotation_deg": [0, 0, 0], "scale": [1, 1, 1],
    "mirror": false, "material": "glass", "subdivision": 1,           // Catmull-Clark levels 0-3, applied when saved too
    "material_id": "m1", "interior": false,                           // optional: palette entry (wins over "material"); interior piece
    "vertices": [[0.1, 0.2, 0.3], ...], "faces": [[0, 1, 2, 3], ...], // polygons, wound so cross(b - a, c - a) points outwards
    "source": "capsule"               // box, cylinder, sphere, cone, capsule, obj or mesh
  }],
  "wings": [{ "kind": "wing", "root": [0.5, 0, 1], "span": 4, "root_chord": 2, "tip_chord": 1, "sweep_deg": 10,
              "dihedral_deg": 3, "incidence_deg": 1, "twist_deg": 0, "vertical": false, "mirror": true,
              "moving": { "type": "ruddervator", "min_deg": -25, "max_deg": 25, "inputs": null },   // optional: the whole panel turns, like a surface of this type
              "surfaces": [{ "type": "aileron", "edge": "trailing", "from_pct": 58, "to_pct": 95, "chord_pct": 25,
                             "min_deg": null, "max_deg": null, "servo_deg_per_sec": null,
                             "inputs": null }] }],   // null = the type's defaults; input gains are for the +x side
  "engines": [{ "type": "jet", "variant": null, "sizing": "thrust", "thrust_kn": 45, "afterburner_kn": 0,
                "diameter_m": 0.9, "turbine_temp_k": 1400, "afterburner": false, "power_kw": 600, "prop_diameter_m": 2.4 }],
  "gear": [], "hardpoints": [], "guns": [], "cockpit": { "position": [0, 0.7, 3] },
  "sensors": {}, "handling": {}, "mass": {}, "paint": {},
  "references": [{ "id": "ref1", "name": "Side view", "image": "<sha1>.png", "view": "side",   // top, side or front
                   "center": [0, 0], "width_m": 14, "rotation_deg": 0, "opacity": 0.5, "visible": true, "locked": false }],
  "editor": { "hidden": ["shape:shape-ab12cd"], "locked": [] },   // the builder view's own state; never changes the design
  "design_hash": "<sha1 of aircraft.json>"
}
```

- `center` of a reference is in the view's plane, in metres: top view (x, z), side view (z, y), front view (x, y). Its
  height follows the picture's shape.
- Shapes become fuselage meshes in `meshes.json` (after the lofted and fixed fuselages), each with its own `fuselages`
  entry, so the game draws them and the flight estimates count them as body (mass, wetted and frontal area). A
  mirrored shape gets a twin "<name> (left)" unless it is its own mirror image. Glass is drawn as skin in the game for now.
- Surface types: aileron, elevator, rudder, flap, elevon, flaperon, ruddervator, airbrake, slat (always on the leading
  edge) and none. Leading-edge strips measure their chord from the leading edge.
- Engine `variant`: turbojet, turbofan, ramjet (saved as a turbojet; the game has no ramjet), piston or electric
  (propellers). With `"sizing": "designed"` a jet's dry thrust is 0.1697 · A · (turbine_temp_k − 300)^1.766 N, A being
  the area of its diameter; an afterburner adds 55 %.
- Converted planes' bodies stay fixed `"mesh"` fuselages; the builder can turn one into an editable shape.
- `.obj` models imported as shapes: the plane's (x, y, z) is the file's (z, y, x) (nose along the file's +x, y up, as
  Flyout reference models are drawn), models longer than 60 m are shrunk by tens, and the result is centred.
- Version 1 files (`openplane-builder/1`) open as version 2: they have no shapes, references or editor state, and every
  other new field defaults to version 1's behaviour, so they regenerate exactly the same `aircraft.json`. Files of a
  newer builder version open as fixed shapes.

## Compatibility

Readers must ignore keys they don't know. Writers must not change the meaning of existing keys within `openplane/1`.

## Plane packs

A plane pack is an ordinary `.zip` holding `.oplane` files (in any folders; other files are ignored). OpenPlane's Share
screen makes one with the chosen planes, reference pictures left out, each plane stored uncompressed (it is a zip
already). Importing a pack imports every plane in it: a plane already present with the same `id` and the same contents
is left alone, and one with the same `id` but other contents is moved to "Deleted planes" and replaced, so every player
who imports a host's pack ends up with the host's version. A `.zip` with no `.oplane` in it is treated as a zipped
Flyout craft folder instead.
