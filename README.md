# KSTS-Fix

Fixes and extensions for KSTS 2.0.4; this is a separate Harmony plugin.
Requires: KSTS 2.0.4, Harmony (000_Harmony)
KSTS-Fix does nothing if KSTS is not installed.

1. Craft cache fix
- KSTS no longer rebuilds its craft list in the VAB/SPH (its window is not available there anyway).
- Thumbnails are looked up under both KSTS's and KSP's file names (fixes crafts in subfolders).
- A missing thumbnail is only rendered in the Space Center or Tracking Station, at most once per craft
  per game session, and the temporary craft is destroyed right after the picture is taken.

2. Interplanetary missions
- While recording, "Release Payload" is available in orbit around any body, not only the launch body.
  The minimum altitude is the top of that body's atmosphere, or 1 km above its highest terrain if airless.
- The profile is saved for the body where the payload was released, so deployments and transports from
  that profile go there. Mission duration is the recorded flight time, including the transfer.
- Uncrewed one-way carriers can stop the recording wherever they are. Crewed recordings still have to
  land back on the launch body.
- Transport missions can target stations orbiting any body.

3. Transfer windows
- When a profile to another planet (or one of its moons) is saved, the angle between the home planet and
  that planet at launch is stored (save file node KSTSFixScenario).
- A new deployment or transport from that profile waits until the planets are back in that arrangement
  (circular orbits assumed). The mission starts and is paid for immediately; the wait is added to its ETA
  and to the alarm-clock entry. A message shows the wait and the total time when the mission starts.
- No wait for missions around the home planet, its moons, or the Sun, nor for profiles recorded before
  this version.

Experimental! Log lines start with [KSTS-Fix].
