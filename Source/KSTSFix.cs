// KSTS-Fix: fixes KSTS issue #11 ("All crafts loaded when opening VAB") without modifying KSTS.
//
// Cause: KSTS rebuilds its craft list on every scene load (ScenarioModule.OnLoad -> GUI.Reset), and for every
// craft or subassembly whose thumbnail it cannot find it calls ShipConstruction.LoadShip() to render one.
// The built ShipConstruct is never destroyed, and the thumbnail name KSTS looks for does not always match the
// one that gets written (crafts in subfolders, KSP's own naming), so this repeats on every scene change.
// In the editor the leftover crafts stay visible in the middle of the hangar.
//
// Fix (Harmony):
//  - GUI.UpdateShipTemplateCache is skipped in the VAB/SPH (the KSTS window is not available there anyway;
//    the list is rebuilt when the window is opened in the Space Center, Tracking Station or flight).
//  - GUI.ReadAllCraftFiles is replaced: it looks for the thumbnail under both KSTS's and KSP's names,
//    only renders a missing one in the Space Center / Tracking Station, at most once per craft per session,
//    and destroys the temporary craft right after the picture is taken.
//
// Interplanetary missions (see Interplanetary class below): payloads can be released in orbit around any body,
// and the resulting mission profile flies to that body.
//
// Transfer windows (see TransferWindows class below): a mission to another planet waits for the planets to
// return to the arrangement they had when the profile was recorded; the wait is added to the mission's ETA.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace KSTSFix
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class KSTSFixLoader : MonoBehaviour
    {
        void Start()
        {
            try
            {
                Type gui = AccessTools.TypeByName("KSTS.GUI");
                if (gui == null) { Debug.Log("[KSTS-Fix] KSTS not installed, nothing to patch"); return; }
                var harmony = new Harmony("kerbangelion.kstsfix");
                MethodInfo update = AccessTools.Method(gui, "UpdateShipTemplateCache");
                MethodInfo read = AccessTools.Method(gui, "ReadAllCraftFiles", new[] { typeof(string), typeof(string) });
                if (update == null || read == null) { Debug.LogError("[KSTS-Fix] KSTS methods not found, KSTS version not supported"); return; }
                harmony.Patch(update, prefix: new HarmonyMethod(typeof(Patches), nameof(Patches.UpdatePrefix)));
                harmony.Patch(read, prefix: new HarmonyMethod(typeof(Patches), nameof(Patches.ReadPrefix)));
                Debug.Log("[KSTS-Fix] KSTS craft cache patched");

                try { Interplanetary.Apply(harmony); Debug.Log("[KSTS-Fix] interplanetary missions enabled"); }
                catch (Exception e) { Debug.LogError("[KSTS-Fix] interplanetary patch failed: " + e); }

                try { TransferWindows.Apply(harmony); Debug.Log("[KSTS-Fix] transfer windows enabled"); }
                catch (Exception e) { Debug.LogError("[KSTS-Fix] transfer window patch failed: " + e); }
            }
            catch (Exception e) { Debug.LogError("[KSTS-Fix] patch failed: " + e); }
        }
    }

    public static class Patches
    {
        static readonly HashSet<string> attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static bool UpdatePrefix()
        {
            if (HighLogic.LoadedSceneIsEditor && KSTS.GUI.shipTemplates != null) return false;
            if (HighLogic.LoadedSceneIsEditor)
            {
                // First load straight into the editor: keep an empty list, it is filled when the KSTS window opens.
                KSTS.GUI.shipTemplates = new List<KSTS.CachedShipTemplate>();
                return false;
            }
            return true;
        }

        public static bool ReadPrefix(string editorFacility, string shipDirectory)
        {
            try { ReadAllCraftFiles(editorFacility, shipDirectory); }
            catch (Exception e) { Debug.LogError("[KSTS-Fix] ReadAllCraftFiles: " + e); }
            return false;
        }

        static bool CanRender()
        {
            return HighLogic.LoadedScene == GameScenes.SPACECENTER || HighLogic.LoadedScene == GameScenes.TRACKSTATION;
        }

        static void ReadAllCraftFiles(string editorFacility, string shipDirectory)
        {
            string root = KSPUtil.ApplicationRootPath;
            foreach (string craftFile in Directory.GetFiles(shipDirectory, "*.craft", SearchOption.AllDirectories))
            {
                try
                {
                    string name = Path.GetFileNameWithoutExtension(craftFile);
                    if (name == "Auto-Saved Ship") continue;

                    var cached = new KSTS.CachedShipTemplate();
                    switch (editorFacility)
                    {
                        case "VAB": cached.templateOrigin = KSTS.TemplateOrigin.VAB; break;
                        case "SPH": cached.templateOrigin = KSTS.TemplateOrigin.SPH; break;
                        default: cached.templateOrigin = KSTS.TemplateOrigin.SubAssembly; break;
                    }
                    cached.template = ShipConstruction.LoadTemplate(craftFile);
                    if (cached.template == null) continue;
                    if (cached.template.shipPartsExperimental || !cached.template.shipPartsUnlocked) continue;

                    string[] sub = craftFile.Replace(shipDirectory, string.Empty)
                        .Replace(Path.GetFileName(craftFile), string.Empty)
                        .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
                    string subPart = sub.Length > 0 ? string.Join("_", sub) + "_" : string.Empty;
                    string kstsName = HighLogic.SaveFolder + "_" + editorFacility + "_" + subPart + name;

                    var candidates = new List<string> { root + "thumbs/" + kstsName + ".png" };
                    if (editorFacility == "VAB" || editorFacility == "SPH")
                    {
                        try
                        {
                            string kspName = ShipConstruction.GetPlayerCraftThumbnailName(Path.GetDirectoryName(craftFile), name);
                            if (!string.IsNullOrEmpty(kspName)) candidates.Add(root + "thumbs/" + kspName + ".png");
                        }
                        catch { }
                    }

                    string thumbFile = candidates.Find(File.Exists);
                    if (thumbFile == null && CanRender() && attempted.Add(craftFile))
                    {
                        RenderThumbnail(craftFile, kstsName);
                        thumbFile = candidates.Find(File.Exists);
                    }

                    if (thumbFile != null)
                    {
                        var full = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        full.LoadImage(File.ReadAllBytes(thumbFile));
                        cached.thumbnail = KSTS.GUI.ResizeTexture(full, 64, 64);
                        UnityEngine.Object.Destroy(full);
                    }
                    else cached.thumbnail = KSTS.GUI.placeholderImage;

                    KSTS.GUI.shipTemplates.Add(cached);
                }
                catch (Exception e)
                {
                    Debug.LogError("[KSTS-Fix] processing '" + craftFile + "': " + e);
                }
            }
        }

        static void RenderThumbnail(string craftFile, string thumbName)
        {
            ShipConstruct ship = null;
            try
            {
                Debug.Log("[KSTS-Fix] rendering missing thumbnail for " + craftFile);
                ship = ShipConstruction.LoadShip(craftFile);
                if (ship != null && ship.parts != null && ship.parts.Count > 0)
                    KSTS.ThumbnailHelper.CaptureThumbnail(ship, 256, "thumbs/", thumbName);
            }
            catch (Exception e)
            {
                Debug.LogError("[KSTS-Fix] thumbnail for '" + craftFile + "' failed: " + e.Message);
            }
            finally
            {
                if (ship != null && ship.parts != null)
                {
                    foreach (Part p in ship.parts)
                        if (p != null) UnityEngine.Object.Destroy(p.gameObject);
                    ship.parts.Clear();
                }
            }
        }
    }

    // Lets KSTS record and fly missions to bodies other than the launch body.
    //  - While a recording is ascending, an orbit around ANY body counts (KSTS only accepted the launch body),
    //    with the minimum altitude worked out for that body.
    //  - The body where the payload was released is stored with the recording and becomes the profile's body.
    //  - Uncrewed one-way carriers can finish the recording wherever they are; crewed ones still have to land
    //    back on the launch body. A carrier that ends landed on another body is not counted as recovered.
    //  - Transport targets can orbit any body (KSTS only listed stations around the home body).
    public static class Interplanetary
    {
        const string Marker = "KSTSFix-deployBody:";
        static FieldInfo vesselField;
        static MethodInfo dockingPortTypes;
        static Vessel recVessel(KSTS.FlightRecording r) { return (Vessel)vesselField.GetValue(r); }

        public static void Apply(Harmony h)
        {
            vesselField = AccessTools.Field(typeof(KSTS.FlightRecording), "vessel");
            Type tv = AccessTools.TypeByName("KSTS.TargetVessel");
            dockingPortTypes = AccessTools.Method(tv, "GetVesselDockingPortTypes");
            Type rec = typeof(KSTS.FlightRecording);
            h.Patch(AccessTools.Method(rec, "Update"), postfix: new HarmonyMethod(typeof(Interplanetary), nameof(UpdatePostfix)));
            h.Patch(AccessTools.Method(rec, "CanFinish"), prefix: new HarmonyMethod(typeof(Interplanetary), nameof(CanFinishPrefix)));
            h.Patch(AccessTools.Method(rec, "DeployPayloadAssembly"),
                prefix: new HarmonyMethod(typeof(Interplanetary), nameof(DeployPrefix)),
                postfix: new HarmonyMethod(typeof(Interplanetary), nameof(DeployPostfix)));
            h.Patch(AccessTools.Method(rec, "DeployPayloadResources"),
                prefix: new HarmonyMethod(typeof(Interplanetary), nameof(DeployPrefix)),
                postfix: new HarmonyMethod(typeof(Interplanetary), nameof(DeployPostfix)));
            h.Patch(AccessTools.Method(typeof(KSTS.MissionProfile), "CreateFromRecording"),
                postfix: new HarmonyMethod(typeof(Interplanetary), nameof(ProfilePostfix)));
            h.Patch(AccessTools.Method(tv, "IsValidTarget"),
                prefix: new HarmonyMethod(typeof(Interplanetary), nameof(IsValidTargetPrefix)));
        }

        // Lowest altitude a payload may be placed at around this body.
        static double MinAltitude(CelestialBody body)
        {
            if (body.atmosphere)
            {
                // Same rule as KSTS (top of the pressure curve), falling back to the atmosphere depth.
                FloatCurve pc = body.atmospherePressureCurve;
                if (pc != null && pc.Curve != null)
                    for (int i = pc.Curve.length - 1; i >= 0; i--)
                        if (pc.Curve[i].value == 0) return Math.Max(pc.Curve[i].time, 1);
                return Math.Max(body.atmosphereDepth, 1);
            }
            // Airless: clear the highest terrain by 1 km.
            if (body.pqsController != null && body.pqsController.radiusMax > body.Radius)
                return body.pqsController.radiusMax - body.Radius + 1000;
            return 1;
        }

        public static void UpdatePostfix(KSTS.FlightRecording __instance, Vessel vessel)
        {
            if (__instance.status != KSTS.FlightRecordingStatus.ASCENDING || vessel == null) return;
            if (vessel.situation != Vessel.Situations.ORBITING || vessel.orbit == null) return;
            CelestialBody body = vessel.orbit.referenceBody;
            __instance.minAltitude = MinAltitude(body);
            __instance.maxAltitude = vessel.orbit.PeA > __instance.minAltitude ? vessel.orbit.PeA : 0;
        }

        public static bool CanFinishPrefix(KSTS.FlightRecording __instance, ref bool __result)
        {
            Vessel v = recVessel(__instance);
            __result = false;
            if (__instance.status != KSTS.FlightRecordingStatus.DESCENDING || __instance.payloadMass <= 0 || v == null) return false;
            if (__instance.mustReturn)
                __result = v.mainBody.bodyName == __instance.launchBodyName &&
                           (v.situation == Vessel.Situations.LANDED || v.situation == Vessel.Situations.SPLASHED);
            else __result = true;
            return false;
        }

        public static void DeployPrefix(KSTS.FlightRecording __instance, out string __state)
        {
            Vessel v = recVessel(__instance);
            __state = (v != null && v.orbit != null) ? v.orbit.referenceBody.bodyName : null;
        }

        public static void DeployPostfix(KSTS.FlightRecording __instance, string __state)
        {
            if (__state == null || __instance.status != KSTS.FlightRecordingStatus.DESCENDING) return;
            if (__instance.usedPartIds == null) __instance.usedPartIds = new List<string>();
            __instance.usedPartIds.RemoveAll(x => x.StartsWith(Marker));
            __instance.usedPartIds.Add(Marker + __state);
            Debug.Log("[KSTS-Fix] payload released around " + __state);
        }

        public static void ProfilePostfix(Vessel vessel, KSTS.FlightRecording recording, KSTS.MissionProfile __result)
        {
            if (__result == null || recording == null) return;
            string body = recording.usedPartIds?.Find(x => x.StartsWith(Marker));
            if (body != null) __result.bodyName = body.Substring(Marker.Length);
            if (vessel != null && vessel.mainBody.bodyName != recording.launchBodyName && !__result.oneWayMission)
            {
                // Landed somewhere other than home: the carrier is not recovered.
                __result.oneWayMission = true;
                __result.launchCost = recording.launchCost;
            }
            Debug.Log("[KSTS-Fix] mission profile '" + __result.profileName + "' flies to " + __result.bodyName);
            TransferWindows.OnProfileRecorded(__result, recording);
        }

        public static bool IsValidTargetPrefix(Vessel vessel, KSTS.MissionProfile profile, ref bool __result)
        {
            if (profile != null) return true; // KSTS already compares the target's body with the profile's body.
            __result = vessel != null && vessel.situation == Vessel.Situations.ORBITING && vessel.orbit != null &&
                       ((List<string>)dockingPortTypes.Invoke(null, new object[] { vessel })).Count > 0;
            return false;
        }
    }

    // Interplanetary profiles remember the planets' arrangement at launch (the phase angle between the home planet
    // and the destination's planet, using circular orbits in the plane of each orbit). A new mission from such a
    // profile is delayed until that arrangement comes back, so its ETA = wait + recorded duration.
    // Missions to the home planet's moons, to the star, or from profiles recorded before this feature: no wait.
    public static class TransferWindows
    {
        class Window { public string home, dest; public double phase; }

        static readonly Dictionary<KSTS.MissionProfile, Window> byProfile = new Dictionary<KSTS.MissionProfile, Window>();
        static readonly Dictionary<string, Window> byName = new Dictionary<string, Window>();
        static FieldInfo profilesField;

        public static void Apply(Harmony h)
        {
            Type mc = AccessTools.TypeByName("KSTS.MissionController");
            profilesField = AccessTools.Field(mc, "missionProfiles");
            Type mission = typeof(KSTS.Mission);
            var pre = new HarmonyMethod(typeof(TransferWindows), nameof(CreatePrefix));
            var post = new HarmonyMethod(typeof(TransferWindows), nameof(CreatePostfix));
            h.Patch(AccessTools.Method(mission, "CreateDeployment"), prefix: pre, postfix: post);
            h.Patch(AccessTools.Method(mission, "CreateTransport"), prefix: pre, postfix: post);
        }

        // The body's ancestor that orbits the star (the body itself for a planet); null for the star.
        static CelestialBody Planet(CelestialBody b)
        {
            while (b != null && b.referenceBody != null && b.referenceBody != b && b.referenceBody.referenceBody != b.referenceBody)
                b = b.referenceBody;
            if (b == null || b.orbit == null || b.referenceBody == b) return null;
            return b;
        }

        static CelestialBody Body(string name)
        {
            return string.IsNullOrEmpty(name) ? null : FlightGlobals.Bodies.Find(x => x.bodyName == name);
        }

        // Mean longitude in degrees (circular-orbit position around the star).
        static double Longitude(CelestialBody planet, double ut)
        {
            Orbit o = planet.orbit;
            double m = o.meanAnomalyAtEpoch + 2 * Math.PI / o.period * (ut - o.epoch);
            return Mod(o.LAN + o.argumentOfPeriapsis + m * 180 / Math.PI, 360);
        }

        static double Mod(double x, double m) { return x - m * Math.Floor(x / m); }

        public static void OnProfileRecorded(KSTS.MissionProfile profile, KSTS.FlightRecording recording)
        {
            try
            {
                CelestialBody home = Planet(Body(recording.launchBodyName));
                CelestialBody dest = Planet(Body(profile.bodyName));
                if (home == null || dest == null || home == dest) return;
                var w = new Window
                {
                    home = home.bodyName,
                    dest = dest.bodyName,
                    phase = Mod(Longitude(dest, recording.startTime) - Longitude(home, recording.startTime), 360)
                };
                byProfile[profile] = w;
                Debug.Log("[KSTS-Fix] profile '" + profile.profileName + "': " + w.home + " -> " + w.dest + " phase angle at launch " + w.phase.ToString("F1") + " deg");
            }
            catch (Exception e) { Debug.LogError("[KSTS-Fix] OnProfileRecorded: " + e); }
        }

        static Window Get(KSTS.MissionProfile profile)
        {
            Window w;
            if (profile == null) return null;
            if (byProfile.TryGetValue(profile, out w)) return w;
            if (byName.TryGetValue(profile.profileName, out w)) { byProfile[profile] = w; return w; }
            return null;
        }

        // Seconds until the recorded arrangement comes back (0 if none applies).
        public static double Wait(KSTS.MissionProfile profile, out string dest)
        {
            dest = null;
            Window w = Get(profile);
            if (w == null) return 0;
            CelestialBody home = Body(w.home), target = Body(w.dest);
            if (home == null || target == null) return 0;
            dest = target.bodyName;
            double ut = Planetarium.GetUniversalTime();
            double rel = 360 / target.orbit.period - 360 / home.orbit.period; // deg/s, negative for outer planets
            if (Math.Abs(rel) < 1e-12) return 0;
            double synodic = 360 / Math.Abs(rel);
            double now = Longitude(target, ut) - Longitude(home, ut);
            double delta = Mod(w.phase - now, 360);
            if (rel < 0) delta -= 360;
            double t = Mod(delta / rel, synodic);
            return t;
        }

        public static void CreatePrefix(KSTS.MissionProfile profile, out double __state)
        {
            __state = 0;
            try
            {
                string dest;
                double wait = Wait(profile, out dest);
                if (wait <= 0) return;
                __state = wait;
                profile.missionDuration += wait; // KSTS computes eta and its alarms from this; restored in the postfix.
                string msg = "Next transfer window to " + dest + " in " + KSTS.GUI.FormatDuration(wait) +
                             ", arrival in " + KSTS.GUI.FormatDuration(profile.missionDuration);
                ScreenMessages.PostScreenMessage(msg, 10f);
                Debug.Log("[KSTS-Fix] " + profile.profileName + ": " + msg);
            }
            catch (Exception e) { Debug.LogError("[KSTS-Fix] transfer window: " + e); }
        }

        public static void CreatePostfix(KSTS.MissionProfile profile, double __state)
        {
            if (__state > 0) profile.missionDuration -= __state;
        }

        public static void Load(ConfigNode node)
        {
            byProfile.Clear();
            byName.Clear();
            foreach (ConfigNode n in node.GetNodes("WINDOW"))
            {
                double phase;
                string name = n.GetValue("profile");
                if (name == null || !double.TryParse(n.GetValue("phase"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out phase)) continue;
                byName[name] = new Window { home = n.GetValue("home"), dest = n.GetValue("dest"), phase = phase };
            }
        }

        public static void Save(ConfigNode node)
        {
            var profiles = profilesField != null ? profilesField.GetValue(null) as SortedList<string, KSTS.MissionProfile> : null;
            if (profiles == null)
            {
                // KSTS not ready: keep what was loaded.
                foreach (var kv in byName) Write(node, kv.Key, kv.Value);
                return;
            }
            foreach (var kv in profiles)
            {
                Window w = Get(kv.Value);
                if (w != null) Write(node, kv.Value.profileName, w);
            }
        }

        static void Write(ConfigNode node, string name, Window w)
        {
            ConfigNode n = node.AddNode("WINDOW");
            n.AddValue("profile", name);
            n.AddValue("home", w.home);
            n.AddValue("dest", w.dest);
            n.AddValue("phase", w.phase.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.SPACECENTER, GameScenes.TRACKSTATION, GameScenes.FLIGHT, GameScenes.EDITOR)]
    public class KSTSFixScenario : ScenarioModule
    {
        public override void OnLoad(ConfigNode node) { if (AccessTools.TypeByName("KSTS.GUI") != null) TransferWindows.Load(node); }
        public override void OnSave(ConfigNode node) { if (AccessTools.TypeByName("KSTS.GUI") != null) TransferWindows.Save(node); }
    }
}
