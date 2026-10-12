// FlyoutMouseAim: War Thunder-style mouse aim for Flyout.
//
// While Flyout's mouse flight is on (F2) this mod takes over the three things that make War
// Thunder's mouse aim what it is:
//   1. Camera: the mouse turns the camera directly. The camera sits behind the aircraft along
//      the aim direction, keeps the horizon level, and the plane follows it.
//   2. Markers: an aim circle (where you want to go) and a nose cross (where the nose points).
//   3. Instructor: a predictive desired-direction flight controller flies the aircraft through
//      its normal controls toward the aim circle (see Instructor.cs).
//
// Engine hooks (verified in the ISIL dump):
//   * CraftControls.UpdateControls (from Craft.Update) calls MouseController.UpdateController()
//     and copies mc.pitch/yaw/roll into rawInput -> postfix on UpdateController sets the inputs.
//     Flyout's autopilot skips UpdateController, so the autopilot keeps ownership while engaged.
//   * CameraController positions the mouse-flight camera (MouseFlightMode, in FixedUpdate) and
//     runs LateUpdate -> postfix on CameraController.LateUpdate sets the final camera pose.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;
using NVec3 = System.Numerics.Vector3;

[assembly: MelonInfo(typeof(FlyoutMouseAim.MouseAimMod), "War Thunder Style Mouse Controls", "1.0.0", "itayo")]
[assembly: MelonGame("Stonext Games", "Flyout")]

namespace FlyoutMouseAim
{
    public sealed class ModSettings
    {
        public bool Enabled = true;
        public bool ShowHud = true;
        public bool BypassNativeFbw = true;
        public bool ArtificialYawStability = true;
        public float YawStabilityStrength = .65f;
        public float YawStabilityMaxAcceleration = .5f; // rad/s^2 before strength/fade
        public string ToggleKey = "F7";
        public string HudKey = "F6";
        public string EmergencyKey = "F9";
        public string FreeLookKey = "LeftAlt";
        public float MouseSensitivity = 0.09f;    // degrees per mouse count
        public float PrecisionSensitivity = 0.4f;
        public float FullSensitivitySpeed = 1000f;
        public float MouseSmoothing = 0.025f;
        public string HelpKey = "F12";
        public bool InvertY = false;
        public float CameraDistance = 0f;         // m; 0 = automatic from aircraft size
        public float CameraHeight = 0.22f;        // fraction of distance above the aim line
        public float CameraSmoothing = 0.05f;     // s, camera rotation follows the aim
        public float FreeLookReturn = 0.15f;      // s
        public float ManualDeadzone = 0.08f;
        public bool ManualPitchOverride = true;
        public float ManualReturnTime = 0.35f;
        public InstructorConfig Instructor = new InstructorConfig();
    }

    public class MouseAimMod : MelonMod
    {
        internal static MouseAimMod I;
        internal ModSettings S = new ModSettings();
        internal Instructor Ins;

        float sP = -1f, sY = 1f, sR = 1f;          // raw = sign * control-convention
        bool signsRead;
        internal readonly AoaEnvelope Envelope = new();
        readonly WingEnvelope wingEnvelope = new();
        readonly MouseResponse mouseResponse = new();
        readonly YawStabilizer yawStabilizer = new();
        float yawAssistDelta, yawRateBefore, yawRateAfter;
        bool yawAssistActive;
        bool showHelp, wasFreeLooking, suspended, previouslyOwned;
        IntPtr helpFlightCraft;
        double automaticHelpUntil;
        bool lastFbwBypassed;
        float separation;
        double testUntil;
        bool testRunning, priorBackground;
        bool TestActive => testRunning && Time.realtimeSinceStartupAsDouble < testUntil;
        internal object TestSession(float seconds)
        {
            if (!float.IsFinite(seconds) || seconds < 0 || seconds > 180) throw new Exception("Test seconds must be 0..180.");
            if (seconds == 0) { EndTest(); return new { test = false }; }
            var c = ActiveCraft();
            if (c == null || CameraController.instance == null) throw new Exception("Launch an aircraft first.");
            if (c.Pointer != craftPtr) { ResetState(); craftPtr = c.Pointer; }
            if (!testRunning) priorBackground = Application.runInBackground;
            testRunning = true; testUntil = Time.realtimeSinceStartupAsDouble + seconds;
            Application.runInBackground = true;
            CameraController.instance.SetMode(CameraController.Mode.MouseFlight);
            MouseController.instance.enabled = true;
            S.Enabled = true; InitAim(c); Ins.Reset();
            return new { test = true, seconds, expiryPausesGame = true };
        }
        void ResetYawAssist()
        {
            yawStabilizer.Reset(); yawAssistDelta = 0; yawAssistActive = false;
        }
        void EndTest()
        {
            if (!testRunning) return;
            ResetYawAssist();
            testRunning = false; Application.runInBackground = priorBackground;
            Ins.Reset(); mouseResponse.Reset(); GameLogic.SetPause(true);
        }

        // aim / camera state (world space)
        bool aimInit;
        Vector3 aimDir;                             // where the player wants to fly
        Quaternion rigRot;                          // camera rig: looks along aimDir, horizon level
        Quaternion freeLook = Quaternion.identity;  // extra camera rotation while free looking
        Vector3 localCom;
        float zoom = 1f;
        IntPtr craftPtr;
        bool nativeHudHidden;

        NVec3 manualW;
        internal InstructorOutput Last;
        internal string Owner = "none";
        internal NVec3 LastRaw, LastManual, LastBodyRates;
        int errorCount;
        float lastErrorLog = -999f;
        string settingsPath, ioRoot;
        static readonly JsonSerializerOptions Json = new JsonSerializerOptions { IncludeFields = true, WriteIndented = true };

        readonly ConcurrentQueue<string> telemetry = new ConcurrentQueue<string>();
        volatile bool recording;
        double recordUntil;
        string recordFile;

        object bridgeCore; FieldInfo bridgeField; bool bridgeLookedUp;

        public override void OnInitializeMelon()
        {
            I = this;
            settingsPath = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "FlyoutMouseAim.json");
            LoadSettings();
            Ins = new Instructor(S.Instructor);
            ioRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low",
                "Stonext Games", "Flyout", "McpBridge", "MouseAim");
            Directory.CreateDirectory(Path.Combine(ioRoot, "requests"));
            Directory.CreateDirectory(Path.Combine(ioRoot, "responses"));
            new Thread(WriterLoop) { IsBackground = true, Name = "MouseAimTelemetry" }.Start();
            LoggerInstance.Msg($"War Thunder Style Mouse Controls v1 ready. F12 = help. F2 = mouse flight, {S.ToggleKey} = on/off, {S.FreeLookKey} = free look, {S.HudKey} = HUD, {S.EmergencyKey} = release.");
        }

        internal void LoadSettings()
        {
            try
            {
                if (File.Exists(settingsPath))
                {
                    string txt = File.ReadAllText(settingsPath);
                    S = txt.Contains("\"InstructorEnabled\"")       // v0.1 file: start fresh with new defaults
                        ? new ModSettings()
                        : JsonSerializer.Deserialize<ModSettings>(txt, Json) ?? new ModSettings();
                }
            }
            catch (Exception e) { LoggerInstance.Warning("Settings unreadable, using defaults: " + e.Message); S = new ModSettings(); }
            ValidateSettings();
            SaveSettings();
        }

        internal void ValidateSettings()
        {
            S.Instructor ??= new InstructorConfig();
            static float Clamp(float x, float min, float max, float fallback) => float.IsFinite(x) ? Math.Clamp(x, min, max) : fallback;
            S.YawStabilityStrength = Clamp(S.YawStabilityStrength, 0, 1, .65f);
            S.YawStabilityMaxAcceleration = Clamp(S.YawStabilityMaxAcceleration, .05f, 1.5f, .5f);
            S.MouseSensitivity = Clamp(S.MouseSensitivity, .005f, .5f, .09f);
            S.PrecisionSensitivity = Clamp(S.PrecisionSensitivity, .1f, 1f, .4f);
            S.FullSensitivitySpeed = Clamp(S.FullSensitivitySpeed, 50, 10000, 1000);
            S.MouseSmoothing = Clamp(S.MouseSmoothing, 0, .15f, .025f);
            S.Instructor.AoALimitDeg = Clamp(S.Instructor.AoALimitDeg, 4, 45, 14);
            S.Instructor.AoAMarginDeg = Clamp(S.Instructor.AoAMarginDeg, .5f, 8, 2);
            S.Instructor.FineBankGain = Clamp(S.Instructor.FineBankGain, 0, 5, 2.5f);
            S.Instructor.FineDamping = Clamp(S.Instructor.FineDamping, 1, 3, 1.3f);
            S.Instructor.MinSpeed = Clamp(S.Instructor.MinSpeed, 1, 100, 15);
            S.Instructor.TerrainFloor = Clamp(S.Instructor.TerrainFloor, 1, 1000, 30);
        }

        internal void SaveSettings()
        {
            try { File.WriteAllText(settingsPath, JsonSerializer.Serialize(S, Json)); }
            catch (Exception e) { LoggerInstance.Warning("Could not save settings: " + e.Message); }
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            ResetState();
            helpFlightCraft = IntPtr.Zero;
            automaticHelpUntil = 0;
            showHelp = false;
        }

        void UpdateFlightHelp()
        {
            var c = ActiveCraft();
            double now = Time.realtimeSinceStartupAsDouble;
            // Separate from controller resets: F2/F7 must not restart the popup.
            if (c != null && CameraController.instance != null && Time.timeScale > 0 && c.Pointer != helpFlightCraft)
            {
                helpFlightCraft = c.Pointer;
                automaticHelpUntil = now + 6;
                showHelp = true;
            }
            if (automaticHelpUntil > 0 && now >= automaticHelpUntil)
            {
                automaticHelpUntil = 0;
                showHelp = false;
            }
        }

        void ResetState()
        {
            EndTest();
            RestoreNativeHud();
            aimInit = false; craftPtr = IntPtr.Zero; signsRead = false; manualW = default;
            freeLook = Quaternion.identity; nativeHudHidden = false; wasFreeLooking = false; Last = default; Owner = "none";
            Ins?.Reset(); yawStabilizer.Reset(); yawAssistDelta = 0; yawAssistActive = false; Envelope.Reset(); wingEnvelope.Reset(); mouseResponse.Reset();
            LastRaw = LastManual = default; suspended = false; separation = float.NaN;
        }

        static Craft ActiveCraft()
        {
            var c = Craft.active;
            return c != null && c.initialized ? c : null;
        }

        /// <summary>True when our War Thunder mode owns camera, markers and controls.</summary>
        bool Active(out Craft c, out MouseController mc)
        {
            c = ActiveCraft();
            mc = MouseController.instance;
            if (!S.Enabled || c == null || mc == null || !mc.enabled) return false;
            if ((int)CameraController.mode != 8) return false;          // Mode.MouseFlight
            if (c.Pointer != craftPtr) { ResetState(); craftPtr = c.Pointer; }
            return true;
        }

        static NVec3 N(Vector3 v) => new NVec3(v.x, v.y, v.z);
        static Vector3 U(NVec3 v) => new Vector3(v.X, v.Y, v.Z);
        static Vector3 PlanetUp(Vector3 pos) { var u = WorldScene.UpDirection(pos); return u.sqrMagnitude > 1e-6f ? u.normalized : Vector3.up; }

        Vector3 Pivot(Craft c) => c.transform.TransformPoint(localCom);

        public override void OnFixedUpdate()
        {
            try
            {
                var c = ActiveCraft();
                var rb = c?.masterRB?.rb;
                if (rb != null) localCom = c.transform.InverseTransformPoint(rb.worldCenterOfMass);
                ApplyYawStability(c);
            }
            catch (Exception e) { ReportError("fixed", e); }
        }

        void ApplyYawStability(Craft craft)
        {
            yawAssistDelta = 0; yawAssistActive = false;
            if (!S.ArtificialYawStability || !Active(out var c, out var mc) || craft == null || c.Pointer != craft.Pointer ||
                Time.timeScale <= 0 || (!Application.isFocused && !TestActive) || InputLock.lockInput ||
                c.craftControls?.autopilot?.enabled == true || BridgeOwnsControls(out _,out _,out _) ||
                MathF.Abs(mc.kbYaw) > S.ManualDeadzone || MathF.Abs(InputManager.m_yaw) > S.ManualDeadzone ||
                manualW.Y > .05f || c.radarAlt < 8 || Last.Spin)
            { yawStabilizer.Reset(); return; }
            var rb = c.masterRB?.rb;
            if (rb == null || rb.isKinematic) { yawStabilizer.Reset(); return; }
            var air = c.transform.InverseTransformDirection(c.airspeed);
            var axis = c.transform.up.normalized;
            var before = rb.angularVelocity;
            yawRateBefore = Vector3.Dot(before,axis);
            float beta = Mathf.Atan2(air.x,Mathf.Max(air.z,.001f));
            float alpha = Mathf.Atan2(-air.y,Mathf.Max(air.z,.001f));
            yawAssistDelta = yawStabilizer.Step(N(c.airspeed), N(axis), yawRateBefore, beta, alpha, Time.fixedDeltaTime,
                S.YawStabilityStrength, S.YawStabilityMaxAcceleration, air.z > 20, Last.ErrRight, Last.AimErrorDeg);
            // This single body-yaw projection is the only artificial aircraft-physics write.
            // Do not replace it with global angular drag or a three-axis rate clamp.
            if (yawAssistDelta != 0) rb.angularVelocity = U(YawStabilizer.Apply(N(before), N(axis), yawAssistDelta));
            yawRateAfter = Vector3.Dot(rb.angularVelocity,axis);
            yawAssistActive = yawAssistDelta != 0;
        }

        // ------------------------------------------------------------------ aim + camera
        void InitAim(Craft c)
        {
            Transform t = c.transform;
            aimDir = t.forward;
            rigRot = Quaternion.LookRotation(aimDir, PlanetUp(t.position));
            freeLook = Quaternion.identity;
            aimInit = true;
        }

        static bool KeyHeld(string key)
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            return kb != null && Enum.TryParse(key, true, out UnityEngine.InputSystem.Key k) && kb[k] != null && kb[k].isPressed;
        }

        static bool KeyPressed(string key)
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            return kb != null && Enum.TryParse(key, true, out UnityEngine.InputSystem.Key k) && kb[k] != null && kb[k].wasPressedThisFrame;
        }

        void UpdateAimAndCamera(Craft c, MouseController mc)
        {
            if (!aimInit) InitAim(c);
            float dt = Time.unscaledDeltaTime;
            bool blocked = (!Application.isFocused && !TestActive) || Time.timeScale <= 0 || InputLock.lockInput || c.craftControls?.autopilot?.enabled == true;
            if (blocked) { mouseResponse.Reset(); return; }
            Transform t = c.transform;
            Vector3 up = PlanetUp(t.position);

            Vector2 md = Vector2.zero;
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse != null && Application.isFocused && Time.timeScale > 0f && !TestActive)
            {
                var counts = mouse.delta.ReadValue();
                var shaped = mouseResponse.Step(new System.Numerics.Vector2(counts.x, counts.y), dt,
                    S.MouseSensitivity, S.PrecisionSensitivity, S.FullSensitivitySpeed, S.MouseSmoothing);
                md = new Vector2(shaped.X, shaped.Y);
                if (S.InvertY) md.y = -md.y;
                float scroll = mouse.scroll.ReadValue().y;
                if (scroll != 0f) zoom = Mathf.Clamp(zoom * (scroll > 0 ? 0.9f : 1.1f), 0.4f, 3f);
            }

            bool freeLooking = KeyHeld(S.FreeLookKey);
            if (freeLooking != wasFreeLooking) { mouseResponse.Reset(); md = Vector2.zero; }
            wasFreeLooking = freeLooking;
            if (freeLooking)
            {
                // Look around; the aim stays where it is.
                freeLook = Quaternion.AngleAxis(md.x, Vector3.up) * freeLook * Quaternion.AngleAxis(-md.y, Vector3.right);
            }
            else
            {
                freeLook = Quaternion.Slerp(freeLook, Quaternion.identity, 1f - Mathf.Exp(-dt / Mathf.Max(S.FreeLookReturn, 0.01f)));
                // The mouse turns the aim around the camera's own axes (War Thunder behaviour).
                Vector3 rigUp = rigRot * Vector3.up, rigRight = rigRot * Vector3.right;
                aimDir = Quaternion.AngleAxis(md.x, rigUp) * Quaternion.AngleAxis(-md.y, rigRight) * aimDir;
                aimDir.Normalize();
            }

            // Camera rig follows the aim with the horizon level; near vertical keep the current
            // up so loops pass over the top smoothly.
            Vector3 upRef = Mathf.Abs(Vector3.Dot(aimDir, up)) > 0.92f ? rigRot * Vector3.up : up;
            Quaternion goal = Quaternion.LookRotation(aimDir, upRef);
            rigRot = Quaternion.Slerp(rigRot, goal, 1f - Mathf.Exp(-dt / Mathf.Max(S.CameraSmoothing, 0.005f)));

            mc.targetDirection = aimDir;     // keep Flyout's own state consistent
        }

        float CameraDistance(Craft c)
        {
            if (S.CameraDistance > 0f) return S.CameraDistance * zoom;
            Vector3 dims = c.dimensions;
            float size = Mathf.Max(dims.x, Mathf.Max(dims.y, dims.z));
            if (!(size > 0.5f) || size > 500f) size = 10f;
            return (size * 1.7f + 6f) * zoom;
        }

        [HarmonyPatch(typeof(CameraController), nameof(CameraController.LateUpdate))]
        static class CameraPatch
        {
            static void Postfix(CameraController __instance)
            {
                try { I?.ApplyCamera(__instance); }
                catch (Exception e) { I?.ReportError("camera", e); }
            }
        }

        void ApplyCamera(CameraController cc)
        {
            if (!Active(out var c, out var mc)) { RestoreNativeHud(); return; }
            if (c.craftControls?.autopilot?.enabled == true) { RestoreNativeHud(); Owner = "autopilot"; return; }
            UpdateAimAndCamera(c, mc);
            HideNativeHud(mc);

            Transform camT = cc.transform;
            Quaternion oldRot = camT.rotation;
            Quaternion camRot = rigRot * freeLook;
            float dist = CameraDistance(c);
            Vector3 pos = Pivot(c) + camRot * new Vector3(0f, dist * S.CameraHeight, -dist);
            camT.SetPositionAndRotation(pos, camRot);

            // Keep the scaled-space (sky/planet) camera in the same relation to the world camera.
            var space = cc.spaceCam;
            if (space != null && space.transform != camT && !space.transform.IsChildOf(camT))
                space.transform.rotation = camRot * (Quaternion.Inverse(oldRot) * space.transform.rotation);
        }

        void HideNativeHud(MouseController mc)
        {
            SetActive(mc.crosshairHud, false); SetActive(mc.flightTargetHud, false);
            nativeHudHidden = true;
        }

        void RestoreNativeHud()
        {
            if (!nativeHudHidden) return;
            nativeHudHidden = false;
            var mc = MouseController.instance;
            if (mc != null && mc.enabled) { SetActive(mc.crosshairHud, true); SetActive(mc.flightTargetHud, true); }
        }

        static void SetActive(Transform t, bool on)
        {
            if (t != null && t.gameObject.activeSelf != on) t.gameObject.SetActive(on);
        }

        // One stabilizing controller at a time. Restore the aircraft's saved FBW setting
        // in a finalizer, even if native UpdateControls throws; never persist this override.
        [HarmonyPatch(typeof(CraftControls), nameof(CraftControls.UpdateControls))]
        static class FbwOwnershipPatch
        {
            static void Prefix(CraftControls __instance, ref bool __state)
            {
                __state = false;
                var m = I;
                if (m != null) m.lastFbwBypassed = false;
                if (m == null || !m.S.BypassNativeFbw || !__instance.fbw ||
                    !m.Active(out var c, out _) || c.craftControls == null || c.craftControls.Pointer != __instance.Pointer ||
                    __instance.autopilot?.enabled == true || Time.timeScale <= 0 || InputLock.lockInput ||
                    (!Application.isFocused && !m.TestActive) || c.airspeed.magnitude < m.S.Instructor.MinSpeed ||
                    m.BridgeOwnsControls(out _, out _, out _)) return;
                __state = true; m.lastFbwBypassed = true;
                __instance.fbw = false;
            }
            static void Finalizer(CraftControls __instance, bool __state)
            {
                if (__state && __instance != null) __instance.fbw = true;
            }
        }

        // ------------------------------------------------------------------ control
        [HarmonyPatch(typeof(MouseController), nameof(MouseController.UpdateController))]
        static class ControllerPatch
        {
            static void Postfix(MouseController __instance)
            {
                try { I?.Control(__instance); }
                catch (Exception e) { I?.ReportError("control", e); }
            }
        }

        void ReadSigns(MouseController mc)
        {
            if (signsRead || mc.settings == null) return;
            var m = mc.settings.inputMul;
            // Flyout's own law: pitch = inputMul.y * -angleUp, yaw = inputMul.x * angleRight, roll = inputMul.z * bank.
            if (m.y != 0) sP = -Mathf.Sign(m.y);
            if (m.x != 0) sY = Mathf.Sign(m.x);
            if (m.z != 0) sR = Mathf.Sign(m.z);
            signsRead = true;
            LoggerInstance.Msg($"Control signs: pitchUp={sP} yawRight={sY} rollRight={sR} (inputMul {m.x},{m.y},{m.z})");
        }

        void Control(MouseController mc)
        {
            if (!Active(out var c, out _)) { Owner = S.Enabled ? "native" : "off"; return; }
            if ((!Application.isFocused && !TestActive) || Time.timeScale <= 0 || InputLock.lockInput)
            { Ins.Reset(); mouseResponse.Reset(); manualW = default; suspended = true; Owner = "suspended"; Last.Mode = "PAUSED / INPUT LOCK"; return; }
            if (suspended) { aimInit = false; suspended = false; }
            ReadSigns(mc);
            if (!aimInit) InitAim(c);

            if (BridgeOwnsControls(out float bp, out float br, out float by))
            { Ins.Reset(); mc.pitch = bp; mc.roll = br; mc.yaw = by; Owner = "mcp"; Last.Mode = "MCP"; return; }

            float dt = Time.deltaTime;
            var rb = c.masterRB?.rb;
            if (dt <= 0f || rb == null) { Owner = "native"; return; }

            Transform t = c.transform;
            Vector3 upW = PlanetUp(t.position);
            var localAir = t.InverseTransformDirection(c.airspeed);
            float alpha = Mathf.Atan2(-localAir.y, Mathf.Max(localAir.z, .001f)) * Mathf.Rad2Deg;
            float beta = Mathf.Atan2(localAir.x, Mathf.Max(localAir.z, .001f)) * Mathf.Rad2Deg;
            wingEnvelope.Update(c, Envelope, alpha);
            separation = wingEnvelope.Separation();
            Envelope.Observe(alpha, separation, beta, t.InverseTransformDirection(rb.angularVelocity).z,
                c.airspeed.magnitude, dt, c.radarAlt > 50 && localAir.z > 20,
                S.Instructor.AoALimitDeg, S.Instructor.AoAMarginDeg);
            float mP = S.ManualPitchOverride ? InputManager.m_pitch : 0f, mY = mc.kbYaw, mR = mc.kbRoll;
            LastBodyRates = N(t.InverseTransformDirection(rb.angularVelocity));
            var st = new AircraftState
            {
                TargetDir = N(t.InverseTransformDirection(aimDir)),
                Omega = LastBodyRates,
                AirVel = N(t.InverseTransformDirection(c.airspeed)),
                Up = N(t.InverseTransformDirection(upW)),
                RadarAlt = c.radarAlt,
                VerticalSpeed = Vector3.Dot(c.velocity, upW),
                Dt = dt,
                PositiveAoALimit = Envelope.StablePositive,
                NegativeAoALimit = Envelope.StableNegative,
                ManualPitch = MathF.Abs(mP) > S.ManualDeadzone || manualW.X > .05f,
                ManualYaw = MathF.Abs(mY) > S.ManualDeadzone || manualW.Y > .05f,
                ManualRoll = MathF.Abs(mR) > S.ManualDeadzone || manualW.Z > .05f,
            };
            var o = Ins.Step(st);
            Last = o;
            if (!o.Valid)
            {
                // Taxi / takeoff roll / bad data: keep the aim on the nose so lifting off is calm.
                if (o.Mode == "LOW SPEED") { aimDir = t.forward; mc.pitch = mP; mc.roll = mR; mc.yaw = mY; }
                Owner = "instructor-idle"; return;
            }

            float iP = sP * o.PitchUp, iY = sY * o.YawRight, iR = sR * o.RollRight;
            LastManual = new NVec3(mP, mY, mR);
            float k = 1f - MathF.Exp(-dt / MathF.Max(S.ManualReturnTime, 0.05f));
            manualW.X = Blend(manualW.X, ManualTarget(mP), k);
            manualW.Y = Blend(manualW.Y, ManualTarget(mY), k);
            manualW.Z = Blend(manualW.Z, ManualTarget(mR), k);
            mc.pitch = Math.Clamp(Lerp(iP, mP, manualW.X), -1f, 1f);
            mc.yaw = Math.Clamp(Lerp(iY, mY, manualW.Y), -1f, 1f);
            mc.roll = Math.Clamp(Lerp(iR, mR, manualW.Z), -1f, 1f);
            Owner = manualW.X + manualW.Y + manualW.Z > 0.05f ? "instructor+manual" : "instructor";
            LastRaw = new NVec3(mc.pitch, mc.yaw, mc.roll);
            if (recording) Record(o, c);
        }

        float ManualTarget(float m) => MathF.Abs(m) > S.ManualDeadzone ? Math.Clamp(MathF.Abs(m) * 2f, 0f, 1f) : 0f;
        static float Blend(float cur, float target, float k) => target > cur ? target : cur + (target - cur) * k;
        static float Lerp(float a, float b, float t) => a + (b - a) * Math.Clamp(t, 0f, 1f);

        bool BridgeOwnsControls(out float p, out float r, out float y)
        {
            p = r = y = 0;
            if (!bridgeLookedUp)
            {
                bridgeLookedUp = true;
                bridgeCore = MelonMod.RegisteredMelons.FirstOrDefault(m => m.GetType().FullName == "FlyoutMcpBridge.Core");
                bridgeField = bridgeCore?.GetType().GetField("flightInput", BindingFlags.Instance | BindingFlags.NonPublic);
            }
            if (bridgeField == null) return false;
            object v = bridgeField.GetValue(bridgeCore);
            if (v == null) return false;
            var tuple = (ValueTuple<string, float, float, float, DateTime>)v;
            if (DateTime.UtcNow >= tuple.Item5) return false;
            p = tuple.Item2; r = tuple.Item3; y = tuple.Item4;
            return true;
        }

        internal void ReportError(string where, Exception e)
        {
            errorCount++;
            if (Time.realtimeSinceStartup - lastErrorLog > 5f)
            {
                lastErrorLog = Time.realtimeSinceStartup;
                LoggerInstance.Error($"[{where}] {e.GetType().Name}: {e.Message} (errors: {errorCount})");
            }
            if (errorCount > 300 && S.Enabled)
            {
                S.Enabled = false;
                LoggerInstance.Error("Too many errors; War Thunder mouse aim disabled, Flyout's native mouse flight restored.");
            }
        }

        // ------------------------------------------------------------------ keys, HUD, IO
        public override void OnUpdate()
        {
            try
            {
                UpdateFlightHelp();
                bool owns = S.Enabled && MouseController.instance != null && MouseController.instance.enabled &&
                    (int)CameraController.mode == 8 && ActiveCraft()?.craftControls?.autopilot?.enabled != true;
                if (previouslyOwned && !owns)
                { Ins.Reset(); mouseResponse.Reset(); aimInit = false; manualW = default; RestoreNativeHud(); }
                previouslyOwned = owns;
                if (!owns || !S.ArtificialYawStability || Time.timeScale <= 0 || (!Application.isFocused && !TestActive) || InputLock.lockInput)
                    ResetYawAssist();
                if (Application.isFocused)
                {
                    if (KeyPressed(S.HelpKey)) { showHelp = !showHelp; automaticHelpUntil = 0; }
                    if (!InputLock.lockInput && KeyPressed(S.ToggleKey)) SetEnabled(!S.Enabled);
                    if (KeyPressed(S.HudKey)) { S.ShowHud = !S.ShowHud; SaveSettings(); }
                    if (KeyPressed(S.EmergencyKey)) EmergencyRelease("hotkey");
                }
                if (testRunning && !TestActive) EndTest();
                PollRequests();
                if (recording && Time.realtimeSinceStartupAsDouble > recordUntil) StopRecording();
            }
            catch (Exception e) { ReportError("update", e); }
        }

        internal void SetEnabled(bool on)
        {
            S.Enabled = on; SaveSettings();
            Ins.Reset(); mouseResponse.Reset(); ResetYawAssist(); manualW = default; aimInit = false;
            if (!on) RestoreNativeHud();
            Popup.ShowMessage(on ? "War Thunder mouse aim: ON" : "War Thunder mouse aim: OFF (Flyout native)", 2f);
        }

        internal void EmergencyRelease(string why)
        {
            EndTest();
            SetEnabled(false);
            var mc = MouseController.instance;
            if (mc != null) { mc.pitch = 0; mc.roll = 0; mc.yaw = 0; }
            LoggerInstance.Warning("Released (" + why + ").");
        }

        Texture2D circleTex, crossTex;
        GUIStyle style;

        static Texture2D MakeCircle(int size, float radius, float thickness)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            float c = (size - 1) / 2f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Mathf.Abs(Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) - radius);
                    float a = Mathf.Clamp01(thickness / 2f + 0.75f - d);
                    tex.SetPixel(x, y, new Color(1, 1, 1, a));
                }
            tex.Apply();
            return tex;
        }

        static Texture2D MakeCross(int size, float gap, float thickness)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            float c = (size - 1) / 2f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = Mathf.Abs(x - c), dy = Mathf.Abs(y - c);
                    bool h = dy <= thickness / 2f && dx >= gap;
                    bool v = dx <= thickness / 2f && dy >= gap;
                    tex.SetPixel(x, y, new Color(1, 1, 1, h || v ? 1f : 0f));
                }
            tex.Apply();
            return tex;
        }

        public override void OnGUI()
        {
            try
            {
                if (showHelp) DrawHelp();
                if (!Active(out var c, out var mc)) return;
                Camera cam = CameraController.instance != null && CameraController.instance.worldCam != null
                    ? CameraController.instance.worldCam : Camera.main;
                if (cam == null) return;
                if (circleTex == null) { circleTex = MakeCircle(48, 18f, 2.2f); crossTex = MakeCross(40, 5f, 2.2f); }

                Vector3 pivot = Pivot(c);
                // Nose cross: where the aircraft is pointing.
                DrawTex(cam, pivot + c.transform.forward * 1500f, crossTex, 40, new Color(1f, 1f, 1f, 0.9f));
                // Aim circle: where you are telling it to go.
                DrawTex(cam, cam.transform.position + aimDir * 1500f, circleTex, 48, new Color(1f, 1f, 1f, 0.95f));

                if (!S.ShowHud) return;
                if (style == null) style = new GUIStyle(GUI.skin.label) { fontSize = 13 };
                string lim = (Last.AoALimited ? " AOA" : "") + (Last.GLimited ? " G" : "") + (Last.Terrain ? " TERRAIN" : "") + (Last.Spin ? " SPIN" : "");
                string source = S.Instructor.AdaptiveAoA ? (Envelope.HasModel ? "wing estimate" : "fallback") : "manual";
                string ownerLabel = Owner == "instructor" ? "ASSIST ACTIVE" : Owner == "instructor+manual" ? "MANUAL OVERRIDE" : Owner == "mcp" ? "MCP CONTROL" : Owner.ToUpperInvariant();
                string txt = $"MOUSE AIM  |  {ownerLabel}  |  {Last.Mode}    Aim error {Last.AimErrorDeg:F1} deg";
                string detail = $"AoA {Last.AlphaDeg:F1}   Limit +{Last.PositiveAoALimit:F1} / -{Last.NegativeAoALimit:F1} ({source})    Yaw assist {(S.ArtificialYawStability ? "ON" : "OFF")}    F12 help";
                float width = Mathf.Min(760, Screen.width - 40);
                GUI.Box(new Rect(16, Screen.height - 76, width, 62), "");
                GUI.color = Last.AoALimited || Last.GLimited || Last.ControlLimited || Last.Spin || Last.Terrain ? new Color(1f, .8f, .4f) : Color.white;
                GUI.Label(new Rect(26, Screen.height - 71, width - 20, 24), txt, style);
                GUI.color = new Color(.8f, .87f, .92f);
                GUI.Label(new Rect(26, Screen.height - 46, width - 20, 24), detail, style);
                GUI.color = Color.white;
            }
            catch (Exception e) { ReportError("gui", e); }
        }

        void DrawHelp()
        {
            float w = Mathf.Min(580, Screen.width - 40);
            GUI.color = Color.white;
            GUI.Box(new Rect(20, 60, w, 430), "MOUSE AIM  |  Controls & guidance");
            var helpStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true };
            string text = "Get airborne, then press F2 for mouse flight. Point the circle where you want to fly; the cross marks your nose.\n\n"
                + $"{S.FreeLookKey}: look around   |   Wheel: zoom\n"
                + $"{S.ToggleKey}: mod on/off   |   {S.HudKey}: status   |   {S.EmergencyKey}: release\n"
                + "Your normal pitch / roll / yaw keys override each axis. Release them to return smoothly.\n\n"
                + "Yaw assist gently damps sideways wobble. Pitch and roll keep normal physics.\n"
                + "Slow mouse movement gives fine aim; fast movement gives full turning speed.\n"
                + $"Sensitivity {S.MouseSensitivity:F3}   Precision {S.PrecisionSensitivity:P0}\n\n"
                + "AoA limits are estimated from this aircraft's wings and reduced when separation is observed. They are not a guarantee against stalls.\n\n"
                + $"{S.HelpKey}: close help. Settings: UserData/FlyoutMouseAim.json";
            GUI.Label(new Rect(34, 87, w - 28, 395), text, helpStyle);
        }

        static void DrawTex(Camera cam, Vector3 world, Texture2D tex, int size, Color col)
        {
            Vector3 sp = cam.WorldToScreenPoint(world);
            if (sp.z <= 0) return;
            GUI.color = col;
            GUI.DrawTexture(new Rect(sp.x - size / 2f, Screen.height - sp.y - size / 2f, size, size), tex);
        }

        void PollRequests()
        {
            string dir = Path.Combine(ioRoot, "requests");
            foreach (string f in Directory.EnumerateFiles(dir, "*.json").Take(4))
            {
                string id = Path.GetFileNameWithoutExtension(f);
                if (!Guid.TryParse(id, out _)) continue;
                object resp;
                try
                {
                    string json = File.ReadAllText(f);
                    File.Delete(f);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("expiresUtc", out var exp) && exp.GetDateTime() < DateTime.UtcNow)
                        throw new Exception("Request expired before execution");
                    resp = new { ok = true, result = Commands.Execute(this, root) };
                }
                catch (Exception e) { resp = new { ok = false, error = e.GetBaseException().Message }; }
                string outPath = Path.Combine(ioRoot, "responses", id + ".json");
                File.WriteAllText(outPath + ".tmp", JsonSerializer.Serialize(resp, Json));
                File.Move(outPath + ".tmp", outPath, true);
            }
        }

        internal object Status()
        {
            bool active = Active(out var c, out var mc);
            var cc = CameraController.instance;
            return new
            {
                version = "1.0.0",
                enabled = S.Enabled,
                help = new { visible = showHelp, automaticSecondsRemaining = Math.Max(0, automaticHelpUntil - Time.realtimeSinceStartupAsDouble) },
                active,
                mouseFlightEnabled = mc != null && mc.enabled,
                cameraMode = (int)CameraController.mode,
                craft = c?.vName,
                owner = Owner,
                yawStability = new { enabled = S.ArtificialYawStability, active = yawAssistActive,
                    strength = S.YawStabilityStrength, deltaRate = yawAssistDelta,
                    rateBefore = yawRateBefore, rateAfter = yawRateAfter, flowRate = yawStabilizer.FlowRate },
                nativeFbwBypassed = lastFbwBypassed,
                savedFbwEnabled = c?.craftControls?.fbw,
                mode = Last.Mode,
                aimErrorDeg = Last.AimErrorDeg,
                errorsDeg = new { up = Last.ErrUp * 57.2958f, right = Last.ErrRight * 57.2958f, roll = Last.ErrRoll * 57.2958f },
                output = new { pitchUp = Last.PitchUp, yawRight = Last.YawRight, rollRight = Last.RollRight },
                raw = new { pitch = LastRaw.X, yaw = LastRaw.Y, roll = LastRaw.Z },
                manual = new { pitch = LastManual.X, yaw = LastManual.Y, roll = LastManual.Z },
                radarAltitude = c?.radarAlt,
                bodyRates = LastBodyRates,
                air = new { speed = Last.Speed, alphaDeg = Last.AlphaDeg, betaDeg = Last.BetaDeg, loadFactor = Last.LoadFactor },
                envelope = new { adaptive = S.Instructor.AdaptiveAoA, source = Envelope.HasModel ? "wing model + passive observation" : "fallback",
                    wings = wingEnvelope.Count, peakPositive = Envelope.PeakPositive, peakNegative = Envelope.PeakNegative,
                    stablePositive = Envelope.StablePositive, stableNegative = Envelope.StableNegative,
                    observedPositive = Envelope.ObservedPositive, observedNegative = Envelope.ObservedNegative,
                    separation = float.IsFinite(separation) ? separation : (float?)null },
                limits = new { controls = Last.ControlLimited, aoa = Last.AoALimited, g = Last.GLimited, terrain = Last.Terrain, spin = Last.Spin, push = Last.Push },
                signs = new { pitchUp = sP, yawRight = sY, rollRight = sR, fromSettings = signsRead },
                camera = cc == null ? null : new
                {
                    path = TPath(cc.transform),
                    worldCam = cc.worldCam != null ? TPath(cc.worldCam.transform) : null,
                    spaceCam = cc.spaceCam != null ? TPath(cc.spaceCam.transform) : null,
                    main = Camera.main != null ? TPath(Camera.main.transform) : null,
                },
                testActive = TestActive,
                recording = recording ? recordFile : null,
                errors = errorCount,
            };
        }

        static string TPath(Transform t)
        {
            string p = t.name;
            for (var x = t.parent; x != null; x = x.parent) p = x.name + "/" + p;
            return p;
        }

        internal object Aim(float yawDeg, float pitchDeg, string from)
        {
            if (!Active(out var c, out _)) throw new Exception("War Thunder mouse aim is not active (be in flight with F2 on).");
            if (!aimInit) InitAim(c);
            Transform t = c.transform;
            Vector3 up = PlanetUp(t.position);
            Vector3 baseDir = from == "target" ? aimDir : t.forward;
            if (from == "horizon")
            {
                baseDir -= up * Vector3.Dot(baseDir,up);
                if (baseDir.sqrMagnitude < 1e-6f) baseDir = Vector3.Cross(t.right,up);
                baseDir.Normalize();
            }
            Vector3 right = Vector3.Cross(up, baseDir);
            if (right.sqrMagnitude < 1e-6f) right = t.right;
            right.Normalize();
            Quaternion yaw = Quaternion.AngleAxis(yawDeg, up);
            aimDir = (Quaternion.AngleAxis(-pitchDeg, yaw * right) * (yaw * baseDir)).normalized;
            return new { yawDeg, pitchDeg, from };
        }

        internal object StartRecording(float seconds, string name)
        {
            if (seconds <= 0 || seconds > 1800) throw new Exception("seconds must be in (0, 1800]");
            string safe = string.IsNullOrWhiteSpace(name) ? DateTime.Now.ToString("yyyyMMdd_HHmmss") : string.Concat(name.Where(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-'));
            recordFile = System.IO.Path.Combine(ioRoot, "rec_" + safe + ".csv");
            telemetry.Enqueue("#OPEN " + recordFile);
            telemetry.Enqueue("t,dt,mode,owner,aimErrDeg,eUpDeg,eRightDeg,eRollDeg,uPitchUp,uYawRight,uRollRight,rawPitch,rawYaw,rawRoll,manP,manY,manR,speed,alphaDeg,betaDeg,nz,radarAlt,aoaLim,gLim,terrain,spin,positiveAoALimit,negativeAoALimit,separation,yawAssistDelta,yawRateBefore,yawRateAfter,bodyPitchRate,bodyYawRate,bodyRollRate");
            recordUntil = Time.realtimeSinceStartupAsDouble + seconds;
            recording = true;
            return new { file = recordFile, seconds };
        }

        internal void StopRecording() { recording = false; telemetry.Enqueue("#CLOSE"); }

        void Record(InstructorOutput o, Craft c)
        {
            const float D = 57.2958f;
            telemetry.Enqueue(string.Join(",", new object[] {
                Time.timeAsDouble.ToString("F4"), Time.deltaTime.ToString("F4"), o.Mode, Owner,
                o.AimErrorDeg.ToString("F3"), (o.ErrUp*D).ToString("F3"), (o.ErrRight*D).ToString("F3"), (o.ErrRoll*D).ToString("F3"),
                o.PitchUp.ToString("F4"), o.YawRight.ToString("F4"), o.RollRight.ToString("F4"),
                LastRaw.X.ToString("F4"), LastRaw.Y.ToString("F4"), LastRaw.Z.ToString("F4"),
                LastManual.X.ToString("F3"), LastManual.Y.ToString("F3"), LastManual.Z.ToString("F3"),
                o.Speed.ToString("F2"), o.AlphaDeg.ToString("F2"), o.BetaDeg.ToString("F2"), o.LoadFactor.ToString("F2"), c.radarAlt.ToString("F1"),
                o.AoALimited ? 1 : 0, o.GLimited ? 1 : 0, o.Terrain ? 1 : 0, o.Spin ? 1 : 0, o.PositiveAoALimit, o.NegativeAoALimit, float.IsFinite(separation) ? separation : -1, yawAssistDelta, yawRateBefore, yawRateAfter, LastBodyRates.X, LastBodyRates.Y, LastBodyRates.Z }));
        }

        void WriterLoop()
        {
            StreamWriter w = null;
            while (true)
            {
                while (telemetry.TryDequeue(out var line))
                {
                    try
                    {
                        if (line.StartsWith("#OPEN ")) { w?.Dispose(); w = new StreamWriter(line.Substring(6), false); }
                        else if (line == "#CLOSE") { w?.Dispose(); w = null; }
                        else w?.WriteLine(line);
                    }
                    catch { }
                }
                w?.Flush();
                Thread.Sleep(100);
            }
        }
    }
}
