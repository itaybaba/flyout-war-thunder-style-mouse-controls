using System;
using System.Numerics;

namespace FlyoutMouseAim
{
    // Deliberate arcade assistance on BODY YAW ONLY. Returns a bounded rate increment;
    // the adapter adds it along the craft's up axis, preserving pitch/roll components.
    // Reference the turning airflow, so coordinated turns are not braked toward zero yaw.
    public sealed class YawStabilizer
    {
        Vector3 previousFlow;
        bool initialized;
        float flowRate, blend;
        public float FlowRate => flowRate;
        public void Reset() { initialized = false; flowRate = blend = 0; }
        static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
        static float Ramp(float a, float b, float v)
        { float x = Math.Clamp((v-a)/(b-a),0,1); return x*x*(3-2*x); }

        public float Step(Vector3 worldAir, Vector3 bodyUp, float yawRate, float beta, float alpha,
            float dt, float strength, float maxAcceleration, bool allowed, float aimRight = 0, float aimErrorDeg = 0)
        {
            if (!allowed || !Finite(worldAir) || !Finite(bodyUp) || bodyUp.LengthSquared() < .9f ||
                !float.IsFinite(yawRate) || !float.IsFinite(beta) || !float.IsFinite(alpha) ||
                !float.IsFinite(dt) || dt <= 0 || dt > .1f || !float.IsFinite(strength) ||
                !float.IsFinite(maxAcceleration) || !float.IsFinite(aimRight) || !float.IsFinite(aimErrorDeg) || strength <= 0 || maxAcceleration <= 0 || worldAir.Length() < 20)
            { Reset(); return 0; }
            Vector3 flow = Vector3.Normalize(worldAir);
            float measured = initialized ? Vector3.Dot(Vector3.Cross(previousFlow, flow), bodyUp) / dt : yawRate;
            previousFlow = flow; initialized = true;
            flowRate += (Math.Clamp(measured, -2, 2) - flowRate) * (1-MathF.Exp(-dt/.15f));
            blend += (1-blend) * (1-MathF.Exp(-dt/.25f));
            float fade = Ramp(20,45,worldAir.Length()) * (1-Ramp(.35f,.7f,MathF.Abs(alpha)))
                * (1-Ramp(.35f,.8f,MathF.Abs(beta)));
            // Small intentional aiming needs a nonzero yaw-rate reference. Without it,
            // flow alignment fights the Instructor's rudder and introduces tracking lag.
            float fine = 1-Ramp(6,18,MathF.Abs(aimErrorDeg));
            float requestedRate = flowRate + fine * Math.Clamp(aimRight * 1.5f,-.26f,.26f);
            float acceleration = .5f * beta - 4f * (yawRate-requestedRate);
            return Math.Clamp(acceleration, -maxAcceleration, maxAcceleration) * dt
                * Math.Clamp(strength,0,1) * blend * fade;
        }

        public static Vector3 Apply(Vector3 worldRate, Vector3 bodyUp, float delta)
            => worldRate + bodyUp * delta;
    }
}
