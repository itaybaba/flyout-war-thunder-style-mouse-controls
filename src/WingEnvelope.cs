using System;
using System.Collections.Generic;
using Il2Cpp;
using UnityEngine;

namespace FlyoutMouseAim
{
    // Read-only adapter. ClCd takes degrees (verified in Lift.SolveSegments).
    // Find the first lift maximum / significant separation, excluding post-stall vortex peaks.
    internal sealed class WingEnvelope
    {
        readonly List<(Lift wing, float area, float peak)> wings = new();
        float nextScan;
        public int Count => wings.Count;
        public void Reset() { wings.Clear(); nextScan = 0; }
        static float Peak(float aspect)
        {
            float best = 0, bestAngle = 4;
            for (float a = 1; a <= 45; a += .5f)
            {
                float stall = 0;
                var coeff = Aerodynamics.ClCd(a, aspect, out stall);
                if (!float.IsFinite(coeff.x) || !float.IsFinite(stall)) return 0;
                if (coeff.x > best) { best = coeff.x; bestAngle = a; }
                if (a >= 5 && (coeff.x < best * .98f || stall >= .18f)) return MathF.Max(4, MathF.Min(a, bestAngle));
            }
            return bestAngle;
        }
        public void Update(Craft craft, AoaEnvelope model, float bodyAlpha)
        {
            if (Time.unscaledTime < nextScan) return;
            nextScan = Time.unscaledTime + 2;
            wings.Clear();
            var seen = new HashSet<IntPtr>();
            if (craft.wings != null)
                foreach (var solver in craft.wings)
                {
                    if (solver == null || solver.wings == null || !float.IsFinite(solver.EffectiveAR) || solver.EffectiveAR <= .1f) continue;
                    float peak = Peak(solver.EffectiveAR);
                    if (peak < 4) continue;
                    foreach (var wing in solver.wings)
                    {
                        if (wing == null || !seen.Add(wing.Pointer) || wing.isControlSurface || wing.area <= .01f) continue;
                        float alignment = MathF.Abs(Vector3.Dot(wing.transform.up, craft.transform.up));
                        if (alignment < .5f) continue; // fins are not pitch-lift surfaces
                        wings.Add((wing, wing.area * alignment * alignment, peak));
                    }
                }
            var pos = new List<(float angle, float area)>();
            var neg = new List<(float angle, float area)>();
            float total = 0;
            foreach (var w in wings)
            {
                // Geometric incidence also works on the ground. Local flow and camber can
                // differ; observed separation corrects the estimate during actual flight.
                var localForward = craft.transform.InverseTransformDirection(w.wing.transform.forward);
                float incidence = Mathf.Atan2(localForward.y, localForward.z) * Mathf.Rad2Deg;
                if (!float.IsFinite(incidence) || MathF.Abs(incidence) > 30) continue;
                pos.Add((Math.Clamp(w.peak - incidence, 4, 45), w.area));
                neg.Add((Math.Clamp(w.peak + incidence, 4, 45), w.area));
                total += w.area;
            }
            // Protect the earliest-stalling substantial part of the lift system.
            model.SetModel(Quantile(pos, total), Quantile(neg, total));
        }
        static float Quantile(List<(float angle, float area)> values, float total)
        {
            if (total <= 0) return 0;
            values.Sort((a,b) => a.angle.CompareTo(b.angle));
            float sum = 0;
            foreach (var v in values) { sum += v.area; if (sum >= total * .2f) return v.angle; }
            return 0;
        }
        public float Separation()
        {
            float sum = 0, area = 0;
            foreach (var w in wings)
                if (w.wing != null && float.IsFinite(w.wing.stalled))
                { sum += Math.Clamp(w.wing.stalled, 0, 1) * w.area; area += w.area; }
            return area > 0 ? sum / area : float.NaN;
        }
    }
}
