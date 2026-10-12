// War Thunder-style "Instructor" (desired-direction flight control, after Gaijin's patents
// US8770979B2 / US9011152B2).
//
// The player only chooses a direction. Every frame the Instructor predicts the aircraft's
// attitude a short time ahead from its angular velocity, expresses the requested direction in
// that predicted frame, and sets roll/pitch/yaw so that:
//   * far from the target it banks the lift vector onto the target and pulls (a real turn),
//   * close to the target it levels the wings and fine-aims with elevator + rudder,
//   * small corrections below the nose are made by pushing, not by rolling inverted,
//   * pitch is limited by angle of attack and load factor, and spins are recovered.
// Working on the predicted attitude is what removes overshoot: the controls ease off before
// the nose arrives. Explicit rate damping keeps it stable across aircraft.
//
// Pure System.Numerics so it can be tested offline. Unity body frame: x right, y up, z fwd.
// Unity angular velocity: +x nose down, +y nose right, +z roll left. Outputs use control
// signs: positive = pitch up / yaw right / roll right.

using System;
using System.Numerics;

namespace FlyoutMouseAim
{
    public sealed class InstructorConfig
    {
        public float LeadTime = 0.25f;          // s, attitude prediction horizon at RefSpeed
        public float MaxLeadTime = 0.7f;        // s, horizon grows at low speed (slower response)
        public float PitchGain = 4.0f;          // stick per rad of predicted pitch error
        public float RollGain = 2.5f;           // stick per rad of predicted bank error
        public float YawGain = 3.0f;            // stick per rad of predicted yaw error (near target)
        public float PitchDamping = 0.35f;      // stick per rad/s of pitch rate
        public float RollDamping = 0.25f;       // stick per rad/s of roll rate
        public float YawDamping = 0.6f;         // stick per rad/s of yaw rate
        public float SlipGain = 1.5f;           // rudder per rad of sideslip (coordination)
        public float RollStartDeg = 3f;         // below: wings level + rudder aiming
        public float RollFullDeg = 15f;         // above: full bank-to-target
        public float PushMaxDeg = 35f;          // below-nose corrections smaller than this push
        public float LevelGain = 0.8f;          // wings-level strength near the target
        public float RefSpeed = 90f;            // m/s, gains scale ~ RefSpeed/V
        public float MinGainScale = 0.5f, MaxGainScale = 1.8f;
        public float AoALimitDeg = 14f;         // fallback / manual override only
        public bool AdaptiveAoA = true;
        public float AoAMarginDeg = 2f;
        public float FineBankGain = 2.5f;
        public float FineDamping = 1.3f;
        public float AoAGain = 6f;              // stick removed per rad beyond the limit
        public float GLimit = 8f;
        public float MinSpeed = 15f;            // m/s; below: native law (taxi/takeoff roll)
        public float SpinYawRate = 1.5f;
        public float TerrainFloor = 30f;        // m
        public float TerrainClimbDeg = 8f;
        public float Smoothing = 0.03f;         // s output smoothing
        public float TrimRate = 1.2f;           // stick per (rad*s) of error: auto-trim holds the aim exactly
        public float TrimLimit = 0.6f;          // max auto-trim stick
        public float TrimWindowDeg = 12f;       // auto-trim only integrates this close to the target
    }

    public struct AircraftState
    {
        public Vector3 TargetDir;   // requested direction, body frame
        public Vector3 Omega;       // body angular velocity (Unity convention), rad/s
        public Vector3 AirVel;      // body air-relative velocity, m/s
        public Vector3 Up;          // local planetary up, body frame
        public float RadarAlt;      // m, NaN if unknown
        public float VerticalSpeed; // m/s
        public float Dt;
        public float PositiveAoALimit, NegativeAoALimit; // supplied by aircraft envelope
        public bool ManualPitch, ManualYaw, ManualRoll;
    }

    public struct InstructorOutput
    {
        public bool Valid;
        public float PitchUp, YawRight, RollRight;
        public string Mode;
        public float AimErrorDeg, AlphaDeg, BetaDeg, Speed, LoadFactor;
        public float ErrUp, ErrRight, ErrRoll;
        public Vector3 PredictedForward;           // body frame
        public bool AoALimited, GLimited, Terrain, Spin, Push, ControlLimited;
        public float PositiveAoALimit, NegativeAoALimit;
    }

    public sealed class Instructor
    {
        public readonly InstructorConfig Cfg;
        Vector2 lastAim = new Vector2(0, 1);
        Vector3 smooth, trim;
        float spinTimer;
        bool pushing;

        public Instructor(InstructorConfig cfg) { Cfg = cfg; }
        public void Reset() { pushing = false; smooth = Vector3.Zero; trim = Vector3.Zero; spinTimer = 0; lastAim = new Vector2(0, 1); }

        static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
        static float S01(float a, float b, float x) { float t = Math.Clamp((x - a) / MathF.Max(b - a, 1e-4f), 0, 1); return t * t * (3 - 2 * t); }
        const float Deg = MathF.PI / 180f;
        static float Wrap(float a) { while (a > MathF.PI) a -= 2 * MathF.PI; while (a < -MathF.PI) a += 2 * MathF.PI; return a; }

        public InstructorOutput Step(in AircraftState st)
        {
            var o = new InstructorOutput { Mode = "TRACK" };
            if (!float.IsFinite(st.Dt) || !(st.Dt > 0) || st.Dt > .25f || !Finite(st.TargetDir) || !Finite(st.Omega) || !Finite(st.AirVel) || !Finite(st.Up)
                || st.TargetDir.LengthSquared() < 1e-8f)
            { Reset(); o.Mode = "INVALID"; return o; }
            float V = st.AirVel.Length();
            o.Speed = V;
            if (V < Cfg.MinSpeed) { Reset(); o.Mode = "LOW SPEED"; return o; }

            Vector3 target = Vector3.Normalize(st.TargetDir);
            Vector3 up = st.Up.LengthSquared() > 1e-6f ? Vector3.Normalize(st.Up) : Vector3.UnitY;

            // Terrain: near the ground, never ask for a descent.
            if (float.IsFinite(st.RadarAlt) && st.RadarAlt < Cfg.TerrainFloor && st.VerticalSpeed < 0)
            {
                float minElev = Math.Clamp((Cfg.TerrainFloor - st.RadarAlt) / Cfg.TerrainFloor, 0, 1) * Cfg.TerrainClimbDeg * Deg;
                float elev = MathF.Asin(Math.Clamp(Vector3.Dot(target, up), -1, 1));
                if (elev < minElev)
                {
                    Vector3 h = target - up * Vector3.Dot(target, up);
                    if (h.LengthSquared() < 1e-6f) h = Vector3.UnitZ - up * up.Z;
                    h = Vector3.Normalize(h);
                    target = Vector3.Normalize(h * MathF.Cos(minElev) + up * MathF.Sin(minElev));
                    o.Terrain = true;
                }
            }

            // Predicted attitude: rotate by omega * LeadTime.
            float lead = Math.Clamp(Cfg.LeadTime * Cfg.RefSpeed / MathF.Max(V, 1f), Cfg.LeadTime * 0.7f, Cfg.MaxLeadTime);
            Vector3 rot = st.Omega * lead;
            float ang = rot.Length();
            Quaternion q = ang > 1e-6f ? Quaternion.CreateFromAxisAngle(rot / ang, ang) : Quaternion.Identity;
            Quaternion inv = Quaternion.Conjugate(q);
            Vector3 d = Vector3.Transform(target, inv);
            Vector3 upP = Vector3.Transform(up, inv);
            o.PredictedForward = Vector3.Transform(Vector3.UnitZ, q);
            o.AimErrorDeg = MathF.Acos(Math.Clamp(target.Z, -1, 1)) / Deg;

            // Errors in the predicted frame (full angle split by direction, valid behind too).
            float theta = MathF.Acos(Math.Clamp(d.Z, -1, 1));
            float rho = MathF.Sqrt(d.X * d.X + d.Y * d.Y);
            Vector2 aim;
            if (rho > 0.06f) { aim = new Vector2(d.X, d.Y) / rho; lastAim = aim; }
            else if (d.Z < 0) aim = lastAim;
            else aim = rho > 1e-6f ? new Vector2(d.X, d.Y) / rho : new Vector2(0, 1);
            float eUp = theta * aim.Y, eRight = theta * aim.X;

            // Bank target. Far from the target: roll the lift vector onto it (or, for small
            // corrections below the nose, roll so the target is "below" and push). Near the
            // target: roll toward wings level. Blended as angles so the goal never jumps.
            float w = S01(Cfg.RollStartDeg * Deg, Cfg.RollFullDeg * Deg, theta);
            float ePull = MathF.Atan2(aim.X, aim.Y);
            float ePushRoll = Wrap(ePull + MathF.PI);
            bool canPush = theta < Cfg.PushMaxDeg * Deg;
            // Push only when the target is nearly straight below in the current bank.
            if (!canPush) pushing = false;
            else if (!pushing && MathF.Abs(ePushRoll) < 40 * Deg) pushing = true;
            else if (pushing && MathF.Abs(ePushRoll) > 75 * Deg) pushing = false;
            bool push = pushing;
            float eAimRoll = push ? ePushRoll : ePull;
            float levelMag = MathF.Sqrt(upP.X * upP.X + upP.Y * upP.Y);
            float eLevel = levelMag > 1e-3f ? MathF.Atan2(upP.X, upP.Y) * Cfg.LevelGain * Math.Clamp(levelMag * 2, 0, 1) : 0;
            // Small heading corrections need a modest bank as well as rudder. This is
            // continuous at zero and at both blend boundaries (no minimum-stick jump).
            float fineBank = Math.Clamp(eRight * Cfg.FineBankGain, -25 * Deg, 25 * Deg);
            float eRoll = w * eAimRoll + (1 - w) * (eLevel + fineBank);
            o.ErrUp = eUp; o.ErrRight = eRight; o.ErrRoll = eRoll; o.Push = push;

            float qr = -st.Omega.X, rr = st.Omega.Y, pr = -st.Omega.Z;   // pitch-up, yaw-right, roll-right rates
            float alpha = MathF.Atan2(-st.AirVel.Y, MathF.Max(st.AirVel.Z, 1e-3f));
            float beta = MathF.Atan2(st.AirVel.X, MathF.Max(st.AirVel.Z, 1e-3f));
            o.AlphaDeg = alpha / Deg; o.BetaDeg = beta / Deg;
            o.LoadFactor = V * qr / 9.81f + Vector3.Dot(up, Vector3.UnitY);

            float g = Math.Clamp(Cfg.RefSpeed / MathF.Max(V, 1f), Cfg.MinGainScale, Cfg.MaxGainScale);

            float positive = Cfg.AdaptiveAoA && st.PositiveAoALimit >= 3 ? st.PositiveAoALimit : Cfg.AoALimitDeg;
            float negative = Cfg.AdaptiveAoA && st.NegativeAoALimit >= 3 ? st.NegativeAoALimit : Cfg.AoALimitDeg;
            o.PositiveAoALimit = positive; o.NegativeAoALimit = negative;
            float damping = 1 + (Cfg.FineDamping - 1) * (1 - w);
            // Spin detection.
            bool spinning = MathF.Abs(rr) > Cfg.SpinYawRate && MathF.Abs(alpha) > ((alpha >= 0 ? positive : negative) + 6) * Deg;
            spinTimer = spinning ? spinTimer + st.Dt : MathF.Max(0, spinTimer - st.Dt);
            float uP, uY, uR;
            if (spinTimer > 0.4f)
            {
                o.Spin = true; o.Mode = "SPIN RECOVERY";
                uP = alpha > 0 ? -0.6f : 0.3f;
                uY = -Math.Clamp(rr * 2f, -1, 1);
                uR = 0;
            }
            else
            {
                uP = trim.X + g * (Cfg.PitchGain * eUp - Cfg.PitchDamping * damping * qr);
                if (!push && w > 0.5f) uP = MathF.Max(uP, -0.15f);      // don't push while banking to the target
                uR = g * (Cfg.RollGain * eRoll - Cfg.RollDamping * damping * pr);
                uY = trim.Y + g * ((1 - w) * Cfg.YawGain * eRight + Cfg.SlipGain * beta - Cfg.YawDamping * damping * rr);

                // Envelope: angle of attack and load factor limit the pull.
                float aoaCap = Cfg.AoAGain * (positive * Deg - alpha);
                if (uP > aoaCap) { uP = MathF.Max(aoaCap, -1); o.AoALimited = true; }
                float aoaFloor = -Cfg.AoAGain * (negative * Deg + alpha);
                if (uP < aoaFloor) { uP = MathF.Min(aoaFloor, 1); o.AoALimited = true; }
                float n = V * qr / 9.81f;
                if (n > Cfg.GLimit && uP > 0) { uP *= Math.Clamp(1 - (n - Cfg.GLimit) / 2f, 0, 1); o.GLimited = true; }
                // Integrate only after envelope flags are known. Freeze a manually owned
                // axis and back off stored trim during protection, avoiding a release kick.
                if (theta < Cfg.TrimWindowDeg * Deg && !o.AoALimited && !o.GLimited && !o.Terrain)
                {
                    float c = MathF.Cos(eRoll) > 0 ? 1f : 0f;
                    var add = new Vector3(eUp, eRight, 0) * (Cfg.TrimRate * st.Dt * c);
                    if (st.ManualPitch || (MathF.Abs(uP) >= .98f && MathF.Sign(add.X) == MathF.Sign(uP))) add.X = 0;
                    if (st.ManualYaw || (MathF.Abs(uY) >= .98f && MathF.Sign(add.Y) == MathF.Sign(uY))) add.Y = 0;
                    trim = Vector3.Clamp(trim + add, new Vector3(-Cfg.TrimLimit), new Vector3(Cfg.TrimLimit));
                }
                else if (o.AoALimited || o.GLimited) trim.X *= MathF.Exp(-st.Dt * 2);
                o.Mode = o.AoALimited ? "AOA LIMIT" : o.GLimited ? "LOAD LIMIT" : o.Terrain ? "TERRAIN" :
                    o.AimErrorDeg < .5f ? "HOLD" : o.AimErrorDeg < 6 ? "FINE AIM" : push ? "DESCENT" : "TURN";
            }

            var raw = new Vector3(Math.Clamp(uP, -1, 1), Math.Clamp(uY, -1, 1), Math.Clamp(uR, -1, 1));
            o.ControlLimited = o.AimErrorDeg > 6 && (MathF.Abs(raw.X) > .98f || MathF.Abs(raw.Y) > .98f || MathF.Abs(raw.Z) > .98f);
            if (o.ControlLimited && !o.AoALimited && !o.GLimited && !o.Spin && !o.Terrain) o.Mode = "CONTROL LIMIT";
            float k = Cfg.Smoothing > 0 ? 1 - MathF.Exp(-st.Dt / Cfg.Smoothing) : 1;
            smooth += (raw - smooth) * k;
            o.PitchUp = smooth.X; o.YawRight = smooth.Y; o.RollRight = smooth.Z;
            o.Valid = Finite(smooth);
            if (!o.Valid) { smooth = Vector3.Zero; o.Mode = "INVALID"; }
            return o;
        }
    }
}
