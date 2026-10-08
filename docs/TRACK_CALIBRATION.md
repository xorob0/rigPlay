# Track calibration (fake GPS strategy C)

With the position strategy **Real track** (page "Data to CarPlay", issue #44) the rigPlay plugin puts the
car on the real circuit, so the phone's map shows it driving round Spa or Monza instead of round an
arbitrary origin. Sims publish no GPS position, so every track needs a **calibration**: where the
circuit is, and how the game's track maps onto it.

## Where the position comes from

Per track (the key is SimHub's `TrackCode`, else the track name, lower-cased with accents and
punctuation removed, e.g. `spa gp grand prix pits` for iRacing at Spa), in this order:

1. **Recorded centreline.** If you recorded a lap of this track and the game publishes the lap
   fraction (`TrackPositionPercent`: iRacing, ACC, AC, rF2, LMU and most others), the position is
   interpolated along the recorded lap. Works in every game, iRacing included.
2. **World coordinates.** If the game publishes `CarCoordinates`, the position is
   `origin + rotation · scale · (x, z)`, using the track's origin, rotation, scale and axes. The
   coordinates count once they have moved 5 m in the session, so a game that publishes zeros or a
   frozen point stays on dead reckoning.
3. **Dead reckoning from the origin.** Otherwise the car starts at the track's origin and drives with
   the sim's speed and heading (like "Drive around the origin"), the heading turned by the track's
   rotation. Good enough to see the car at the circuit, and the input for recording a lap.

The origin comes from your calibration when there is one, else from the shipped table (below), else it
is the page's origin.

## Calibrating a track in 5 steps

1. Choose **Real track** as the position strategy and start a session in the game. The *Track* line
   shows the track key, where the calibration comes from (*none*, *shipped*, *yours*, *yours, with a
   recorded lap*), how the car is placed, and whether the game publishes a lap position and world
   coordinates.
2. **Origin.** If the source is *shipped*, the origin is already near the start/finish line. Otherwise
   (or to correct it) stop the car exactly on the start/finish line, press **Set origin to here**, then
   paste the line's real coordinates (`lat, lon`, right-click in Google Maps or OpenStreetMap) into the
   *Track origin* box. With world coordinates this also pins the car's spot to the origin.
3. **Rotation.** Drive down the main straight and compare the direction on the phone's map with the real
   one; type the difference (degrees clockwise) into *Rotation*. If the track comes out mirrored, pick
   another entry in the axes list. *Scale* stays 1 for games that work in metres.
4. **Record a lap.** Press **Start recording** and drive one clean lap. Recording starts when you
   cross the start/finish line and ends at the next crossing; it samples the position every 0.5 % of
   the lap (about 200 points) and closes the loop by spreading any dead-reckoning drift over the lap.
   **Stop** ends early (kept when at least 90 % of the lap was recorded). A reset or shortcut (a jump of
   more than 10 % of the lap) cancels the recording. Keep the page open until the line says *Saved*.
5. The source becomes *yours, with a recorded lap* and the car follows that lap from now on. **Forget
   this track** deletes your calibration (the shipped table applies again).

Calibrations are saved in SimHub's settings file (`RigPlay.RigPlaySettings.json`, `Telemetry.Tracks`)
and always override the shipped table.

A recorded lap is only as good as its input. Recorded from dead reckoning (iRacing), the shape comes
from the sim's speed and heading: start and end meet, but long straights and corners can be slightly
off, and the whole lap is turned by the rotation you set before recording. Set the origin and rotation
first, then record.

## The shipped table is approximate

`plugin/RigPlay/Resources/tracks.json` (embedded in `RigPlay.dll`, `"approximate": true`) holds only the
start/finish-line coordinates of eight circuits, to 4 decimals, each with our own uncertainty estimate.
They come from OpenStreetMap features tagged as the start/finish line and from the first point of the
community [f1-circuits](https://github.com/bacinger/f1-circuits) GeoJSON, cross-checked against the
pit buildings where possible. They are good for "the car is at the circuit", not for lane-level
accuracy, and they carry no rotation, so a track is rotated arbitrarily until you set one.

| Circuit | Start/finish (lat, lon) | Uncertainty |
|---|---|---|
| Circuit de Spa-Francorchamps | 50.4443, 5.9650 | ±150 m |
| Nürburgring GP-Strecke | 50.3356, 6.9477 | ±250 m |
| Silverstone (Grand Prix, Wing pits) | 52.0693, −1.0222 | ±100 m |
| Autodromo Nazionale Monza | 45.6190, 9.2812 | ±150 m |
| Circuit de la Sarthe (Le Mans) | 47.9499, 0.2075 | ±100 m |
| Suzuka | 34.8433, 136.5403 | ±150 m |
| WeatherTech Raceway Laguna Seca | 36.5865, −121.7566 | ±150 m |
| Watkins Glen | 42.3411, −76.9288 | ±300 m |

A track key matches an entry when one of its aliases appears in the key as whole words (`spa` matches
`spa gp` and `circuit de spa francorchamps`), unless an excluded word does (the Nürburgring entry is the
GP circuit and skips `nordschleife` and `combined`; Silverstone skips the historic layouts).

## Which games provide what

| Game | Lap fraction | `CarCoordinates` | Default axes |
|---|---|---|---|
| iRacing | yes | **no** (SimHub publishes a frozen value, x ≈ 0.3, z = 0, which the plugin ignores) | — |
| Assetto Corsa | yes | yes, metres | x east, −z north (the convention of AC's track map files) |
| Assetto Corsa Competizione | yes | yes | x east, z north (not verified) |
| rFactor 2 / Le Mans Ultimate | yes | yes, metres | x east, z north (not verified) |
| Others | usually | depends on the game | x east, z north |

The axes only decide whether the track comes out mirrored; where north is, is the rotation. Games that
publish no coordinates (iRacing) use the recorded centreline, or dead reckoning until one is recorded.
Verified so far: iRacing (centreline and dead reckoning, on the irsdk emulator) and Assetto Corsa (axes
from its map convention, covered by unit tests); the other games' axes are unverified defaults.
