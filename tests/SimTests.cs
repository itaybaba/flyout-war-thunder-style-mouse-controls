// Offline synthetic tests for the Instructor. A simple rigid-body aircraft (first-order
// actuators, per-axis aerodynamic damping, trim moments, velocity lagging the nose) is flown
// purely through control inputs. These are synthetic checks of the control law, not a
// claim about Flyout's flight model.

using System;
using System.Collections.Generic;
using System.Numerics;
using FlyoutMouseAim;

static class Sim
{
    public sealed class Plane
    {
        public Quaternion Q = Quaternion.Identity;          // body -> world
        public Vector3 W;                                    // control-convention rates (q, r, p)
        public Vector3 Act;                                  // actuator state (pitchUp, yawRight, rollRight)
        public Vector3 Vel = new Vector3(0, 0, 120);        // world m/s
        public Vector3 B = new Vector3(4.0f, 1.2f, 10f);     // true authority at 100 m/s
        public Vector3 A = new Vector3(-2.0f, -1.2f, -3.0f);
        public Vector3 C = new Vector3(-0.6f, 0.05f, 0.15f); // trim moments
        public float ActTau = 0.06f;
        public float LiftK = 2.5f;

        public Vector3 OmegaUnity => new Vector3(-W.X, W.Y, -W.Z);
        public Vector3 Forward => Vector3.Transform(Vector3.UnitZ, Q);

        public void Step(Vector3 u, float dt)
        {
            Act += (Vector3.Clamp(u, -Vector3.One, Vector3.One) - Act) * (1f - MathF.Exp(-dt / ActTau));
            float V = Vel.Length();
            float s = (V / 100f) * (V / 100f);
            Vector3 wdot = B * s * Act + A * MathF.Sqrt(s) * W + C * s;   // aero moments scale with dynamic pressure
            W += wdot * dt;
            Vector3 omegaWorld = Vector3.Transform(OmegaUnity, Q);
            float ang = omegaWorld.Length() * dt;
            if (ang > 1e-9f) Q = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.Normalize(omegaWorld), ang) * Q);
            Vector3 f = Forward, vh = Vector3.Normalize(Vel);
            Vector3 turn = f - vh * Vector3.Dot(vh, f);
            vh = Vector3.Normalize(vh + turn * LiftK * dt);
            Vel = vh * V;
        }
    }

    public static (float settle, float overshoot, float finalErr, float maxAlpha, int rollFlips) Run(
        Vector3 targetWorld, float fps, float seconds, Action<Plane> setup = null, InstructorConfig cfg = null,
        bool trace = false, bool corruptTelemetry = false, bool warmup = false)
    {
        var plane = new Plane();
        setup?.Invoke(plane);
        var ins = new Instructor(cfg ?? new InstructorConfig());
        float fixedDt = 0.02f, frameDt = 1f / fps, t = warmup ? -10f : 0f, acc = 0f;
        Vector3 u = Vector3.Zero, prevW = plane.OmegaUnity, wdotF = Vector3.Zero;
        float settle = -1f, overshoot = 0f, err = 0f, maxAlpha = 0f, prevRoll = 0f;
        int rollFlips = 0;
        bool reached = false;
        targetWorld = Vector3.Normalize(targetWorld);
        Vector3 initialForward = plane.Forward;
        Vector3 f0 = plane.Forward - targetWorld;
        while (t < seconds)
        {
            acc += frameDt;
            while (acc >= fixedDt)
            {
                plane.Step(u, fixedDt);
                Vector3 w = plane.OmegaUnity;
                Vector3 wdot = (w - prevW) / fixedDt; prevW = w;
                wdotF += (wdot - wdotF) * 0.5f;
                acc -= fixedDt;
            }
            Quaternion inv = Quaternion.Conjugate(plane.Q);
            var st = new AircraftState
            {
                TargetDir = Vector3.Transform(t < 0 ? initialForward : targetWorld, inv),
                Omega = plane.OmegaUnity,
                AirVel = Vector3.Transform(plane.Vel, inv),
                Up = Vector3.Transform(Vector3.UnitY, inv),
                RadarAlt = float.NaN,
                VerticalSpeed = plane.Vel.Y,
                Dt = frameDt,
            };
            if (corruptTelemetry && t > 1f && t < 1.2f) st.Omega = new Vector3(float.NaN, 0, 0);
            var o = ins.Step(st);
            u = o.Valid ? new Vector3(o.PitchUp, o.YawRight, o.RollRight) : Vector3.Zero;
            if (t < 0) { t += frameDt; continue; }
            // goal discontinuity: bank-goal jumps > 45 deg in a single frame
            if (o.Valid && t > frameDt && MathF.Abs(o.ErrRoll - prevRoll) > 45f * MathF.PI / 180f && MathF.Abs(MathF.Abs(o.ErrRoll - prevRoll) - 2 * MathF.PI) > 0.8f) rollFlips++;
            if (o.Valid) prevRoll = o.ErrRoll;
            err = MathF.Acos(Math.Clamp(Vector3.Dot(plane.Forward, targetWorld), -1f, 1f)) * 180f / MathF.PI;
            if (o.Valid) maxAlpha = MathF.Max(maxAlpha, o.AlphaDeg);
            if (!reached && err < 2f) { reached = true; settle = t; }
            if (Vector3.Dot(plane.Forward - targetWorld, f0) < 0f) overshoot = MathF.Max(overshoot, err); // passed the target
            if (reached && err > 2f) settle = -1f;
            if (reached && err <= 2f && settle < 0) settle = t;
            if (trace && ((int)(t * fps)) % (int)(fps / 4) == 0)
                Console.WriteLine($"  t={t,5:F2} err={err,6:F2} mode={o.Mode,-10} u=({u.X,5:F2},{u.Y,5:F2},{u.Z,5:F2}) a={o.AlphaDeg,5:F1} eUp={o.ErrUp*57.3f,6:F1} eR={o.ErrRight*57.3f,6:F1} eRoll={o.ErrRoll*57.3f,6:F1} w=({plane.W.X:F2},{plane.W.Y:F2},{plane.W.Z:F2})");
            t += frameDt;
        }
        return (settle, overshoot, err, maxAlpha, rollFlips);
    }

    static Vector3 Dir(float yawDeg, float pitchDeg)
    {
        float y = yawDeg * MathF.PI / 180f, p = pitchDeg * MathF.PI / 180f;
        return new Vector3(MathF.Sin(y) * MathF.Cos(p), MathF.Sin(p), MathF.Cos(y) * MathF.Cos(p));
    }

    static int fails;
    static void Check(string name, bool ok, string detail)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  {detail}");
        if (!ok) fails++;
    }

    public static int Main(string[] args)
    {
        bool trace = args.Length > 0 && args[0] == "trace";
        if (args.Length > 0 && args[0] == "trace40")
        {
            Run(Dir(25, 10), 60f, 7f, p => p.Vel = new Vector3(0, 0, 40), null, trace: true);
            return 0;
        }
        if (args.Length > 0 && args[0] == "exp")
        {
            foreach (var (label, mk) in new (string, Func<InstructorConfig>)[] {
                ("default", () => new InstructorConfig()),
                ("maxLead0.7", () => new InstructorConfig { MaxLeadTime = 0.7f }),
                ("maxLead0.7+rd", () => new InstructorConfig { MaxLeadTime = 0.7f, RollDamping = 0.4f }),
            })
            foreach (float v in new[] { 40f, 70f, 120f, 200f })
            {
                var r = Run(Dir(25, 10), 60f, 10f, p => p.Vel = new Vector3(0, 0, v), mk());
                Console.WriteLine($"{label,-10} v={v,4} settle={r.settle,5:F2} over={r.overshoot,5:F2} final={r.finalErr:F2}");
            }
            return 0;
        }
        // Warm-up note: each run starts from the conservative priors (no pre-identified model).
        foreach (float fps in new[] { 30f, 60f, 120f })
        {
            foreach (var (name, d) in new[] { ("20deg right", Dir(20, 0)), ("20deg up", Dir(0, 20)), ("20deg down", Dir(0, -20)), ("20deg diag", Dir(14, 14)) })
            {
                var r = Run(d, fps, 8f, trace: trace && fps == 60f);
                Check($"{name} @{fps}fps", r.settle >= 0 && r.settle <= 5f && r.overshoot <= 5f && r.finalErr < 2f && r.rollFlips == 0,
                    $"settle={r.settle:F2}s overshoot={r.overshoot:F2}deg final={r.finalErr:F2}deg rollFlips={r.rollFlips}");
            }
        }
        {
            var r = Run(Dir(0, 0), 60f, 30f);
            Check("steady hold 30s", r.finalErr < 2f, $"final={r.finalErr:F2}deg (trim moment present)");
        }
        foreach (var (name, d) in new[] { ("90deg right", Dir(90, 0)), ("150deg behind-left", Dir(-150, 10)), ("180deg dead astern", Dir(180, 0)), ("60deg up", Dir(0, 60)) })
        {
            var r = Run(d, 60f, 15f);
            Check($"large turn {name}", r.finalErr < 2f && r.rollFlips == 0, $"final={r.finalErr:F2}deg settle={r.settle:F2}s maxAoA={r.maxAlpha:F1} rollFlips={r.rollFlips}");
        }
        {
            var r = Run(Dir(10, 5), 60f, 12f, p => p.Q = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI));
            Check("inverted start", r.finalErr < 2f, $"final={r.finalErr:F2}deg");
        }
        {
            var r = Run(new Vector3(0.2f, 1f, 0.1f), 60f, 12f, p => { p.Q = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2.2f); p.Vel = Vector3.Transform(new Vector3(0, 0, 120), p.Q); });
            Check("near-vertical climb target", r.finalErr < 3f, $"final={r.finalErr:F2}deg");
        }
        {
            var cfg = new InstructorConfig { AoALimitDeg = 10f };
            var r = Run(Dir(0, 80), 60f, 6f, p => { p.LiftK = 0.6f; p.Vel = new Vector3(0, 0, 60); }, cfg);
            Check("AoA limit respected", r.maxAlpha < 12.5f, $"maxAoA={r.maxAlpha:F1}deg limit=10");
        }
        {
            var r = Run(Dir(20, 0), 60f, 8f, corruptTelemetry: true);
            Check("invalid telemetry recovery", r.finalErr < 2f, $"final={r.finalErr:F2}deg");
        }
        foreach (float k in new[] { 0.3f, 0.6f, 2f, 4f, 6f })
        {
            var r = Run(Dir(25, 10), 60f, 10f, p => p.B *= k);
            Check($"authority x{k}", r.finalErr < 2f && r.overshoot < 6f && r.rollFlips == 0, $"settle={r.settle:F2}s final={r.finalErr:F2} overshoot={r.overshoot:F2}");
        }
        foreach (float v in new[] { 40f, 70f, 200f })
        {
            var r = Run(Dir(25, 10), 60f, 10f, p => p.Vel = new Vector3(0, 0, v));
            Check($"speed {v} m/s", r.finalErr < 2f && r.overshoot < 6f && r.rollFlips == 0, $"settle={r.settle:F2}s final={r.finalErr:F2} overshoot={r.overshoot:F2} rollFlips={r.rollFlips}");
        }
        foreach (float fps in new[] { 30f, 60f, 120f })
        foreach (float angle in new[] { .5f, 1f, 3f, 5f })
        foreach (float side in new[] { -1f, 1f })
        {
            var r = Run(Dir(angle * side, 0), fps, 12f, warmup: true);
            Check($"fine {angle * side}deg @{fps}fps", r.finalErr < .3f && r.overshoot < 1.5f && r.rollFlips == 0,
                $"final={r.finalErr:F3} overshoot={r.overshoot:F3}");
        }
        foreach (float fps in new[] { 30f, 60f, 120f })
        {
            var mouse = new MouseResponse(); float dt = 1 / fps;
            Vector2 sum = Vector2.Zero;
            for (int n = 0; n < (int)fps; n++) sum += mouse.Step(new Vector2(100 * dt, 0), dt, .09f, .4f, 1000, .025f);
            for (int n = 0; n < (int)fps; n++) sum += mouse.Step(Vector2.Zero, dt, .09f, .4f, 1000, .025f);
            float expected = 9 * (.4f + .6f * .028f);
            Check($"mouse displacement @{fps}fps", MathF.Abs(sum.X - expected) < .0001f && sum.Y == 0, $"degrees={sum.X:F5}");
            mouse.Reset();
            var tiny = mouse.Step(new Vector2(.01f, 0), dt, .09f, .4f, 1000, .025f);
            Check($"sub-count mouse @{fps}fps", tiny.X > 0 && float.IsFinite(tiny.X), $"delta={tiny.X}");
        }
        {
            var e = new AoaEnvelope(); e.SetModel(12, 18);
            for(int i=0;i<300;i++) e.Observe(5, 0, 0, 0, 90, .02f, true, 14, 2);
            Check("aircraft-specific asymmetric envelope", e.StablePositive == 10 && e.StableNegative == 16, $"{e.StablePositive}/{e.StableNegative}");
            for(int i=0;i<30;i++) e.Observe(9, .5f, 0, 0, 90, .02f, true, 14, 2);
            Check("observed separation reduces only affected side", e.StablePositive == 7 && e.StableNegative == 16, $"{e.StablePositive}/{e.StableNegative}");
            e.Reset(); e.SetModel(26, 22); e.Observe(5, 0, 0, 0, 90, .02f, true, 14, 2);
            Check("new craft discards previous learning", e.StablePositive == 24 && e.ObservedPositive < 1, $"{e.StablePositive}");
            for(int i=0;i<100;i++) e.Observe(6, .9f, 20, 0, 90, .02f, true, 14, 2);
            Check("sideslip rejects false stall learning", e.StablePositive == 24, $"{e.StablePositive}");
            e.Reset(); e.Observe(5, float.NaN, 0, 0, 90, .02f, true, 14, 2);
            Check("missing wing data uses labelled fallback", !e.HasModel && e.StablePositive == 12, $"{e.StablePositive}");
        }
        {
            var ins = new Instructor(new InstructorConfig());
            var st = new AircraftState { TargetDir = Dir(0, 15), AirVel = new Vector3(0,-15,90), Up = Vector3.UnitY, Dt=.02f, RadarAlt=float.NaN,
                PositiveAoALimit=6, NegativeAoALimit=20 };
            var o = ins.Step(st);
            Check("adaptive pull limit reaches controller", o.AoALimited && o.PitchUp < 0 && o.PositiveAoALimit == 6, $"u={o.PitchUp}");
            st.AirVel.Y = 40; st.TargetDir = Dir(0,-30);
            o = ins.Step(st);
            Check("push protection reports limiting", o.AoALimited, o.Mode);
            st.Dt=float.NaN; o=ins.Step(st);
            Check("nonfinite timestep rejected", !o.Valid, o.Mode);
        }
        {
            var weak = Run(Dir(25,10),60,14,p => p.B *= .3f);
            var strong = Run(Dir(25,10),60,14,p => p.B *= 2);
            Check("airframe maneuverability is preserved", weak.settle > strong.settle * 1.5f && strong.settle > 0,
                $"weak={weak.settle:F2}s strong={strong.settle:F2}s (same Instructor settings)");
            var noAuthority = Run(Dir(0,20),60,8,p => {p.B=Vector3.Zero; p.C=Vector3.Zero;});
            Check("no artificial turn without control authority", noAuthority.finalErr > 19,
                $"error={noAuthority.finalErr:F2}deg; target remains unreachable");
        }
        foreach (float fps in new[] {30f,60f,120f})
        foreach (float pitch in new[] {-.5f,.5f,-1f,1f,-3f,3f})
        {
            var r=Run(Dir(0,pitch),fps,12,warmup:true);
            Check($"fine pitch {pitch}deg @{fps}fps",r.finalErr<.3f && r.overshoot<1.5f && r.rollFlips==0,
                $"final={r.finalErr:F3} overshoot={r.overshoot:F3}");
        }
        // Artificial assistance may modify ONLY the yaw component, including in banked flight.
        foreach (var q in new[] {Quaternion.Identity, Quaternion.CreateFromYawPitchRoll(.7f,.8f,1.2f)})
        {
            var local = new Vector3(.4f,.3f,-.6f);
            var world = Vector3.Transform(local,q); var axis = Vector3.Transform(Vector3.UnitY,q);
            var after = Vector3.Transform(YawStabilizer.Apply(world,axis,-.02f),Quaternion.Conjugate(q));
            Check("yaw-only projection",MathF.Abs(after.X-local.X)<1e-6f && MathF.Abs(after.Z-local.Z)<1e-6f && MathF.Abs(after.Y-.28f)<1e-6f,$"{after}");
        }
        foreach(float dt in new[]{.01f,.02f,.0333333f})
        {
            var assist=new YawStabilizer(); float r=.2f,beta=.08f, peak=0;
            for(int i=0;i<10/dt;i++)
            {
                // Under-damped aircraft yaw: natural restoring fin and slightly negative damping.
                r+=(1.2f*beta+.15f*r)*dt;
                float d=assist.Step(new Vector3(0,0,100),Vector3.UnitY,r,beta,0,dt,.65f,.5f,true);
                peak=MathF.Max(peak,MathF.Abs(d));r+=d;beta-=r*dt;
            }
            Check($"yaw wobble settles dt={dt}",MathF.Abs(r)<.01f && MathF.Abs(beta)<.01f && peak<=.65f*.5f*dt+1e-6f,$"rate={r:F5} slip={beta:F5}");
            float released=assist.Step(new Vector3(0,0,100),Vector3.UnitY,1,.1f,0,dt,.65f,.5f,false);
            Check("yaw assist release",released==0,$"{released}");
        }
        {
            var assist=new YawStabilizer();float max=0;
            for(int i=0;i<500;i++)
            {
                float heading=i*.02f*.15f;
                var flow=new Vector3(MathF.Sin(heading),0,MathF.Cos(heading))*100;
                float d=assist.Step(flow,Vector3.UnitY,.15f,0,0,.02f,.65f,.5f,true);
                if(i>100)max=MathF.Max(max,MathF.Abs(d));
            }
            Check("coordinated turn preserved",max<.00001f,$"max correction={max}");
            float stalled=assist.Step(new Vector3(0,0,100),Vector3.UnitY,1,.1f,.8f,.02f,.65f,.5f,true);
            Check("yaw assistance fades at stalled AoA",stalled==0,$"{stalled}");
            float invalid=assist.Step(new Vector3(float.NaN,0,100),Vector3.UnitY,1,.1f,0,.02f,.65f,.5f,true);
            Check("yaw invalid telemetry rejected",invalid==0,$"{invalid}");
        }
        {
            var assist = new YawStabilizer(); float d=0;
            for(int i=0;i<100;i++) d=assist.Step(new Vector3(0,0,100),Vector3.UnitY,0,0,0,.02f,.65f,.5f,true,.02f,1);
            Check("yaw assist follows fine aim intent",d>0 && d<=.0065f,$"delta={d}");
        }
        Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILED");
        return fails == 0 ? 0 : 1;
    }
}
