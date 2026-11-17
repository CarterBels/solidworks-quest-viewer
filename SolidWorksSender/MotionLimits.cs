using System;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SolidWorksQuestSender
{
    /// How far a moving group may turn or slide from where it is assembled, from its limit mates.
    /// Angles in degrees about PartMotion.Direction (right-hand rule, SolidWorks/glTF space); slides in metres along it.
    /// Unbounded sides are ±infinity. The as-assembled position (0) is always inside the range.
    public sealed class MotionRange
    {
        public double SlideMin = double.NegativeInfinity, SlideMax = double.PositiveInfinity;
        public double AngleMin = double.NegativeInfinity, AngleMax = double.PositiveInfinity;

        public bool HasSlide => !double.IsInfinity(SlideMin) || !double.IsInfinity(SlideMax);
        public bool HasAngle => !double.IsInfinity(AngleMin) || !double.IsInfinity(AngleMax);
        public bool Any => HasSlide || HasAngle;

        public void LimitSlide(double lo, double hi)
        {
            SlideMin = Math.Max(SlideMin, Math.Min(lo, 0));
            SlideMax = Math.Min(SlideMax, Math.Max(hi, 0));
        }

        public void LimitAngle(double lo, double hi)
        {
            AngleMin = Math.Max(AngleMin, Math.Min(lo, 0));
            AngleMax = Math.Min(AngleMax, Math.Max(hi, 0));
        }
    }

    /// SolidWorks limit mates (advanced distance / angle mates with a minimum and maximum). They don't fix a part,
    /// they bound how far it can move, so Motion.Analyze must not treat them as constraints; instead their bounds
    /// are turned into a MotionRange for the group's single axis. Never throws; returns false when unsure.
    public static class MotionLimits
    {
        /// True for a limit distance or limit angle mate. min/max/current in system units: metres for distance,
        /// radians for angle. current is NaN if the mate's present value can't be read.
        public static bool IsLimit(Mate2 mate, Feature feature, out double min, out double max, out double current)
        {
            min = max = current = double.NaN;
            try
            {
                if (mate == null) return false;
                var type = (swMateType_e)mate.Type;
                if (type != swMateType_e.swMateDISTANCE && type != swMateType_e.swMateANGLE) return false;

                // The feature definition holds the absolute limits (Mate2.Minimum/MaximumVariation are relative to the
                // current value: on Turret Assembly LimitAngle1 has min 0, max 15° but variation -15°..0).
                double lo = double.NaN, hi = double.NaN;
                object def = null;
                try { def = feature?.GetDefinition(); } catch (Exception) { }
                if (def is DistanceMateFeatureData dd0) { lo = dd0.MinimumDistance; hi = dd0.MaximumDistance; }
                else if (def is AngleMateFeatureData ad0) { lo = ad0.MinimumAngle; hi = ad0.MaximumAngle; }
                if (double.IsNaN(lo) || double.IsNaN(hi) || !(Math.Abs(hi - lo) > 1e-9)) return false;
                min = Math.Min(lo, hi);
                max = Math.Max(lo, hi);

                // Present value: the feature data's (for a limit mate, where it sits now), else the mate's dimension.
                if (def is DistanceMateFeatureData dd) current = dd.Distance;
                else if (def is AngleMateFeatureData ad) current = ad.Angle;
                if (double.IsNaN(current))
                    try
                    {
                        var dim = mate.DisplayDimension?.GetDimension2(0);
                        if (dim != null) current = dim.SystemValue;
                    }
                    catch (Exception) { }
                return true;
            }
            catch (Exception) { return false; }
        }

        public static bool IsAngle(Mate2 mate)
        {
            try { return mate != null && (swMateType_e)mate.Type == swMateType_e.swMateANGLE; } catch (Exception) { return false; }
        }

        /// Slide bounds (metres along axis) from a limit DISTANCE mate between our entity (mine) and a held one (other).
        /// Points/directions in top-level space; kinds are swMateEntityTypes_e (Mate entity ReferenceType).
        /// Handles plane–plane and point–plane (either way round): the distance changes linearly as we slide.
        public static bool SlideBounds(double min, double max, double current,
                                       double[] minePoint, double[] mineDir, int mineKind,
                                       double[] otherPoint, double[] otherDir, int otherKind,
                                       double[] axis, out double lo, out double hi)
        {
            lo = double.NegativeInfinity; hi = double.PositiveInfinity;
            try
            {
                const int Point = (int)swMateEntityTypes_e.swMatePoint, Plane = (int)swMateEntityTypes_e.swMatePlane;
                var d = Unit(axis);
                Func<double, double> value;
                if (otherKind == Plane && (mineKind == Plane || mineKind == Point))
                {
                    var n = Unit(otherDir); // the held plane's normal: our point/plane moves across it
                    value = s => Math.Abs(Dot(Sub(Add(minePoint, Scale(d, s)), otherPoint), n));
                }
                else if (mineKind == Plane && otherKind == Point)
                {
                    var n = Unit(mineDir); // our plane moves (without turning) past the held point
                    value = s => Math.Abs(Dot(Sub(otherPoint, Add(minePoint, Scale(d, s))), n));
                }
                else return false;
                return Linear(value, 1e-4, min, max, current, 1e-3, out lo, out hi);
            }
            catch (Exception) { return false; }
        }

        /// Rotation bounds (degrees about axis, right-hand rule) from a limit ANGLE mate between our entity's direction
        /// (plane normal or line direction) and a held one.
        public static bool AngleBounds(double min, double max, double current,
                                       double[] mineDir, double[] otherDir, double[] axis,
                                       out double loDeg, out double hiDeg)
        {
            loDeg = double.NegativeInfinity; hiDeg = double.PositiveInfinity;
            try
            {
                var d = Unit(axis);
                var m = Unit(mineDir);
                var o = Unit(otherDir);
                Func<double, double> geometric = t => Math.Acos(Clamp(Dot(Rotate(m, d, t), o), -1, 1));
                double g0 = geometric(0);
                // SolidWorks may report the angle between the normals or its supplement (alignment/flip):
                // pick whichever matches the mate's present value.
                bool supplement = !double.IsNaN(current) && Math.Abs(current - (Math.PI - g0)) + 1e-6 < Math.Abs(current - g0);
                Func<double, double> value = supplement ? (t => Math.PI - geometric(t)) : geometric;
                if (!Linear(value, 1e-3, min, max, current, 0.2, out double lo, out double hi)) return false;
                loDeg = lo * 180 / Math.PI;
                hiDeg = hi * 180 / Math.PI;
                return true;
            }
            catch (Exception) { return false; }
        }

        /// Bounds on a mechanism mode's parameter t from a limit mate: 'rel' = the twist (w, v) of our side relative
        /// to the other side per unit t (top-level space). Our entity moves (to first order) as p + t(v + w x p), its
        /// direction as d + t(w x d). Distance: plane-plane and point-plane; angle: between normals / directions.
        public static bool ModeBounds(bool angle, double min, double max, double current,
                                      double[] minePoint, double[] mineDir, int mineKind,
                                      double[] otherPoint, double[] otherDir, int otherKind,
                                      double[] rel, out double lo, out double hi)
        {
            lo = double.NegativeInfinity; hi = double.PositiveInfinity;
            try
            {
                var w = new[] { rel[0], rel[1], rel[2] };
                var v = new[] { rel[3], rel[4], rel[5] };
                double[] P(double t) => Add(minePoint, Scale(Add(v, Cross(w, minePoint)), t));
                double[] D(double t) => Unit(Add(mineDir, Scale(Cross(w, mineDir), t)));
                const int Point = (int)swMateEntityTypes_e.swMatePoint, Plane = (int)swMateEntityTypes_e.swMatePlane;
                Func<double, double> value;
                if (angle)
                {
                    var o = Unit(otherDir);
                    Func<double, double> geometric = t => Math.Acos(Clamp(Dot(D(t), o), -1, 1));
                    double g0 = geometric(0);
                    bool supplement = !double.IsNaN(current) && Math.Abs(current - (Math.PI - g0)) + 1e-6 < Math.Abs(current - g0);
                    value = supplement ? (t => Math.PI - geometric(t)) : geometric;
                }
                else if (otherKind == Plane && (mineKind == Plane || mineKind == Point))
                {
                    var n = Unit(otherDir);
                    value = t => Math.Abs(Dot(Sub(P(t), otherPoint), n));
                }
                else if (mineKind == Plane && otherKind == Point)
                    value = t => Math.Abs(Dot(Sub(otherPoint, P(t)), D(t)));
                else return false;
                return Linear(value, 1e-5, min, max, current, 1e-6, out lo, out hi);
            }
            catch (Exception) { return false; }
        }

        /// value(x) ≈ value0 + k·x near x = 0; the x range that keeps value within [min, max]. False if value barely
        /// changes with x (|k| < minSlope): this limit doesn't bound this motion.
        static bool Linear(Func<double, double> value, double eps, double min, double max, double current, double minSlope,
                           out double lo, out double hi)
        {
            lo = double.NegativeInfinity; hi = double.PositiveInfinity;
            double v0 = value(0);
            double k = (value(eps) - value(-eps)) / (2 * eps);
            double cur = double.IsNaN(current) ? v0 : current; // the mate's own value absorbs offsets/arc conditions
            if (!double.IsNaN(k) && Math.Abs(k) < minSlope)
            {
                // A V: an unsigned value (an angle at 0°, a distance at 0) that grows whichever way it moves. Which
                // side SolidWorks counts as positive isn't known here, so either way may go as far as the range allows.
                double kp = (value(eps) - v0) / eps, km = (value(-eps) - v0) / eps;
                if (kp > minSlope && km > minSlope && max > cur)
                {
                    lo = -(max - cur) / km;
                    hi = (max - cur) / kp;
                    return true;
                }
                return false;
            }
            if (double.IsNaN(k)) return false;
            double a = (min - cur) / k, b = (max - cur) / k;
            lo = Math.Min(Math.Min(a, b), 0); // the assembled position always stays allowed
            hi = Math.Max(Math.Max(a, b), 0);
            return true;
        }

        // ---- vectors ----
        static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        static double[] Add(double[] a, double[] b) => new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] };
        static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        static double[] Scale(double[] a, double s) => new[] { a[0] * s, a[1] * s, a[2] * s };
        static double[] Cross(double[] a, double[] b) => new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
        static double[] Unit(double[] a) { double l = Math.Sqrt(Dot(a, a)); return l < 1e-12 ? new double[] { 0, 0, 1 } : Scale(a, 1 / l); }
        static double Clamp(double x, double lo, double hi) => x < lo ? lo : x > hi ? hi : x;

        /// Rodrigues: v turned by t radians about unit axis d (right-hand rule).
        static double[] Rotate(double[] v, double[] d, double t)
        {
            double c = Math.Cos(t), s = Math.Sin(t);
            var cross = Cross(d, v);
            double dot = Dot(d, v);
            return new[]
            {
                v[0] * c + cross[0] * s + d[0] * dot * (1 - c),
                v[1] * c + cross[1] * s + d[1] * dot * (1 - c),
                v[2] * c + cross[2] * s + d[2] * dot * (1 - c),
            };
        }
    }
}
