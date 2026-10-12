using System;

namespace FlyoutMouseAim
{
    // The game adapter supplies geometry/curve estimates and area-weighted separation.
    // Learning is passive: it never deliberately stalls the aircraft to discover a limit.
    public sealed class AoaEnvelope
    {
        public float PeakPositive { get; private set; }
        public float PeakNegative { get; private set; }
        public float StablePositive { get; private set; }
        public float StableNegative { get; private set; }
        public float ObservedPositive { get; private set; }
        public float ObservedNegative { get; private set; }
        public bool HasModel { get; private set; }
        float lossPositive, lossNegative, lossTime;
        public void Reset()
        {
            HasModel = false; lossPositive = lossNegative = lossTime = 0;
            ObservedPositive = ObservedNegative = 0;
            PeakPositive = PeakNegative = StablePositive = StableNegative = 0;
        }
        public void SetModel(float positive, float negative)
        {
            HasModel = float.IsFinite(positive) && float.IsFinite(negative) && positive >= 4 && negative >= 4;
            PeakPositive = HasModel ? Math.Clamp(positive, 4, 45) : 0;
            PeakNegative = HasModel ? Math.Clamp(negative, 4, 45) : 0;
        }
        public void Observe(float alpha, float separation, float beta, float rollRate, float speed, float dt,
            bool allowed, float fallback, float margin)
        {
            if (!float.IsFinite(dt) || dt <= 0) return;
            bool clean = allowed && speed > 25 && MathF.Abs(beta) < 8 && MathF.Abs(rollRate) < .7f
                && float.IsFinite(alpha) && float.IsFinite(separation) && separation >= 0;
            float a = MathF.Abs(alpha);
            if (clean && separation < .08f)
            {
                // Slow evidence accumulation avoids declaring a transient excursion stable.
                if (alpha >= 0) ObservedPositive += MathF.Max(0, a - ObservedPositive) * (1 - MathF.Exp(-dt / 3));
                else ObservedNegative += MathF.Max(0, a - ObservedNegative) * (1 - MathF.Exp(-dt / 3));
            }
            lossTime = clean && separation > .2f ? lossTime + dt : 0;
            if (lossTime > .15f && a > 4)
            {
                float cap = MathF.Max(4, a - margin);
                if (alpha >= 0) lossPositive = lossPositive == 0 ? cap : MathF.Min(lossPositive, cap);
                else lossNegative = lossNegative == 0 ? cap : MathF.Min(lossNegative, cap);
            }
            // Keep learned reductions for this flight. A new craft/flight starts fresh.
            float pos = HasModel ? PeakPositive : fallback, neg = HasModel ? PeakNegative : fallback;
            StablePositive = MathF.Max(3, pos - margin);
            StableNegative = MathF.Max(3, neg - margin);
            if (lossPositive > 0) StablePositive = MathF.Min(StablePositive, lossPositive);
            if (lossNegative > 0) StableNegative = MathF.Min(StableNegative, lossNegative);
        }
    }
}
