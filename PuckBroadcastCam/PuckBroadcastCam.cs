// PuckBroadcastCam.cs
// Client-only spectator / streamer camera for Puck.
//
// Takes over the vanilla SpectatorCamera while you are spectating and gives it
// follow modes. Nothing here runs on, or needs, the server; other players do
// not need the mod.
//
// Controls (only while spectating; all rebindable in-game, F9 by default):
//   Left / Right   cycle subject: Puck, then every player (Blue, then Red)
//   Up / Down      cycle view:    First Person -> Chase -> Broadcast -> Free
//   [ / ]          cycle angle within the current view
//   Numpad 0       First Person
//   Numpad 1-5     Chase: Behind, Behind High, Left, Right, Front
//   Numpad 6-9, .  Broadcast: Gantry, Far Gantry, End 1, End 2, Overhead
//   \  (Backslash) toggle auto-spectate (follows the puck carrier)
//   F7             toggle the on-screen overlay (F8 is the game's own camera key)
//   F6             goalie only: step out into the camera (body stays put,
//                  movement suspended) and back in again
//   F9             key-bind menu (saved to <persistentDataPath>/BroadcastCam/keybinds.cfg)
//
// During goal replays the camera follows the replay copies of the players and
// puck, keeping whoever you had selected.
//
// "Free" is the untouched vanilla fly camera (WASD / mouse). Leaving a follow
// mode hands the camera back exactly where it is, so there is no snap.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using System.Reflection;

namespace PuckBroadcastCam
{
    // =====================================================================
    // PLUGIN ENTRY
    // =====================================================================
    public class BroadcastCamPlugin : IPuckPlugin
    {
        private Harmony _harmony;
        private GameObject _host;

        public bool OnEnable()
        {
            try
            {
                if (UnityEngine.Application.isBatchMode)
                {
                    UnityEngine.Debug.Log("[BroadcastCam] Dedicated server detected; nothing to do.");
                    return true;
                }

                _harmony = new Harmony("com.pak.puck.broadcastcam");
                _harmony.PatchAll(typeof(BroadcastCamPlugin).Assembly);

                _host = new GameObject("BroadcastCam");
                UnityEngine.Object.DontDestroyOnLoad(_host);
                _host.AddComponent<BroadcastCam>();

                UnityEngine.Debug.Log("[BroadcastCam] Enabled.");
                return true;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[BroadcastCam] Failed to enable: " + e);
                return false;
            }
        }

        public bool OnDisable()
        {
            if (_host != null)
            {
                UnityEngine.Object.Destroy(_host);
                _host = null;
            }
            BroadcastCam.Active = false;
            _harmony?.UnpatchSelf();
            UnityEngine.Debug.Log("[BroadcastCam] Disabled.");
            return true;
        }
    }

    // =====================================================================
    // SETTINGS
    // =====================================================================
    public static class Settings
    {
        // Keys live in Binds (rebindable in-game, F9 by default).

        // ---- Switching ----
        /// <summary>true = instant cut when the view or angle changes (numpad, arrows, [ ]).</summary>
        public static bool CutOnAngleChange = true;
        /// <summary>true = instant cut when the subject changes; false = fast glide.</summary>
        public static bool CutOnSubjectChange = false;
        /// <summary>How long (s) the glide after a switch lasts.</summary>
        public static float TransitionTime = 0.6f;

        // ---- Auto-spectate ----
        /// <summary>A blade this close (m) to the puck counts as carrying it.</summary>
        public static float CarrierRadius = 1.2f;
        /// <summary>After the last touch, the toucher stays the carrier this long (s).</summary>
        public static float CarrierMemory = 1.5f;
        /// <summary>A new carrier must hold the puck this long (s) before auto cuts to them.</summary>
        public static float AutoSwitchToPlayerDelay = 0.3f;
        /// <summary>Loose puck must stay loose this long (s) before auto follows the puck itself.</summary>
        public static float AutoSwitchToPuckDelay = 0.8f;

        // ---- First person ----
        public static bool FirstPersonLevelHorizon = true;
        public static float FirstPersonRotationSharpness = 18f;
        /// <summary>A player's camera further than this (m) from their head is treated as misplaced.</summary>
        public static float FirstPersonMaxCameraToHead = 0.6f;
        /// <summary>Eye offset from the head, used only when the camera is misplaced.</summary>
        public static float FirstPersonEyeUp = 0.05f;
        public static float FirstPersonEyeForward = 0.12f;

        // ---- Chase ----
        public static float ChaseDistance = 4.0f;
        public static float ChaseHeight = 2.0f;
        /// <summary>0 = use your in-game FOV setting.</summary>
        public static float ChaseFov = 0f;
        public static float ChasePositionSmooth = 0.12f;
        public static float ChaseLookSmooth = 0.08f;

        // ---- Broadcast ----
        public static float BroadcastFov = 60f;
        public static float GantryHeight = 11f;
        /// <summary>Distance (m) behind the boards for the side gantry.</summary>
        public static float GantryBack = 9f;
        /// <summary>0..1: how far the gantry slides along the boards to track play.</summary>
        public static float GantryTrack = 0.75f;
        /// <summary>
        /// Minimum downward angle (degrees) the side/end cameras look at the play
        /// with. They rise (up to the max height) to keep it, so the far boards and
        /// the empty void beyond don't take over the top of the picture.
        /// </summary>
        public static float BroadcastMinPitch = 24f;
        public static float GantryMaxHeight = 20f;
        /// <summary>Side/end cameras never get closer than this (m, along the ice) to the play; they back off instead.</summary>
        public static float BroadcastMinDistance = 20f;
        /// <summary>Cut straight to the faceoff dot when the puck is reset (icing, offside, goal).</summary>
        public static bool CutOnFaceoff = true;
        /// <summary>A puck jump this big (m) in one frame, or a new puck appearing this far away, counts as a faceoff reset.</summary>
        public static float PuckTeleportDistance = 4f;
        /// <summary>Longest (s) the camera holds on the dot waiting for the drop.</summary>
        public static float FaceoffFocusTime = 8f;
        public static float EndMaxHeight = 18f;
        /// <summary>
        /// Keep-in-frame: the puck is never allowed further from the centre than
        /// this fraction of the half-frame. If it gets there the camera pans with it
        /// immediately instead of waiting for its smoothing to catch up.
        /// </summary>
        public static float PuckFrameMargin = 0.72f;
        /// <summary>The broadcast pan speed limit rises to this multiple of the puck's speed for shots and passes.</summary>
        public static float PuckPanSpeedFactor = 1.15f;
        public static float EndHeight = 8f;
        /// <summary>Distance (m) behind the goal line for the end cameras.</summary>
        public static float EndBack = 9f;
        public static float EndFov = 68f;
        public static float OverheadHeight = 20f;
        public static float OverheadFov = 68f;
        /// <summary>0..1: with a player subject, how far broadcast framing leans toward the puck.</summary>
        public static float BroadcastPuckBias = 0.35f;
        public static float BroadcastPuckBiasRange = 18f;
        public static float BroadcastPositionSmooth = 0.45f;
        public static float BroadcastLookSmooth = 0.12f;
        /// <summary>
        /// Broadcast "soft zone" (m): the shot ignores focus movement smaller than
        /// this, so puck battles, takeaways and stick-handling don't twitch the camera.
        /// </summary>
        public static float BroadcastDeadZone = 1.5f;
        /// <summary>Smoothing (s) on the broadcast focus once it leaves the soft zone.</summary>
        public static float BroadcastFocusSmooth = 0.35f;
        /// <summary>Fastest the broadcast focus may pan (m/s). Caps whip-pans on turnovers.</summary>
        public static float BroadcastMaxPanSpeed = 16f;

        // ---- Zoom ----
        public static float FovStep = 5f;
        public static float FovMin = 25f;
        public static float FovMax = 120f;

        // ---- Goalie spectate ----
        /// <summary>Drop out of goalie spectate (camera AND control back) when the goalie is reset for a faceoff.</summary>
        public static bool GoalieExitOnFaceoff = true;
        /// <summary>A body jump bigger than this (m) in one frame counts as a faceoff reset.</summary>
        public static float GoalieTeleportDistance = 2.5f;

        // ---- Free fly on the goalie camera ----
        public static float FreeFlySpeed = 5f;

        // ---- Rink (0 = detect from the goals and the level bounds) ----
        public static float RinkHalfWidthOverride = 0f;
    }


    // =====================================================================
    // KEY BINDINGS — rebindable in-game, saved per user
    // =====================================================================
    public enum CamAction
    {
        PrevSubject, NextSubject, NextView, PrevView, PrevAngle, NextAngle,
        ToggleAuto, ToggleOverlay, OpenBindings,
        ShotFirstPerson,
        ShotChase1, ShotChase2, ShotChase3, ShotChase4, ShotChase5,
        ShotBroadcast1, ShotBroadcast2, ShotBroadcast3, ShotBroadcast4, ShotBroadcast5,
        GoalieSpectate,
        ZoomIn, ZoomOut, ZoomReset
    }

    public static class Binds
    {
        public static readonly CamAction[] All = (CamAction[])Enum.GetValues(typeof(CamAction));

        private static readonly Dictionary<CamAction, Key> Defaults = new Dictionary<CamAction, Key>
        {
            { CamAction.PrevSubject,    Key.LeftArrow },
            { CamAction.NextSubject,    Key.RightArrow },
            { CamAction.NextView,       Key.UpArrow },
            { CamAction.PrevView,       Key.DownArrow },
            { CamAction.PrevAngle,      Key.LeftBracket },
            { CamAction.NextAngle,      Key.RightBracket },
            { CamAction.ToggleAuto,     Key.Backslash },
            { CamAction.ToggleOverlay,  Key.F7 },
            { CamAction.OpenBindings,   Key.F9 },
            { CamAction.ShotFirstPerson, Key.Numpad0 },
            { CamAction.ShotChase1,     Key.Numpad1 },
            { CamAction.ShotChase2,     Key.Numpad2 },
            { CamAction.ShotChase3,     Key.Numpad3 },
            { CamAction.ShotChase4,     Key.Numpad4 },
            { CamAction.ShotChase5,     Key.Numpad5 },
            { CamAction.ShotBroadcast1, Key.Numpad6 },
            { CamAction.ShotBroadcast2, Key.Numpad7 },
            { CamAction.ShotBroadcast3, Key.Numpad8 },
            { CamAction.ShotBroadcast4, Key.Numpad9 },
            { CamAction.ShotBroadcast5, Key.NumpadPeriod },
            { CamAction.GoalieSpectate, Key.F6 },
            { CamAction.ZoomIn,         Key.NumpadPlus },
            { CamAction.ZoomOut,        Key.NumpadMinus },
            { CamAction.ZoomReset,      Key.NumpadMultiply },
        };

        public static readonly Dictionary<CamAction, Key> Current = new Dictionary<CamAction, Key>();

        private const int FileVersion = 2;

        /// <summary>Degrees added to every view's FOV. Saved with the key binds.</summary>
        public static float FovOffset;

        public static void AdjustFov(float delta)
        {
            FovOffset = Mathf.Clamp(FovOffset + delta, -40f, 40f);
            Save();
        }

        public static void ResetFov()
        {
            FovOffset = 0f;
            Save();
        }

        public static string Label(CamAction a)
        {
            switch (a)
            {
                case CamAction.PrevSubject: return "Previous subject";
                case CamAction.NextSubject: return "Next subject";
                case CamAction.NextView: return "Next view";
                case CamAction.PrevView: return "Previous view";
                case CamAction.PrevAngle: return "Previous angle";
                case CamAction.NextAngle: return "Next angle";
                case CamAction.ToggleAuto: return "Auto-spectate on/off";
                case CamAction.ToggleOverlay: return "Overlay on/off";
                case CamAction.OpenBindings: return "Open this menu";
                case CamAction.ShotFirstPerson: return "Shot: First Person";
                case CamAction.ShotChase1: return "Shot: Chase Behind";
                case CamAction.ShotChase2: return "Shot: Chase Behind High";
                case CamAction.ShotChase3: return "Shot: Chase Left";
                case CamAction.ShotChase4: return "Shot: Chase Right";
                case CamAction.ShotChase5: return "Shot: Chase Front";
                case CamAction.ShotBroadcast1: return "Shot: Gantry";
                case CamAction.ShotBroadcast2: return "Shot: Far Gantry";
                case CamAction.ShotBroadcast3: return "Shot: End 1";
                case CamAction.ShotBroadcast4: return "Shot: End 2";
                case CamAction.ShotBroadcast5: return "Shot: Overhead";
                case CamAction.GoalieSpectate: return "Goalie: spectate on/off";
                case CamAction.ZoomIn: return "Zoom in (narrower FOV)";
                case CamAction.ZoomOut: return "Zoom out (wider FOV)";
                case CamAction.ZoomReset: return "Reset zoom";
            }
            return a.ToString();
        }

        public static Key Get(CamAction a)
        {
            Key k;
            return Current.TryGetValue(a, out k) ? k : Key.None;
        }

        public static bool Pressed(Keyboard kb, CamAction a)
        {
            Key k = Get(a);
            if (k == Key.None) return false;
            var control = kb[k];
            return control != null && control.wasPressedThisFrame;
        }

        /// <summary>Bind a key. If another action already uses it, the two swap.</summary>
        public static void Set(CamAction a, Key k)
        {
            if (k != Key.None)
            {
                Key old = Get(a);
                foreach (CamAction other in All)
                {
                    if (other != a && Get(other) == k) Current[other] = old;
                }
            }
            Current[a] = k;
            Save();
        }

        public static void ResetToDefaults()
        {
            Current.Clear();
            foreach (var kv in Defaults) Current[kv.Key] = kv.Value;
            Save();
        }

        public static string KeyName(Key k)
        {
            if (k == Key.None) return "—";
            try
            {
                Keyboard kb = Keyboard.current;
                if (kb != null)
                {
                    var control = kb[k];
                    if (control != null && !string.IsNullOrEmpty(control.displayName)) return control.displayName;
                }
            }
            catch { }
            return k.ToString();
        }

        // Saved in the user's own data folder rather than next to the DLL:
        // the Workshop folder is not reliably writable.
        private static string FilePath
        {
            get { return Path.Combine(UnityEngine.Application.persistentDataPath, "BroadcastCam", "keybinds.cfg"); }
        }

        public static void Load()
        {
            Current.Clear();
            foreach (var kv in Defaults) Current[kv.Key] = kv.Value;
            try
            {
                string path = FilePath;
                if (!File.Exists(path)) { Save(); return; }
                int version = 1;
                var seen = new HashSet<CamAction>();
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    if (line.Substring(0, eq).Trim().Equals("FovOffset", StringComparison.OrdinalIgnoreCase))
                    {
                        float f;
                        if (float.TryParse(line.Substring(eq + 1).Trim(), System.Globalization.NumberStyles.Float,
                                           System.Globalization.CultureInfo.InvariantCulture, out f))
                            FovOffset = Mathf.Clamp(f, -40f, 40f);
                        continue;
                    }
                    if (line.Substring(0, eq).Trim().Equals("Version", StringComparison.OrdinalIgnoreCase))
                    {
                        int.TryParse(line.Substring(eq + 1).Trim(), out version);
                        continue;
                    }
                    CamAction a;
                    Key k;
                    if (Enum.TryParse(line.Substring(0, eq).Trim(), true, out a)
                        && Enum.TryParse(line.Substring(eq + 1).Trim(), true, out k))
                    {
                        Current[a] = k;
                        seen.Add(a);
                    }
                }

                // Actions added after the file was written get their default,
                // unless that key is already taken - then they start unbound
                // rather than silently doubling up on someone's existing bind.
                foreach (CamAction a in All)
                {
                    if (seen.Contains(a)) continue;
                    Key k = Get(a);
                    if (k == Key.None) continue;
                    foreach (CamAction other in All)
                    {
                        if (other != a && seen.Contains(other) && Get(other) == k) { Current[a] = Key.None; break; }
                    }
                }

                // v1 files defaulted the overlay to F8, which the game already
                // uses for its own camera mode. Move it unless the player has
                // since picked something else themselves.
                if (version < FileVersion)
                {
                    if (Get(CamAction.ToggleOverlay) == Key.F8)
                        Set(CamAction.ToggleOverlay, Defaults[CamAction.ToggleOverlay]);
                    Save();
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[BroadcastCam] Couldn't read key binds, using defaults: " + e.Message);
            }
        }

        public static void Save()
        {
            try
            {
                string path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var sb = new StringBuilder();
                sb.AppendLine("# Broadcast Cam key binds. Rebind in-game with F9 (default), or edit here.");
                sb.AppendLine("# Key names are Unity Input System names, e.g. Numpad1, F5, LeftArrow, Semicolon. None = unbound.");
                sb.AppendLine("Version = " + FileVersion);
                sb.AppendLine("FovOffset = " + FovOffset.ToString(System.Globalization.CultureInfo.InvariantCulture));
                foreach (CamAction a in All) sb.AppendLine(a + " = " + Get(a));
                File.WriteAllText(path, sb.ToString());
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[BroadcastCam] Couldn't save key binds: " + e.Message);
            }
        }
    }

    public enum ViewMode { FirstPerson = 0, Chase = 1, Broadcast = 2, Free = 3 }

    // =====================================================================
    // VANILLA FLY-CAM SUPPRESSION
    // =====================================================================
    [HarmonyPatch(typeof(SpectatorCamera), "Update")]
    public static class SpectatorCamera_Update_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(SpectatorCamera __instance)
        {
            // Skip vanilla free-fly only for OUR camera, only while we drive it.
            return !((BroadcastCam.Active || BroadcastCam.MenuOpen) && __instance != null && __instance.IsOwner);
        }
    }

    // =====================================================================
    // CONTROLLER
    // =====================================================================
    public class BroadcastCam : MonoBehaviour
    {
        /// <summary>True while this mod is driving the local spectator camera.</summary>
        public static bool Active;

        /// <summary>True while the key-bind menu is open.</summary>
        public static bool MenuOpen;

        // ---- vanilla SpectatorCamera internals, for a seamless hand-back to Free ----
        private static readonly AccessTools.FieldRef<SpectatorCamera, Vector3> FlyPos;
        private static readonly AccessTools.FieldRef<SpectatorCamera, Vector3> FlyVel;
        private static readonly AccessTools.FieldRef<SpectatorCamera, float> FlyPitch;
        private static readonly AccessTools.FieldRef<SpectatorCamera, float> FlyYaw;
        private static readonly AccessTools.FieldRef<SpectatorCamera, float> FlyTargetPitch;
        private static readonly AccessTools.FieldRef<SpectatorCamera, float> FlyTargetYaw;
        private static readonly bool FlyRefsOk;

        static BroadcastCam()
        {
            try
            {
                FlyPos = AccessTools.FieldRefAccess<SpectatorCamera, Vector3>("position");
                FlyVel = AccessTools.FieldRefAccess<SpectatorCamera, Vector3>("positionVelocity");
                FlyPitch = AccessTools.FieldRefAccess<SpectatorCamera, float>("pitch");
                FlyYaw = AccessTools.FieldRefAccess<SpectatorCamera, float>("yaw");
                FlyTargetPitch = AccessTools.FieldRefAccess<SpectatorCamera, float>("targetPitch");
                FlyTargetYaw = AccessTools.FieldRefAccess<SpectatorCamera, float>("targetYaw");
                FlyRefsOk = true;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[BroadcastCam] Can't read SpectatorCamera internals (" + e.Message +
                                 "); returning to Free may snap back to the old spot.");
                FlyRefsOk = false;
            }
        }

        private static readonly string[] ViewNames = { "First Person", "Chase", "Broadcast", "Free" };
        private static readonly string[] ChaseAngles = { "Behind", "Behind High", "Left", "Right", "Front" };
        private static readonly string[] BroadcastAngles = { "Gantry", "Gantry (far side)", "End 1", "End 2", "Overhead" };

        // ---- state ----
        private ViewMode _mode = ViewMode.Chase;
        private ViewMode _lastFollowMode = ViewMode.Chase;
        private readonly int[] _angle = new int[3];
        private ulong? _subject;          // null = the puck
        private bool _auto;
        private bool _overlay = true;

        private ulong? _pendingSubject;
        private float _pendingSince;
        private bool _hasPending;

        private BaseCamera _cam;
        private PlayerBody _hiddenBody;

        // smoothing
        private Vector3 _pos, _posVel, _look, _lookVel;
        private bool _haveSmoothed;
        private float _transitionUntil;
        private bool _cutPending;
        private float _headingYaw, _headingYawVel;
        private bool _haveHeading;
        private Vector3 _lastPuckPos;
        private Vector3 _puckVel;
        private bool _havePuckPos;

        // rink frame
        private float _nextRinkRefresh;
        private Vector3 _center, _axisL = Vector3.forward, _axisW = Vector3.right, _endA, _endB;
        private float _halfGoal = 26f, _halfWidth = 13f, _ice;

        // carrier tracking
        private readonly Dictionary<ulong, float> _touchServerTime = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, float> _touchLocalTime = new Dictionary<ulong, float>();
        private readonly List<Player> _players = new List<Player>(16);
        private bool _replaying;

        // -----------------------------------------------------------------
        private void Awake()
        {
            Binds.Load();
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        }

        private void OnDestroy()
        {
            CloseMenu();
            ShowHiddenBody();
            if (_cam != null && Active) ReleaseToFree(_cam as SpectatorCamera);
            ExitGoalieSpectate();
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            Active = false;
        }

        private void LateUpdate()
        {
            GoalieTick();

            // The camera we drive: the goalie's own camera while they're in
            // goalie spectate, otherwise the vanilla spectator camera.
            BaseCamera cam = GoalieSpectate.Active
                ? (BaseCamera)GoalieSpectate.Camera
                : GetLocalSpectatorCamera();
            if (cam == null || !cam.IsEnabled || !cam.IsOwner)
            {
                // Never leave a goalie's inputs suspended without our camera.
                ExitGoalieSpectate();
                CloseMenu();
                Deactivate();
                return;
            }
            if (_cam != cam) { _haveSmoothed = false; _hasFinal = false; _flyInit = false; }
            _cam = cam;

            HandleInput();

            if (_mode == ViewMode.Free)
            {
                ShowHiddenBody();
                if (GoalieSpectate.Active)
                {
                    // No vanilla fly-cam on the goalie's camera; run our own.
                    Active = true;
                    FreeFly(cam);
                    return;
                }
                if (Active) ReleaseToFree(cam as SpectatorCamera);
                Active = false;
                return;
            }
            _flyInit = false;

            if (!Active) { _haveSmoothed = false; }
            Active = true;

            RefreshRink();
            RefreshPlayers();

            Puck puck = GetPuck();
            TrackPuckVelocity(puck);
            if (_auto) UpdateAutoSubject(puck);

            Player subjectPlayer = ResolveSubject(puck);
            if (subjectPlayer == null && puck == null)
            {
                // Nothing to look at; hold where we are.
                ShowHiddenBody();
                return;
            }

            Vector3 desiredPos, desiredLook;
            float fov;
            bool firstPerson = _mode == ViewMode.FirstPerson && subjectPlayer != null;

            if (firstPerson)
            {
                ApplyFirstPerson(cam, subjectPlayer);
                return;
            }

            ShowHiddenBody();

            Vector3 subjectPos = subjectPlayer != null
                ? subjectPlayer.PlayerBody.transform.position
                : puck.transform.position;

            float posSmooth, lookSmooth;
            if (_mode == ViewMode.Chase || _mode == ViewMode.FirstPerson)
            {
                // First person on the puck falls back to chase.
                Vector3 heading = GetHeading(subjectPlayer, puck);
                ComputeChase(subjectPos, heading, out desiredPos, out desiredLook);
                fov = Settings.ChaseFov > 0f ? Settings.ChaseFov : SettingsManager.Fov;
                posSmooth = Settings.ChasePositionSmooth;
                lookSmooth = Settings.ChaseLookSmooth;
            }
            else
            {
                Vector3 rawFocus = subjectPos;
                bool faceoff = puck != null && Time.time < _faceoffFocusUntil;
                if (faceoff)
                {
                    // Waiting for the drop: frame the dot, not whoever was followed before.
                    rawFocus = puck.transform.position;
                }
                else if (subjectPlayer != null && puck != null)
                {
                    // Lean toward the puck, fading out with distance rather than
                    // switching off at a hard line (that line was a visible jump).
                    float d = Vector3.Distance(subjectPos, puck.transform.position);
                    float range = Settings.BroadcastPuckBiasRange;
                    float w = Settings.BroadcastPuckBias * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(range * 0.6f, range, d)));
                    rawFocus = Vector3.Lerp(subjectPos, puck.transform.position, w);
                }
                if (_puckTeleported && Settings.CutOnFaceoff)
                {
                    _haveSmoothed = false;      // focus and camera jump straight there
                    _cutPending = true;
                }
                Vector3 focus = StabiliseBroadcastFocus(rawFocus);
                ComputeBroadcast(focus, out desiredPos, out desiredLook, out fov);
                posSmooth = Settings.BroadcastPositionSmooth;
                lookSmooth = Settings.BroadcastLookSmooth;
                ApplyPose(cam, desiredPos, desiredLook, fov, posSmooth, lookSmooth);
                if (puck != null && !_cutPending) KeepInFrame(cam, puck.transform.position);
                return;
            }

            ApplyPose(cam, desiredPos, desiredLook, fov, posSmooth, lookSmooth);
        }

        // -----------------------------------------------------------------
        // Input
        // -----------------------------------------------------------------
        private void HandleInput()
        {
            Keyboard kb = Keyboard.current;
            if (kb == null) return;

            if (MenuOpen)
            {
                if (_capturing.HasValue) CaptureKey(kb);
                else if (Binds.Pressed(kb, CamAction.OpenBindings)) CloseMenu();
                return;                       // no camera actions while rebinding
            }

            // Same gate the vanilla spectator camera uses for its own keys.
            if (GlobalStateManager.UIState.IsMouseRequired) return;

            if (Binds.Pressed(kb, CamAction.OpenBindings)) { OpenMenu(); return; }
            if (Binds.Pressed(kb, CamAction.ToggleOverlay)) _overlay = !_overlay;

            if (Binds.Pressed(kb, CamAction.ToggleAuto))
            {
                _auto = !_auto;
                _hasPending = false;
                if (_auto && _mode == ViewMode.Free) SetMode(_lastFollowMode);
            }

            if (Binds.Pressed(kb, CamAction.NextSubject)) CycleSubject(+1);
            if (Binds.Pressed(kb, CamAction.PrevSubject)) CycleSubject(-1);

            if (Binds.Pressed(kb, CamAction.NextView)) SetMode((ViewMode)(((int)_mode + 1) % 4));
            if (Binds.Pressed(kb, CamAction.PrevView)) SetMode((ViewMode)(((int)_mode + 3) % 4));

            if (Binds.Pressed(kb, CamAction.NextAngle)) CycleAngle(+1);
            if (Binds.Pressed(kb, CamAction.PrevAngle)) CycleAngle(-1);

            if (Binds.Pressed(kb, CamAction.ZoomIn)) Binds.AdjustFov(-Settings.FovStep);
            if (Binds.Pressed(kb, CamAction.ZoomOut)) Binds.AdjustFov(+Settings.FovStep);
            if (Binds.Pressed(kb, CamAction.ZoomReset)) Binds.ResetFov();

            if (Binds.Pressed(kb, CamAction.ShotFirstPerson)) JumpToShot(ViewMode.FirstPerson, 0);
            for (int i = 0; i < 5; i++)
            {
                if (Binds.Pressed(kb, CamAction.ShotChase1 + i)) JumpToShot(ViewMode.Chase, i);
                if (Binds.Pressed(kb, CamAction.ShotBroadcast1 + i)) JumpToShot(ViewMode.Broadcast, i);
            }
        }

        // -----------------------------------------------------------------
        // Key-bind menu
        // -----------------------------------------------------------------
        private CamAction? _capturing;
        private CursorLockMode _savedLock;
        private bool _savedVisible;
        private Vector2 _menuScroll;

        private void OpenMenu()
        {
            MenuOpen = true;
            _capturing = null;
            _savedLock = Cursor.lockState;
            _savedVisible = Cursor.visible;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void CloseMenu()
        {
            if (!MenuOpen) return;
            MenuOpen = false;
            _capturing = null;
            Cursor.lockState = _savedLock;
            Cursor.visible = _savedVisible;
        }

        private void CaptureKey(Keyboard kb)
        {
            if (kb.escapeKey.wasPressedThisFrame) { _capturing = null; return; }
            if (kb.deleteKey.wasPressedThisFrame || kb.backspaceKey.wasPressedThisFrame)
            {
                Binds.Set(_capturing.Value, Key.None);
                _capturing = null;
                return;
            }
            foreach (var key in kb.allKeys)
            {
                if (key == null || !key.wasPressedThisFrame) continue;
                if (key.keyCode == Key.None) continue;
                Binds.Set(_capturing.Value, key.keyCode);
                _capturing = null;
                return;
            }
        }

        private void DrawMenu()
        {
            float w = 460f, h = Mathf.Min(620f, Screen.height - 40f);
            Rect r = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            GUI.Box(r, GUIContent.none);
            GUILayout.BeginArea(new Rect(r.x + 12, r.y + 10, w - 24, h - 20));
            GUILayout.Label("<b><size=16>Broadcast Cam — Key Binds</size></b>", _labelStyle);
            GUILayout.Label(_capturing.HasValue
                ? "<color=#7fd4ff>Press a key for \"" + Binds.Label(_capturing.Value) + "\"  ·  Esc cancel  ·  Delete unbind</color>"
                : "Click a key to change it. A key already in use swaps with it.", _labelStyle);
            GUILayout.Space(6);

            _menuScroll = GUILayout.BeginScrollView(_menuScroll);
            foreach (CamAction a in Binds.All)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Binds.Label(a), _labelStyle, GUILayout.Width(240));
                string text = _capturing == a ? "…" : Binds.KeyName(Binds.Get(a));
                if (GUILayout.Button(text, GUILayout.Width(170))) _capturing = a;
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Reset to defaults")) { Binds.ResetToDefaults(); _capturing = null; }
            if (GUILayout.Button("Close")) CloseMenu();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        private void SetMode(ViewMode mode)
        {
            if (mode == _mode) return;
            if (mode != ViewMode.Free) _lastFollowMode = mode;
            _mode = mode;
            BeginTransition(Settings.CutOnAngleChange);
        }

        private void CycleAngle(int dir)
        {
            if (_mode == ViewMode.Chase)
                _angle[1] = Wrap(_angle[1] + dir, ChaseAngles.Length);
            else if (_mode == ViewMode.Broadcast)
                _angle[2] = Wrap(_angle[2] + dir, BroadcastAngles.Length);
            else return;
            BeginTransition(Settings.CutOnAngleChange);
        }

        private void CycleSubject(int dir)
        {
            RefreshPlayers();
            // Order: puck (index 0), then players.
            int count = _players.Count + 1;
            int current = 0;
            if (_subject.HasValue)
            {
                for (int i = 0; i < _players.Count; i++)
                    if (RealId(_players[i]) == _subject.Value) { current = i + 1; break; }
            }
            int next = Wrap(current + dir, count);
            _subject = next == 0 ? (ulong?)null : RealId(_players[next - 1]);

            _auto = false;              // manual pick overrides auto
            _hasPending = false;
            if (_mode == ViewMode.Free) SetMode(_lastFollowMode);
            BeginTransition(SubjectChangeCuts());
        }

        /// <summary>
        /// Subject changes may cut in Chase/First Person if configured, but never
        /// in Broadcast: a TV camera pans to the new carrier, it doesn't jump.
        /// </summary>
        private bool SubjectChangeCuts()
        {
            return Settings.CutOnSubjectChange && _mode != ViewMode.Broadcast;
        }

        private void BeginTransition(bool cut)
        {
            _haveHeading = false;
            if (cut)
            {
                // Next frame lands exactly on the new shot: position, aim and FOV.
                _haveSmoothed = false;
                _cutPending = true;
                _transitionUntil = 0f;
            }
            else
            {
                _transitionUntil = Time.time + Settings.TransitionTime;
            }
        }

        private void JumpToShot(ViewMode mode, int angle)
        {
            if (mode == ViewMode.Chase) _angle[1] = Wrap(angle, ChaseAngles.Length);
            else if (mode == ViewMode.Broadcast) _angle[2] = Wrap(angle, BroadcastAngles.Length);
            if (mode != ViewMode.Free) _lastFollowMode = mode;
            _mode = mode;
            BeginTransition(true);
        }

        private static int Wrap(int v, int n) { return n <= 0 ? 0 : ((v % n) + n) % n; }

        // -----------------------------------------------------------------
        // Subjects
        // -----------------------------------------------------------------
        private static SpectatorCamera GetLocalSpectatorCamera()
        {
            PlayerManager pm = MonoBehaviourSingleton<PlayerManager>.Instance;
            if (pm == null) return null;
            Player local = pm.GetLocalPlayer();
            return local != null ? local.SpectatorCamera : null;
        }

        /// <summary>During a replay: the replay puck. Otherwise: the live puck.</summary>
        private Puck GetPuck()
        {
            PuckManager pm = MonoBehaviourSingleton<PuckManager>.Instance;
            if (pm == null) return null;
            if (_replaying)
            {
                List<Puck> replayPucks = pm.GetReplayPucks();
                for (int i = 0; i < replayPucks.Count; i++)
                    if (replayPucks[i] != null) return replayPucks[i];
            }
            return pm.GetPuck(false);
        }

        // Replay copies of a player are owned by client (1337 + real id);
        // see PlayerManager.GetReplayPlayerByClientId. Subjects are always
        // stored as the REAL id so the same person stays selected across a
        // replay starting and ending.
        private const ulong ReplayClientIdOffset = 1337UL;

        private static ulong RealId(Player p)
        {
            return (p.IsReplay.Value && p.OwnerClientId >= ReplayClientIdOffset)
                ? p.OwnerClientId - ReplayClientIdOffset
                : p.OwnerClientId;
        }

        private void RefreshPlayers()
        {
            _players.Clear();
            PlayerManager pm = MonoBehaviourSingleton<PlayerManager>.Instance;
            if (pm == null) { SetReplaying(false); return; }

            // A replay spawns separate replay players, sticks and pucks. While
            // any replay body exists, follow those; the live ones are frozen or
            // gone for the duration.
            List<Player> replay = pm.GetReplayPlayers();
            bool replaying = false;
            for (int i = 0; i < replay.Count; i++)
            {
                Player p = replay[i];
                if (p == null || p.PlayerBody == null) continue;
                _players.Add(p);
                replaying = true;
            }
            SetReplaying(replaying);

            if (!replaying)
            {
                List<Player> all = pm.GetPlayers(false);
                if (all == null) return;
                for (int i = 0; i < all.Count; i++)
                {
                    Player p = all[i];
                    if (p == null || p.IsLocalPlayer || !p.IsCharacterSpawned || p.PlayerBody == null) continue;
                    _players.Add(p);
                }
            }
            _players.Sort((a, b) =>
            {
                int t = ((int)a.Team).CompareTo((int)b.Team);
                if (t != 0) return t;
                int r = ((int)a.Role).CompareTo((int)b.Role);
                if (r != 0) return r;
                return a.Number.Value.CompareTo(b.Number.Value);
            });
        }

        private Player FindPlayer(ulong clientId)
        {
            for (int i = 0; i < _players.Count; i++)
                if (_players[i] != null && RealId(_players[i]) == clientId) return _players[i];
            return null;
        }

        private void SetReplaying(bool replaying)
        {
            if (replaying == _replaying) return;
            _replaying = replaying;
            // Everything just teleported: cut straight to the new positions,
            // forget touch history from the other timeline, and put back any
            // body we hid (it may be the one that just despawned).
            _touchServerTime.Clear();
            _touchLocalTime.Clear();
            _hasPending = false;
            _havePuckPos = false;
            ShowHiddenBody();
            BeginTransition(true);
        }

        private Player ResolveSubject(Puck puck)
        {
            if (_subject.HasValue)
            {
                Player p = FindPlayer(_subject.Value);
                if (p != null) return p;
                _subject = null;                          // they left or despawned
                BeginTransition(SubjectChangeCuts());
            }
            if (puck == null && _players.Count > 0)
            {
                // No puck (between faceoffs etc.) - fall back to someone.
                return _players[0];
            }
            return null;
        }

        // -----------------------------------------------------------------
        // Auto-spectate
        // -----------------------------------------------------------------
        private void UpdateAutoSubject(Puck puck)
        {
            if (puck == null) return;

            ulong? carrier = FindCarrier(puck);

            if (!_hasPending || !Nullable.Equals(carrier, _pendingSubject))
            {
                _pendingSubject = carrier;
                _pendingSince = Time.time;
                _hasPending = true;
            }

            if (Nullable.Equals(_pendingSubject, _subject)) return;

            float delay = _pendingSubject.HasValue
                ? Settings.AutoSwitchToPlayerDelay
                : Settings.AutoSwitchToPuckDelay;
            if (Time.time - _pendingSince >= delay)
            {
                _subject = _pendingSubject;
                BeginTransition(SubjectChangeCuts());
            }
        }

        private ulong? FindCarrier(Puck puck)
        {
            Vector3 puckPos = puck.transform.position;

            // 1) Nearest blade within CarrierRadius.
            Player nearest = null;
            float best = Settings.CarrierRadius;
            for (int i = 0; i < _players.Count; i++)
            {
                Player p = _players[i];
                if (p.Stick == null) continue;
                float d = Vector3.Distance(p.Stick.BladeHandlePosition, puckPos);
                if (d < best) { best = d; nearest = p; }
            }

            // 2) Record touches from each stick's synced collision buffer.
            float now = Time.time;
            ulong? recent = null;
            float recentLocal = float.MinValue;
            for (int i = 0; i < _players.Count; i++)
            {
                Player p = _players[i];
                if (p.Stick == null || p.Stick.NetworkObjectCollisionRecorder == null) continue;
                List<NetworkObjectCollision> hits = p.Stick.NetworkObjectCollisionRecorder.NetworkObjectCollisions;
                if (hits == null || hits.Count == 0) continue;

                NetworkObjectCollision last = hits[hits.Count - 1];
                if (NetworkingUtils.GetPuckFromNetworkObjectReference(last.NetworkObjectReference) != puck) continue;

                ulong id = RealId(p);
                float seen;
                if (!_touchServerTime.TryGetValue(id, out seen))
                {
                    // First look at this history: don't treat old touches as new.
                    _touchServerTime[id] = last.Time;
                    _touchLocalTime[id] = float.MinValue;
                }
                else if (!Mathf.Approximately(seen, last.Time))
                {
                    _touchServerTime[id] = last.Time;
                    _touchLocalTime[id] = now;
                }

                float local;
                if (_touchLocalTime.TryGetValue(id, out local) && local > recentLocal)
                {
                    recentLocal = local;
                    recent = id;
                }
            }

            if (nearest != null) return RealId(nearest);
            if (recent.HasValue && now - recentLocal <= Settings.CarrierMemory) return recent;
            return null;
        }

        // -----------------------------------------------------------------
        // Rink frame (detected from the two goals, so it follows arena scalers)
        // -----------------------------------------------------------------
        private void RefreshRink()
        {
            if (Time.time < _nextRinkRefresh) return;
            _nextRinkRefresh = Time.time + 2f;

            Goal[] goals = UnityEngine.Object.FindObjectsByType<Goal>(FindObjectsSortMode.None);
            if (goals != null && goals.Length >= 2)
            {
                Vector3 a = goals[0].transform.position;
                Vector3 b = goals[1].transform.position;
                _endA = a;
                _endB = b;
                _center = (a + b) * 0.5f;
                Vector3 axis = b - a;
                axis.y = 0f;
                _halfGoal = Mathf.Max(5f, axis.magnitude * 0.5f);
                _axisL = axis.sqrMagnitude > 0.01f ? axis.normalized : Vector3.forward;
                _ice = Mathf.Min(a.y, b.y);
            }
            else
            {
                _center = Vector3.zero;
                _axisL = Vector3.forward;
                _halfGoal = 26f;
                _endA = _center - _axisL * _halfGoal;
                _endB = _center + _axisL * _halfGoal;
                _ice = 0f;
            }
            _center.y = _ice;
            _axisW = Vector3.Cross(Vector3.up, _axisL).normalized;

            if (Settings.RinkHalfWidthOverride > 0f)
            {
                _halfWidth = Settings.RinkHalfWidthOverride;
            }
            else
            {
                float hw = _halfGoal * 0.45f;
                Level[] levels = UnityEngine.Object.FindObjectsByType<Level>(FindObjectsSortMode.None);
                if (levels != null && levels.Length > 0 && levels[0] != null)
                {
                    Vector3 ext = levels[0].Bounds.extents;
                    float fromBounds = Mathf.Abs(_axisW.x) * ext.x + Mathf.Abs(_axisW.z) * ext.z;
                    if (fromBounds > 1f) hw = fromBounds;
                }
                // Bounds may include the stands; keep it to a sensible rink ratio.
                _halfWidth = Mathf.Clamp(hw, _halfGoal * 0.3f, _halfGoal * 0.7f);
            }
        }

        // -----------------------------------------------------------------
        // View math
        // -----------------------------------------------------------------
        private bool _everHadPuck, _puckTeleported;
        private float _faceoffFocusUntil = -1f;

        private void TrackPuckVelocity(Puck puck)
        {
            _puckTeleported = false;
            if (puck == null) { _havePuckPos = false; return; }
            Vector3 p = puck.transform.position;
            float dt = Mathf.Max(Time.deltaTime, 0.0001f);

            // A faceoff (after icing, offside, a goal...) puts the puck on a dot,
            // often in the other zone: either it jumps there in one frame, or it
            // is despawned and a new one appears there. Either way that is a new
            // shot, not a pan - panning 50 m at a capped speed is what left the
            // camera behind after icing.
            float jump = (p - _lastPuckPos).magnitude;
            if (_everHadPuck && jump >= Settings.PuckTeleportDistance
                && (!_havePuckPos || jump / dt > 60f))
            {
                _puckTeleported = true;
                _puckVel = Vector3.zero;
                _faceoffFocusUntil = Time.time + Settings.FaceoffFocusTime;
            }
            else if (_havePuckPos)
            {
                _puckVel = Vector3.Lerp(_puckVel, (p - _lastPuckPos) / dt, Mathf.Clamp01(dt * 8f));
            }
            _lastPuckPos = p;
            _havePuckPos = true;
            _everHadPuck = true;

            // Once the puck is dropped and played, go back to normal following.
            if (_faceoffFocusUntil > 0f && Time.time > _faceoffFocusUntil - Settings.FaceoffFocusTime + 0.5f
                && _puckVel.magnitude > 3f)
                _faceoffFocusUntil = -1f;
        }

        private Vector3 GetHeading(Player player, Puck puck)
        {
            Vector3 raw;
            if (player != null)
            {
                raw = player.PlayerBody.transform.forward;
            }
            else
            {
                Vector3 v = _puckVel;
                v.y = 0f;
                raw = v.magnitude > 1.5f
                    ? v
                    : (_haveHeading ? Quaternion.Euler(0f, _headingYaw, 0f) * Vector3.forward : _axisL);
            }
            raw.y = 0f;
            if (raw.sqrMagnitude < 0.0001f) raw = _axisL;

            float targetYaw = Mathf.Atan2(raw.x, raw.z) * Mathf.Rad2Deg;
            if (!_haveHeading)
            {
                _headingYaw = targetYaw;
                _headingYawVel = 0f;
                _haveHeading = true;
            }
            else
            {
                _headingYaw = Mathf.SmoothDampAngle(_headingYaw, targetYaw, ref _headingYawVel, 0.25f);
            }
            return Quaternion.Euler(0f, _headingYaw, 0f) * Vector3.forward;
        }

        private void ComputeChase(Vector3 subject, Vector3 heading, out Vector3 pos, out Vector3 look)
        {
            Vector3 up = Vector3.up;
            Vector3 side = Vector3.Cross(up, heading).normalized;
            float dist = Settings.ChaseDistance;
            float h = Settings.ChaseHeight;
            Vector3 lookAhead = heading * 1.5f;

            switch (_angle[1])
            {
                default:
                case 0: pos = subject - heading * dist + up * h; break;                     // Behind
                case 1: pos = subject - heading * dist * 1.4f + up * h * 2.3f; break;       // Behind High
                case 2: pos = subject - side * dist + up * h; lookAhead = heading * 0.5f; break;   // Left
                case 3: pos = subject + side * dist + up * h; lookAhead = heading * 0.5f; break;   // Right
                case 4: pos = subject + heading * dist + up * h; lookAhead = Vector3.zero; break;  // Front
            }
            look = subject + up * 0.9f + lookAhead;
        }

        private Vector3 _focus, _focusVel;

        /// <summary>
        /// Operator-style framing for the broadcast shots: a dead zone so small
        /// movements are ignored, then a smoothed, speed-capped pan. A change of
        /// possession moves the focus a few metres in one frame; this turns that
        /// into a steady pan instead of a jerk.
        /// </summary>
        private Vector3 StabiliseBroadcastFocus(Vector3 raw)
        {
            if (!_haveSmoothed)
            {
                _focus = raw;
                _focusVel = Vector3.zero;
                return _focus;
            }
            Vector3 off = raw - _focus;
            float mag = off.magnitude;
            float dz = Settings.BroadcastDeadZone;
            Vector3 target = mag > dz ? _focus + off * ((mag - dz) / mag) : _focus;
            // Takeaways and stick battles move the puck slowly and keep the calm,
            // capped pan. A shot or pass is fast: the cap and the lag scale with
            // it, otherwise the camera simply can't keep up with the puck.
            float puckSpeed = _havePuckPos ? _puckVel.magnitude : 0f;
            float fast = Mathf.InverseLerp(8f, 20f, puckSpeed);
            float maxPan = Mathf.Max(Settings.BroadcastMaxPanSpeed, puckSpeed * Settings.PuckPanSpeedFactor);
            float smooth = Mathf.Lerp(Settings.BroadcastFocusSmooth, 0.12f, fast);
            _focus = Vector3.SmoothDamp(_focus, target, ref _focusVel, smooth, maxPan, Time.deltaTime);
            return _focus;
        }

        private void ComputeBroadcast(Vector3 focus, out Vector3 pos, out Vector3 look, out float fov)
        {
            Vector3 up = Vector3.up;
            float along = Vector3.Dot(focus - _center, _axisL);
            float track = Mathf.Clamp(along * Settings.GantryTrack, -_halfGoal * 0.8f, _halfGoal * 0.8f);
            look = focus + up * 0.5f;

            switch (_angle[2])
            {
                default:
                case 0: // Gantry
                    pos = _center + _axisW * (_halfWidth + Settings.GantryBack) + _axisL * track
                          + up * Settings.GantryHeight;
                    fov = Settings.BroadcastFov;
                    break;
                case 1: // Gantry, far side
                    pos = _center - _axisW * (_halfWidth + Settings.GantryBack) + _axisL * track
                          + up * Settings.GantryHeight;
                    fov = Settings.BroadcastFov;
                    break;
                case 2: // End 1
                    pos = EndPosition(_endA);
                    fov = Settings.EndFov;
                    break;
                case 3: // End 2
                    pos = EndPosition(_endB);
                    fov = Settings.EndFov;
                    break;
                case 4: // Overhead
                    pos = focus + up * Settings.OverheadHeight - _axisW * (Settings.OverheadHeight * 0.25f);
                    pos.y = _ice + Settings.OverheadHeight;
                    fov = Settings.OverheadFov;
                    return;
            }

            // Look DOWN on the play by at least BroadcastMinPitch. A fixed height
            // gives a shallow, horizon-heavy shot whenever play is far from the
            // camera (deep in a corner, or along the camera's own boards but far
            // down the ice) - that's the "half the screen is void" picture.
            // Too close: when play comes right up to the camera's own boards the
            // shot used to drop steeply and fill the screen with two players,
            // so fast plays left the frame. Back the camera off instead, so it
            // never gets closer than BroadcastMinDistance (horizontal) to the play.
            Vector3 flat = look - pos; flat.y = 0f;
            float minDist = Settings.BroadcastMinDistance;
            if (_angle[2] <= 3 && flat.magnitude < minDist)
            {
                Vector3 away = flat.sqrMagnitude > 0.01f ? -flat.normalized
                             : (_angle[2] == 0 ? _axisW : _angle[2] == 1 ? -_axisW : Vector3.zero);
                pos += away * (minDist - flat.magnitude);
                flat = look - pos; flat.y = 0f;
            }

            float maxH = _angle[2] <= 1 ? Settings.GantryMaxHeight : Settings.EndMaxHeight;
            float needed = (look.y - _ice) + flat.magnitude * Mathf.Tan(Settings.BroadcastMinPitch * Mathf.Deg2Rad);
            float h = Mathf.Clamp(needed, pos.y - _ice, maxH);
            pos.y = _ice + h;
        }

        /// <summary>
        /// Hard guarantee the puck stays on screen: if, after smoothing, it sits
        /// outside the safe area, rotate the look just far enough to bring it back
        /// to the edge of the safe area. Costs nothing while the puck is framed.
        /// </summary>
        private void KeepInFrame(BaseCamera cam, Vector3 target)
        {
            if (cam.UnityCamera == null) return;
            Vector3 fwd = _look - _pos;
            float dist = fwd.magnitude;
            if (dist < 0.01f) return;
            fwd /= dist;
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            if (right.sqrMagnitude < 1e-6f) return;
            right.Normalize();
            Vector3 upv = Vector3.Cross(fwd, right);

            Vector3 d = target - _pos;
            float z = Vector3.Dot(d, fwd);
            if (z < 0.5f) return;                       // behind/at the lens: leave it to the pan
            float x = Vector3.Dot(d, right) / z;
            float y = Vector3.Dot(d, upv) / z;

            float m = Mathf.Clamp(Settings.PuckFrameMargin, 0.3f, 0.95f);
            float tanV = Mathf.Tan(cam.UnityCamera.fieldOfView * 0.5f * Mathf.Deg2Rad) * m;
            float tanH = tanV * Mathf.Max(0.5f, cam.UnityCamera.aspect);

            float ex = Mathf.Sign(x) * Mathf.Max(0f, Mathf.Abs(x) - tanH);
            float ey = Mathf.Sign(y) * Mathf.Max(0f, Mathf.Abs(y) - tanV);
            if (ex == 0f && ey == 0f) return;

            Vector3 newFwd = (fwd + right * ex + upv * ey).normalized;
            Vector3 newLook = _pos + newFwd * dist;
            // Carry the correction into the smoother so it doesn't spring back.
            _look = newLook;
            _lookVel = Vector3.zero;
            SetPose(cam, _pos, Quaternion.LookRotation(newFwd, Vector3.up));
        }

        private Vector3 EndPosition(Vector3 goal)
        {
            Vector3 outward = goal - _center;
            outward.y = 0f;
            outward = outward.sqrMagnitude > 0.01f ? outward.normalized : _axisL;
            Vector3 p = goal + outward * Settings.EndBack;
            p.y = _ice + Settings.EndHeight;
            return p;
        }

        private void ApplyPose(BaseCamera cam, Vector3 desiredPos, Vector3 desiredLook, float fov,
                               float posSmooth, float lookSmooth)
        {
            float dt = Time.deltaTime;
            if (Time.time < _transitionUntil)
            {
                // Slightly softer while gliding to a new shot.
                posSmooth = Mathf.Max(posSmooth, 0.2f);
                lookSmooth = Mathf.Max(lookSmooth, 0.15f);
            }

            if (!_haveSmoothed)
            {
                _pos = desiredPos;
                _look = desiredLook;
                _posVel = Vector3.zero;
                _lookVel = Vector3.zero;
                _haveSmoothed = true;
            }
            else
            {
                _pos = Vector3.SmoothDamp(_pos, desiredPos, ref _posVel, posSmooth, float.PositiveInfinity, dt);
                _look = Vector3.SmoothDamp(_look, desiredLook, ref _lookVel, lookSmooth, float.PositiveInfinity, dt);
            }

            Vector3 dir = _look - _pos;
            Quaternion rot = dir.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(dir, Vector3.up) : CurrentRotation(cam);
            SetPose(cam, _pos, rot);
            SetFov(cam, fov, dt);
            _cutPending = false;
        }

        private void ApplyFirstPerson(BaseCamera cam, Player player)
        {
            PlayerBody body = player.PlayerBody;

            // The PlayerCamera is parented to the body and its localRotation is
            // the relayed look angle, so rebuild it from the parent + look angle
            // rather than relying on the remote camera being ticked.
            PlayerCamera pc = player.PlayerCamera;
            Transform basis = body.transform;
            Vector3 pos = GetEyePosition(body, pc);
            Vector2 look = player.PlayerInput != null ? player.PlayerInput.LookAngleInput.ServerValue : Vector2.zero;
            Quaternion rot = basis.rotation * Quaternion.Euler(look.x, look.y, 0f);

            if (Settings.FirstPersonLevelHorizon)
            {
                Vector3 fwd = rot * Vector3.forward;
                if (Mathf.Abs(Vector3.Dot(fwd, Vector3.up)) < 0.99f)
                    rot = Quaternion.LookRotation(fwd, Vector3.up);
            }

            if (!_haveSmoothed || _cutPending)
            {
                SetPose(cam, pos, rot);
            }
            else
            {
                float k = 1f - Mathf.Exp(-Settings.FirstPersonRotationSharpness * Time.deltaTime);
                if (Time.time < _transitionUntil)
                {
                    // Glide in from wherever the previous shot was.
                    float g = 1f - Mathf.Exp(-10f * Time.deltaTime);
                    pos = Vector3.Lerp(CurrentPosition(cam), pos, g);
                }
                SetPose(cam, pos, Quaternion.Slerp(CurrentRotation(cam), rot, k));
            }

            // Keep the chase/broadcast smoothers in step so leaving FP doesn't jump.
            _pos = _finalPos;
            _look = _pos + (_finalRot * Vector3.forward) * 5f;
            _posVel = Vector3.zero;
            _lookVel = Vector3.zero;
            _haveSmoothed = true;

            // Their own head and body would fill the screen.
            if (_hiddenBody != body)
            {
                ShowHiddenBody();
                if (body.MeshRendererHider != null)
                {
                    body.MeshRendererHider.HideMeshRenderers();
                    _hiddenBody = body;
                }
            }

            SetFov(cam, SettingsManager.Fov, Time.deltaTime);
            _cutPending = false;
        }

        /// <summary>
        /// Where a player's eyes are. Their PlayerCamera is normally right there,
        /// but on some clients a remote player's camera ends up at the body's
        /// origin, down on the ice (seen for some viewers and not others, so it
        /// depends on how that client received the camera). Trust the camera
        /// only when it is actually near the head; otherwise use the head.
        /// </summary>
        private static Vector3 GetEyePosition(PlayerBody body, PlayerCamera pc)
        {
            Transform head = (body.PlayerMesh != null && body.PlayerMesh.PlayerHead != null)
                ? body.PlayerMesh.PlayerHead.transform
                : null;

            if (pc != null)
            {
                Vector3 camPos = pc.transform.position;
                if (head == null || (camPos - head.position).sqrMagnitude
                                    <= Settings.FirstPersonMaxCameraToHead * Settings.FirstPersonMaxCameraToHead)
                    return camPos;
            }
            if (head != null)
                return head.position + Vector3.up * Settings.FirstPersonEyeUp
                                     + body.transform.forward * Settings.FirstPersonEyeForward;
            return body.transform.position + Vector3.up * 1.6f;
        }

        private void SetFov(BaseCamera cam, float fov, float dt)
        {
            if (cam.UnityCamera == null) return;
            // Player's own zoom, adjustable in-game and remembered.
            fov = Mathf.Clamp(fov + Binds.FovOffset, Settings.FovMin, Settings.FovMax);
            if (_cutPending) { cam.UnityCamera.fieldOfView = fov; return; }
            cam.UnityCamera.fieldOfView = Mathf.Lerp(cam.UnityCamera.fieldOfView, fov, 1f - Mathf.Exp(-6f * dt));
        }

        // -----------------------------------------------------------------
        // Hand-back and cleanup
        // -----------------------------------------------------------------
        // -----------------------------------------------------------------
        // Pose bookkeeping. The pose is re-applied just before the camera
        // renders, so nothing else that touches the transform later in the
        // frame (e.g. the game's own look-angle update on the goalie camera)
        // can show through.
        // -----------------------------------------------------------------
        private Vector3 _finalPos;
        private Quaternion _finalRot = Quaternion.identity;
        private bool _hasFinal;

        private void SetPose(BaseCamera cam, Vector3 pos, Quaternion rot)
        {
            _finalPos = pos;
            _finalRot = rot;
            _hasFinal = true;
            cam.transform.SetPositionAndRotation(pos, rot);
        }

        private Vector3 CurrentPosition(BaseCamera cam) { return _hasFinal ? _finalPos : cam.transform.position; }
        private Quaternion CurrentRotation(BaseCamera cam) { return _hasFinal ? _finalRot : cam.transform.rotation; }

        private void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (!Active || !_hasFinal || _cam == null || _cam.UnityCamera != camera) return;
            camera.transform.SetPositionAndRotation(_finalPos, _finalRot);
        }

        // -----------------------------------------------------------------
        // Free fly for the goalie camera (mirrors the vanilla spectator camera)
        // -----------------------------------------------------------------
        private bool _flyInit;
        private Vector3 _flyPos, _flyVel;
        private float _flyPitch, _flyYaw, _flyTargetPitch, _flyTargetYaw;

        private void FreeFly(BaseCamera cam)
        {
            float dt = Time.deltaTime;
            if (!_flyInit)
            {
                _flyPos = CurrentPosition(cam);
                _flyVel = Vector3.zero;
                Vector3 e = CurrentRotation(cam).eulerAngles;
                _flyPitch = _flyTargetPitch = Mathf.Clamp(e.x > 180f ? e.x - 360f : e.x, -89f, 89f);
                _flyYaw = _flyTargetYaw = e.y;
                _flyInit = true;
            }

            float right = 0f, up = 0f, fwd = 0f;
            bool fast = false;
            Vector2 look = Vector2.zero;
            if (!MenuOpen && !GlobalStateManager.UIState.IsMouseRequired)
            {
                right = (InputManager.TurnRightAction.IsPressed() ? 1f : 0f) - (InputManager.TurnLeftAction.IsPressed() ? 1f : 0f);
                up = InputManager.JumpAction.IsPressed() ? 1f : (InputManager.SlideAction.IsPressed() ? -1f : 0f);
                fwd = (InputManager.MoveForwardAction.IsPressed() ? 1f : 0f) - (InputManager.MoveBackwardAction.IsPressed() ? 1f : 0f);
                fast = InputManager.SprintAction.IsPressed();
                Vector2 v = InputManager.StickAction.ReadValue<Vector2>();
                look = new Vector2(float.IsNaN(v.x) ? 0f : v.x * SettingsManager.LookSensitivity,
                                   float.IsNaN(v.y) ? 0f : v.y * SettingsManager.LookSensitivity);
            }

            Quaternion rot = Quaternion.Euler(_flyPitch, _flyYaw, 0f);
            float speed = Settings.FreeFlySpeed * (fast ? 2f : 1f);
            _flyPos += (rot * Vector3.right) * right * speed * dt;
            _flyPos += (rot * Vector3.up) * up * speed * dt;
            _flyPos += (rot * Vector3.forward) * fwd * speed * dt;

            _flyTargetPitch = Mathf.Clamp(_flyTargetPitch - look.y, -89f, 89f);
            _flyTargetYaw += look.x;
            _flyPitch = Mathf.Lerp(_flyPitch, _flyTargetPitch, 10f * dt);
            _flyYaw = Mathf.Lerp(_flyYaw, _flyTargetYaw, 10f * dt);

            Vector3 pos = Vector3.SmoothDamp(CurrentPosition(cam), _flyPos, ref _flyVel, 0.25f, float.PositiveInfinity, dt);
            SetPose(cam, pos, Quaternion.Euler(_flyPitch, _flyYaw, 0f));
            SetFov(cam, SettingsManager.Fov, dt);
            _cutPending = false;
            _haveSmoothed = false;   // follow modes start clean from here
        }

        // -----------------------------------------------------------------
        // Goalie spectate
        // -----------------------------------------------------------------
        private void GoalieTick()
        {
            PlayerManager pm = MonoBehaviourSingleton<PlayerManager>.Instance;
            Player local = pm != null ? pm.GetLocalPlayer() : null;
            bool eligible = local != null
                            && local.Role == PlayerRole.Goalie
                            && local.Phase == PlayerPhase.Play
                            && local.IsCharacterSpawned
                            && local.PlayerCamera != null
                            && local.PlayerCamera.IsEnabled;

            // Leave automatically if anything about the goalie changes (goal
            // and replay, position swap, despawn, team change ...), and on a
            // faceoff. A faceoff respawns or teleports the goalie: the game
            // switched the view back to first person but inputs stayed
            // suspended, so the goalie had to press the key again to move.
            // Now both come back together.
            if (GoalieSpectate.Active)
            {
                bool changed = !eligible
                               || local != GoalieSpectate.Player
                               || GoalieSpectate.Camera == null
                               || local.PlayerCamera != GoalieSpectate.Camera
                               || local.PlayerBody != GoalieSpectate.Body;
                bool faceoff = !changed && Settings.GoalieExitOnFaceoff && GoalieSpectate.WasTeleported();
                if (changed || faceoff)
                {
                    ExitGoalieSpectate();
                    return;
                }
            }

            if (MenuOpen) return;
            Keyboard kb = Keyboard.current;
            if (kb == null || GlobalStateManager.UIState.IsMouseRequired) return;
            if (!Binds.Pressed(kb, CamAction.GoalieSpectate)) return;

            if (GoalieSpectate.Active) ExitGoalieSpectate();
            else if (eligible) EnterGoalieSpectate(local);
        }

        private void EnterGoalieSpectate(Player local)
        {
            GoalieSpectate.Enter(local);
            _cam = null;               // host switch is picked up this frame
            _haveSmoothed = false;
            _hasFinal = false;
            _flyInit = false;
            _cutPending = true;
            if (_mode == ViewMode.Free) _mode = _lastFollowMode;
        }

        private void ExitGoalieSpectate()
        {
            if (!GoalieSpectate.Active) return;
            CloseMenu();
            ShowHiddenBody();
            GoalieSpectate.Exit();
            Active = false;
            _cam = null;
            _hasFinal = false;
            _haveSmoothed = false;
            _flyInit = false;
        }

        private void Deactivate()
        {
            if (Active && _cam != null && _cam.UnityCamera != null)
                _cam.UnityCamera.fieldOfView = SettingsManager.Fov;
            Active = false;
            _cam = null;
            _haveSmoothed = false;
            _hasFinal = false;
            ShowHiddenBody();
        }

        private void ReleaseToFree(SpectatorCamera cam)
        {
            if (cam == null) return;
            if (FlyRefsOk)
            {
                Vector3 e = cam.transform.eulerAngles;
                float pitch = e.x > 180f ? e.x - 360f : e.x;
                pitch = Mathf.Clamp(pitch, -89f, 89f);
                FlyPos(cam) = cam.transform.position;
                FlyVel(cam) = Vector3.zero;
                FlyPitch(cam) = pitch;
                FlyTargetPitch(cam) = pitch;
                FlyYaw(cam) = e.y;
                FlyTargetYaw(cam) = e.y;
            }
            if (cam.UnityCamera != null) cam.UnityCamera.fieldOfView = SettingsManager.Fov;
            _haveSmoothed = false;
        }

        private void ShowHiddenBody()
        {
            if (_hiddenBody != null && _hiddenBody.MeshRendererHider != null)
                _hiddenBody.MeshRendererHider.ShowMeshRenderers();
            _hiddenBody = null;
        }

        // -----------------------------------------------------------------
        // Overlay
        // -----------------------------------------------------------------
        private GUIStyle _style;
        private GUIStyle _labelStyle;

        private void OnGUI()
        {
            BaseCamera cam = GoalieSpectate.Active
                ? (BaseCamera)GoalieSpectate.Camera
                : GetLocalSpectatorCamera();
            if (cam == null || !cam.IsEnabled) return;

            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.box)
                {
                    alignment = TextAnchor.MiddleLeft,
                    fontSize = 14,
                    richText = true,
                    padding = new RectOffset(10, 10, 6, 6)
                };
                _labelStyle = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 13, wordWrap = true };
            }

            if (MenuOpen)
            {
                DrawMenu();
                return;
            }
            if (!_overlay) return;

            string subject = "Puck";
            if (_subject.HasValue)
            {
                Player p = FindPlayer(_subject.Value);
                if (p != null)
                    subject = "#" + p.Number.Value + " " + p.Username.Value.ToString() + " (" + p.Team + ")";
            }

            string angle = "";
            if (_mode == ViewMode.Chase) angle = " · " + ChaseAngles[_angle[1]];
            else if (_mode == ViewMode.Broadcast) angle = " · " + BroadcastAngles[_angle[2]];

            string line1 = _mode == ViewMode.Free
                ? "<b>FREE CAM</b>"
                : "<b>" + subject + "</b>  —  " + ViewNames[(int)_mode] + angle
                  + (_auto ? "  <color=#7fd4ff>AUTO</color>" : "")
                  + (_replaying ? "  <color=#ffcf5c>REPLAY</color>" : "");
            if (GoalieSpectate.Active)
                line1 = "<color=#ff9a5c><b>GOALIE SPECTATE</b> — movement paused · "
                        + K(CamAction.GoalieSpectate) + " to return</color>\n" + line1;
            string line2 = "<size=11>"
                + K(CamAction.PrevSubject) + "/" + K(CamAction.NextSubject) + " subject   "
                + K(CamAction.PrevView) + "/" + K(CamAction.NextView) + " view   "
                + K(CamAction.PrevAngle) + "/" + K(CamAction.NextAngle) + " angle   "
                + K(CamAction.ZoomIn) + "/" + K(CamAction.ZoomOut) + " zoom"
                + (Mathf.Abs(Binds.FovOffset) > 0.01f ? " (" + (Binds.FovOffset > 0f ? "+" : "") + Binds.FovOffset.ToString("0") + "°)" : "") + "   "
                + K(CamAction.ToggleAuto) + " auto   "
                + K(CamAction.OpenBindings) + " keys   "
                + K(CamAction.ToggleOverlay) + " hide</size>";

            float boxH = GoalieSpectate.Active ? 80f : 60f;
            GUI.Box(new Rect(16, Screen.height - 16 - boxH, 880, boxH), line1 + "\n" + line2, _style);
        }

        private static string K(CamAction a) { return Binds.KeyName(Binds.Get(a)); }
    }

    // =====================================================================
    // GOALIE SPECTATE — state, and suspension of the goalie's own inputs
    // =====================================================================
    /// <summary>
    /// A goalie in play can step out of their body into the broadcast camera.
    /// Their body stays on the ice; every movement, stick and action input
    /// from this client is held neutral until they step back in. Client-side:
    /// the server just sees a goalie whose inputs aren't changing.
    /// </summary>
    public static class GoalieSpectate
    {
        public static bool Active;
        public static Player Player;
        public static PlayerCamera Camera;
        public static PlayerBody Body;
        private static Vector3 _lastBodyPos;

        private static Vector3 _savedLocalPos;
        private static Quaternion _savedLocalRot;
        private static bool _meshWasHidden;

        public static void Enter(Player p)
        {
            Player = p;
            Camera = p.PlayerCamera;
            Active = true;

            Body = p.PlayerBody;
            _lastBodyPos = Body != null ? Body.transform.position : Vector3.zero;
            _savedLocalPos = Camera.transform.localPosition;
            _savedLocalRot = Camera.transform.localRotation;

            // In first person your own body is usually hidden; show it while
            // you're watching from outside, put it back afterwards.
            _meshWasHidden = false;
            MeshRendererHider hider = p.PlayerBody != null ? p.PlayerBody.MeshRendererHider : null;
            if (hider != null && hider.meshRenderers != null)
            {
                foreach (MeshRenderer r in hider.meshRenderers)
                    if (r != null && !r.enabled) { _meshWasHidden = true; break; }
                if (_meshWasHidden) hider.ShowMeshRenderers();
            }

            if (p.PlayerInput != null) Neutralise(p.PlayerInput);
        }

        /// <summary>
        /// True if the goalie's body jumped further in one frame than skating
        /// could move it - the server repositioning them for a faceoff.
        /// </summary>
        public static bool WasTeleported()
        {
            if (Body == null) return false;
            Vector3 now = Body.transform.position;
            float jump = (now - _lastBodyPos).magnitude;
            _lastBodyPos = now;
            return jump > Settings.GoalieTeleportDistance;
        }

        public static void Exit()
        {
            if (!Active) return;
            Active = false;

            if (Camera != null)
            {
                Camera.transform.localPosition = _savedLocalPos;
                Camera.transform.localRotation = _savedLocalRot;
                if (Camera.UnityCamera != null) Camera.UnityCamera.fieldOfView = SettingsManager.Fov;
            }
            if (_meshWasHidden && Player != null && Player.PlayerBody != null
                && Player.PlayerBody.MeshRendererHider != null)
            {
                Player.PlayerBody.MeshRendererHider.HideMeshRenderers();
            }
            Player = null;
            Camera = null;
            Body = null;
        }

        public static bool Suspends(global::PlayerInput input)
        {
            return Active && input != null && Player != null && input.gameObject == Player.gameObject;
        }

        /// <summary>Everything that moves the body goes to rest. The stick stays where it was.</summary>
        public static void Neutralise(global::PlayerInput input)
        {
            input.MoveInput.ClientValue = Vector2.zero;
            input.SlideInput.ClientValue = false;
            input.SprintInput.ClientValue = false;
            input.TrackInput.ClientValue = false;
            input.LookInput.ClientValue = false;
            input.ExtendLeftInput.ClientValue = false;
            input.ExtendRightInput.ClientValue = false;
            input.LateralLeftInput.ClientValue = false;
            input.LateralRightInput.ClientValue = false;
            input.StopInput.ClientValue = false;
        }
    }

    [HarmonyPatch(typeof(global::PlayerInput), "UpdateInputs")]
    public static class PlayerInput_UpdateInputs_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(global::PlayerInput __instance)
        {
            if (!GoalieSpectate.Suspends(__instance)) return true;
            GoalieSpectate.Neutralise(__instance);
            // Push-to-talk keeps working while you watch.
            __instance.TalkInput.ClientValue = !GlobalStateManager.UIState.IsMouseRequired
                                               && InputManager.TalkAction.IsInProgress();
            return false;
        }
    }

    [HarmonyPatch(typeof(global::PlayerInput), "UpdateLookAngle")]
    public static class PlayerInput_UpdateLookAngle_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(global::PlayerInput __instance)
        {
            // The mouse is flying the camera, not turning the goalie's head.
            return !GoalieSpectate.Suspends(__instance);
        }
    }

    /// <summary>Blade angle, jump, twist and dash are event-driven; block those too.</summary>
    [HarmonyPatch]
    public static class PlayerInput_ActionEvents_Patch
    {
        private static readonly string[] Names =
        {
            "OnBladeAngleUpActionPerformed", "OnBladeAngleDownActionPerformed",
            "OnJumpActionPerformed", "OnTwistLeftActionPerformed", "OnTwistRightActionPerformed",
            "OnDashLeftActionPerformed", "OnDashRightActionPerformed"
        };

        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (string n in Names)
            {
                MethodInfo m = AccessTools.Method(typeof(global::PlayerInput), n);
                if (m != null) yield return m;
            }
        }

        [HarmonyPrefix]
        public static bool Prefix(global::PlayerInput __instance)
        {
            return !GoalieSpectate.Suspends(__instance);
        }
    }
}