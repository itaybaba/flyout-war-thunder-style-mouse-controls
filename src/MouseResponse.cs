using System;
using System.Numerics;

namespace FlyoutMouseAim
{
    // Exact integration of a first-order velocity filter: slow movements retain every count,
    // including the short tail after stopping. Response uses counts/second, not counts/frame.
    public sealed class MouseResponse
    {
        Vector2 velocity;
        public void Reset() => velocity = Vector2.Zero;
        public Vector2 Step(Vector2 counts, float dt, float sensitivity, float precision, float fullSpeed, float smoothing)
        {
            if (!float.IsFinite(dt) || dt <= 0 || dt > .2f || !float.IsFinite(counts.X) || !float.IsFinite(counts.Y))
            { Reset(); return Vector2.Zero; }
            float t = Math.Clamp(counts.Length() / dt / MathF.Max(fullSpeed, 1), 0, 1);
            t = t * t * (3 - 2 * t);
            var wanted = counts / dt * sensitivity * (precision + (1 - precision) * t);
            if (smoothing <= 0) { velocity = wanted; return counts * sensitivity * (precision + (1 - precision) * t); }
            float k = 1 - MathF.Exp(-dt / smoothing);
            Vector2 delta = wanted * dt + (velocity - wanted) * (smoothing * k);
            velocity += (wanted - velocity) * k;
            return delta;
        }
    }
}
