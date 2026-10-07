using System;
using System.Collections.Generic;
using Ams.UI.Models;

namespace Ams.UI.Services;

/// <summary>
/// Human-like mouse engine (v0.9.0 → v0.9.8).
///
/// v0.9.2 (recording-matched): analysis of a real hand recording (Record Being Edited.txt —
/// 11,295 samples) showed median 3 px per micro-step, median 5 ms cadence, and a MULTI-PEAK
/// speed profile (overlapping submovement bursts — not a single clean bell). The plan now
/// produces a DENSE micro-step stream (WindMouse spine resampled by arc length to ~3 px,
/// timed with ease-in-out base × 1–3 Gaussian speed bumps × per-point jitter) that RunEngine
/// streams through the bridge in ONE send_path operation — no per-point serial round-trips.
/// ControlPoints (few, firmware-smoothstepped) remain as the fallback for older bridge.py.
///
/// Path: WindMouse algorithm (gravity pull toward the target + random "wind"
/// perturbation + adaptive step size) → dense, naturally-curved waypoints instead
/// of the old single-midpoint two-segment path. Waypoint delays follow an
/// ease-in-out velocity profile (slow start, fast cruise, slow landing) whose
/// total budget comes from the app's px/s speed range.
///
/// Pause management (user request 2026-08-26): every move can carry
///   • a reaction pause BEFORE the first waypoint (hand "thinking"),
///   • one optional hesitation pause mid-path (chance %),
///   • a settle pause AFTER arrival,
///   • and a long "distraction" break (e.g. 0–5000 ms) once every N moves.
/// All ranges are per-step configurable on Random Mouse Position; mouseMove and
/// Find Image use the built-in <see cref="Config.Gentle"/> preset.
///
/// Pure logic only (no WPF/WinForms) so tests can drive it directly.
/// </summary>
public static class HumanMouse
{
    public readonly record struct Waypoint(int X, int Y, int DelayMs);

    /// <summary>One planned move: pauses around + the dense human stream + the few
    /// firmware-smoothstep control points used as fallback on old bridges (v0.9.2).</summary>
    public sealed class Plan
    {
        public int BeforeMs { get; init; }                    // reaction pause
        public required List<Waypoint> Waypoints { get; init; }       // dense ~3px micro-steps (streamed via send_path)
        public required List<Waypoint> ControlPoints { get; init; }   // fallback for bridge.py < v0.9.2 (abs,1)
        public int AfterMs { get; init; }                     // settle pause
        public int LongPauseMs { get; init; }                 // every-N-moves break (0 = not due)
        public bool Overshot { get; init; }                   // path included overshoot+correction
        public bool Arced { get; init; }                      // v0.9.7: true elliptical/circular arc
        public int ArcHeightPx { get; init; }                 // maximum perpendicular arc height
        public int SampledCurvePct { get; init; }             // v0.9.8: fresh value selected for this move
        public int CurveMinPct { get; init; }                 // requested dynamic range (for log/tests)
        public int CurveMaxPct { get; init; }
        public string SelectedSpeedMode { get; init; } = "legacy";
        public int SpeedCap { get; init; }
        public int TargetMoveMs { get; init; }                // v0.9.10: sampled duration target (0 = speed-driven)
    }

    /// <summary>Per-move humanization settings.</summary>
    public sealed class Config
    {
        public int PauseBeforeMinMs { get; init; } = 120;
        public int PauseBeforeMaxMs { get; init; } = 450;
        public int PauseAfterMinMs { get; init; } = 150;
        public int PauseAfterMaxMs { get; init; } = 600;
        public int MidPauseChancePct { get; init; } = 12;     // 0 = never hesitate mid-path
        public int MidPauseMinMs { get; init; } = 100;
        public int MidPauseMaxMs { get; init; } = 400;
        public int IdleEveryMinMoves { get; init; } = 5;      // long break cadence …
        public int IdleEveryMaxMoves { get; init; } = 12;     // … every 5–12 moves …
        public int IdlePauseMinMs { get; init; } = 1000;      // … lasting 1–5 s …
        public int IdlePauseMaxMs { get; init; } = 5000;      // … (set max 0 to disable)
        public int OvershootChancePct { get; init; } = 15;    // aim past the target, then correct
        public int CurveMinPct { get; init; } = 15;           // v0.9.8: sampled independently every move
        public int CurveMaxPct { get; init; } = 45;           // 0..100 bow, 101..200 true arc
        public int SpeedMinPxPerSec { get; init; } = 0;       // app setting (px/s) — max 0 = no path timing
        public int SpeedMaxPxPerSec { get; init; } = 2000;   // v0.9.24 — recalibrated from the dense hand recording (was 2300 → 500 → 2000)
        public string SpeedMode { get; init; } = "legacy";
        public string ProfileSource { get; init; } = "legacy";
        public int SpeedCap { get; init; }
        public int SlowWeight { get; init; } = 20;
        public int NormalWeight { get; init; } = 50;
        public int FastWeight { get; init; } = 30;
        public int CustomSpeedMin { get; init; } = 300;
        public int CustomSpeedMax { get; init; } = 530;
        public int MoveTimeMinMs { get; init; }               // v0.9.10: per-move duration range ms (0/0 = speed-driven)
        public int MoveTimeMaxMs { get; init; }

        /// <summary>Preset for plain Mouse Position / Find Image moves: human path and
        /// light pauses, but NO long idle breaks (those belong to Random Mouse Position).</summary>
        public static Config Gentle(int speedMin, int speedMax) => new()
        {
            PauseBeforeMinMs = 60, PauseBeforeMaxMs = 220,
            PauseAfterMinMs = 80, PauseAfterMaxMs = 280,
            MidPauseChancePct = 6, MidPauseMinMs = 80, MidPauseMaxMs = 250,
            IdleEveryMinMoves = 0, IdleEveryMaxMoves = 0,
            IdlePauseMinMs = 0, IdlePauseMaxMs = 0,           // disabled
            OvershootChancePct = 12,
            CurveMinPct = 20, CurveMaxPct = 40,
            SpeedMinPxPerSec = speedMin, SpeedMaxPxPerSec = speedMax,
        };

        /// <summary>Reads the Random Mouse Position dialog fields; missing keys (old
        /// .amsj files) fall back to the human defaults above.</summary>
        public static Config FromProps(IReadOnlyDictionary<string, object?> p, int speedMin, int speedMax,
                                       bool gentleDefaults = false)
        {
            // v0.9.14 — gentleDefaults: Find Image / Mouse Position dialogs default to the Gentle
            // preset (their previous fixed behavior) and never schedule long idle breaks; those
            // belong to Random Mouse Position wandering loops, not a single approach move.
            // Backward compatibility: a legacy fixed curvePct becomes a ±15 range automatically.
            // Example: 180 -> 165..195. New files store explicit curveMinPct/curveMaxPct.
            int legacy = Math.Clamp(PropEx.GetInt(p, "curvePct", 30), 0, 200);
            int curveMin = p.ContainsKey("curveMinPct")
                ? Math.Clamp(PropEx.GetInt(p, "curveMinPct", gentleDefaults ? 20 : 15), 0, 200)
                : gentleDefaults ? 20 : Math.Max(0, legacy - 15);
            int curveMax = p.ContainsKey("curveMaxPct")
                ? Math.Clamp(PropEx.GetInt(p, "curveMaxPct", gentleDefaults ? 40 : 45), 0, 200)
                : gentleDefaults ? 40 : Math.Min(200, legacy + 15);
            if (curveMax < curveMin) (curveMin, curveMax) = (curveMax, curveMin);
            int mtMin = Math.Max(0, PropEx.GetInt(p, "moveTimeMin", 0));
            int mtMax = Math.Max(0, PropEx.GetInt(p, "moveTimeMax", 0));
            if (mtMax < mtMin) (mtMin, mtMax) = (mtMax, mtMin);
            int effectiveSpeedMin = speedMin, effectiveSpeedMax = speedMax;
            bool hasSampledCadence = false;
            if (!gentleDefaults
                && HandMovementSample.TryDecode(PropEx.GetString(p, "handSample"), out var sample)
                && HandMovementSample.TryGetSpeedRange(sample, out var sampledMin, out var sampledMax))
            {
                effectiveSpeedMin = sampledMin;
                effectiveSpeedMax = sampledMax;
                hasSampledCadence = true;
            }
            // A fresh hand sample is the authoritative cadence source for
            // Random Mouse. Old explicit duration fields must not override it.
            if (hasSampledCadence) mtMin = mtMax = 0;

            return new Config
            {
                PauseBeforeMinMs = PropEx.GetInt(p, "pauseBeforeMin", gentleDefaults ? 60 : 120),
                PauseBeforeMaxMs = PropEx.GetInt(p, "pauseBeforeMax", gentleDefaults ? 220 : 450),
                PauseAfterMinMs = PropEx.GetInt(p, "pauseAfterMin", gentleDefaults ? 80 : 150),
                PauseAfterMaxMs = PropEx.GetInt(p, "pauseAfterMax", gentleDefaults ? 280 : 600),
                MidPauseChancePct = Math.Clamp(PropEx.GetInt(p, "midPauseChance", gentleDefaults ? 6 : 12), 0, 100),
                MidPauseMinMs = PropEx.GetInt(p, "midPauseMin", gentleDefaults ? 80 : 100),
                MidPauseMaxMs = PropEx.GetInt(p, "midPauseMax", gentleDefaults ? 250 : 400),
                IdleEveryMinMoves = gentleDefaults ? 0 : PropEx.GetInt(p, "idleEveryMin", 5),
                IdleEveryMaxMoves = gentleDefaults ? 0 : PropEx.GetInt(p, "idleEveryMax", 12),
                IdlePauseMinMs = gentleDefaults ? 0 : PropEx.GetInt(p, "idlePauseMin", 1000),
                IdlePauseMaxMs = gentleDefaults ? 0 : PropEx.GetInt(p, "idlePauseMax", 5000),
                OvershootChancePct = Math.Clamp(PropEx.GetInt(p, "overshootChance", gentleDefaults ? 12 : 15), 0, 100),
                CurveMinPct = curveMin,
                CurveMaxPct = curveMax,
                SpeedMode = PropEx.GetString(p, "speedMode", "legacy"),
                ProfileSource = PropEx.GetString(p, "handProfileSource", "legacy"),
                SpeedCap = MouseSpeedPolicy.Validate(p),
                SlowWeight = PropEx.GetInt(p, "speedSlowWeight", 20), NormalWeight = PropEx.GetInt(p, "speedNormalWeight", 50), FastWeight = PropEx.GetInt(p, "speedFastWeight", 30),
                CustomSpeedMin = PropEx.GetInt(p, "speedCustomMin", 300), CustomSpeedMax = PropEx.GetInt(p, "speedCustomMax", 530),
                SpeedMinPxPerSec = effectiveSpeedMin,
                SpeedMaxPxPerSec = effectiveSpeedMax,
                MoveTimeMinMs = mtMin,
                MoveTimeMaxMs = mtMax,
            };
        }
    }

    /// <summary>
    /// Run-scoped pause manager: counts completed moves and fires the long
    /// "distraction" break once per a fresh random cadence in [IdleEveryMin, IdleEveryMax].
    /// Lives on the RunEngine instance, so the cadence spans steps and repeat passes.
    /// </summary>
    public sealed class PausePlanner
    {
        private readonly Random _rng;
        private int _movesSinceIdle;
        private int _nextIdleAt = -1;

        public PausePlanner(Random rng) => _rng = rng;

        public int BeforeMoveMs(Config c) => Rand(_rng, c.PauseBeforeMinMs, c.PauseBeforeMaxMs);
        public int AfterMoveMs(Config c) => Rand(_rng, c.PauseAfterMinMs, c.PauseAfterMaxMs);

        /// <summary>One hesitation pause per move at most; null = no hesitation this move.</summary>
        public int? MidPauseMs(Config c)
            => c.MidPauseChancePct > 0 && _rng.Next(100) < c.MidPauseChancePct
                ? Rand(_rng, c.MidPauseMinMs, c.MidPauseMaxMs)
                : null;

        /// <summary>Call once per COMPLETED move; returns the long-break length when due (0 = not due).</summary>
        public int RollLongPauseMs(Config c)
        {
            if (c.IdlePauseMaxMs <= 0 || c.IdleEveryMaxMoves <= 0) return 0;   // feature off
            _movesSinceIdle++;
            if (_nextIdleAt < 0) _nextIdleAt = Math.Max(1, Rand(_rng, c.IdleEveryMinMoves, c.IdleEveryMaxMoves));
            if (_movesSinceIdle < _nextIdleAt) return 0;
            _movesSinceIdle = 0;
            _nextIdleAt = Math.Max(1, Rand(_rng, c.IdleEveryMinMoves, c.IdleEveryMaxMoves));
            return Rand(_rng, c.IdlePauseMinMs, c.IdlePauseMaxMs);
        }
    }

    /// <summary>Uniform int in [min, max] with swapped-endpoint tolerance; 0 when max ≤ 0.</summary>
    public static int Rand(Random rng, int min, int max)
    {
        if (max < min) (min, max) = (max, min);
        if (max <= 0) return 0;
        return max <= min ? min : rng.Next(min, max + 1);
    }

    /// <summary>
    /// Natural-v1 duration: keep the user's configured range as the medium-distance
    /// baseline, shorten close moves and lengthen long moves.  The scale glides from
    /// 0.72x at zero distance to 1.35x at 650px, then stays bounded.  This is applied
    /// once per movement; micro-step cadence remains untouched.
    /// </summary>
    public static int DistanceScaledMoveMs(int minMs, int maxMs, double distance, Random rng)
    {
        if (maxMs < minMs) (minMs, maxMs) = (maxMs, minMs);
        if (maxMs <= 0) return 0;
        int sampled = Rand(rng, Math.Max(1, minMs), Math.Max(1, maxMs));
        double scale = 0.72 + 0.63 * Math.Clamp(distance / 650.0, 0.0, 1.0);
        return (int)Math.Clamp(Math.Round(sampled * scale), 120, 30000);
    }

    /// <summary>
    /// Builds a complete move plan from (sx,sy) to (tx,ty): optional overshoot leg +
    /// correction leg, WindMouse waypoints with ease-in-out timing, a possible folded-in
    /// hesitation pause, and the before/after/long pauses. Target is clamped to the screen.
    /// </summary>
    public static Plan PlanMove(int sx, int sy, int tx, int ty, Config c, PausePlanner pauses,
                                Random rng, int screenW, int screenH)
    {
        tx = Math.Clamp(tx, 0, Math.Max(0, screenW - 1));
        ty = Math.Clamp(ty, 0, Math.Max(0, screenH - 1));
        sx = Math.Clamp(sx, 0, Math.Max(0, screenW - 1));
        sy = Math.Clamp(sy, 0, Math.Max(0, screenH - 1));

        var dense = new List<Waypoint>();
        var ctrl = new List<Waypoint>();
        double dist = Math.Sqrt((tx - sx) * (double)(tx - sx) + (ty - sy) * (double)(ty - sy));
        // Geometry only needs a nominal budget. Actual per-microstep timing is generated later
        // from a continuously-changing speed profile inside the configured min/max range.
        int speedLo = Math.Max(150, c.SpeedMinPxPerSec);
        int speedHi = Math.Max(speedLo, c.SpeedMaxPxPerSec);
        int totalMs = c.SpeedMaxPxPerSec <= 0 ? 0
            : (int)Math.Clamp(dist * 1000.0 / Math.Max(1.0, (speedLo + speedHi) / 2.0), 60, 30000);
        int curveMin = Math.Clamp(c.CurveMinPct, 0, 200);
        int curveMax = Math.Clamp(c.CurveMaxPct, 0, 200);
        if (curveMax < curveMin) (curveMin, curveMax) = (curveMax, curveMin);
        int sampledCurvePct = curveMax == curveMin ? curveMin : rng.Next(curveMin, curveMax + 1);
        double curve = sampledCurvePct / 100.0;
        int moveTimeMin = Math.Max(0, c.MoveTimeMinMs);
        int moveTimeMax = Math.Max(0, c.MoveTimeMaxMs);
        if (moveTimeMax < moveTimeMin) (moveTimeMin, moveTimeMax) = (moveTimeMax, moveTimeMin);
        // v0.9.10 — per-step duration range: a FRESH target time is drawn for every move, then
        // the continuously-changing speed profile is normalized to land on it (0/0 = speed-driven).
        int targetMoveMs = moveTimeMax > 0
            ? DistanceScaledMoveMs(moveTimeMin, moveTimeMax, dist, rng)
            : 0;
        bool overshot = false;
        int overshootDenseIndex = -1, overshootCtrlIndex = -1;

        // v0.9.5 — clamp curve to [0, 2] so callers with higher values still behave predictably.
        // 0..1  = bow intensity (same feel as v0.9.4), 1..2 = arc mode (visible semi-circle sweep).
        curve = Math.Clamp(curve, 0.0, 2.0);

        // v0.9.5 — overshoot and lateral error scale with arc strength.
        // In arc mode (curve > 1) we deliberately over-aim more so the correction leg adds
        // visible curvature rather than just a tiny nudge.

        // v0.9.2 — dense human stream per leg (BuildLeg) for the send_path bridge op, plus the
        // few control points (v0.9.1 style) that the fallback path uses on an old bridge.py.
        static int ControlCount(double d, Random rng)
            => d < 120 ? 1 : d < 400 ? 2 + rng.Next(2) : 3 + rng.Next(3);

        // v0.9.7 — TRUE ARC MODE. The v0.9.5 implementation had two independent conditionals:
        // it first appended the two arc legs and then the `else` belonging to the overshoot
        // conditional appended a second direct path from (sx,sy). Once the arc reached the target,
        // that second path's first waypoint jumped the cursor all the way back near the start.
        //
        // Arc mode is now one mutually-exclusive branch and uses a continuous parametric
        // half-ellipse rather than two almost-straight legs meeting at a bulge point. Above 100,
        // v0.9.8 — for true arcs the curve does NOT stay at sampledCurvePct. BuildArcPath creates
        // a fresh low/high knot profile and smoothly changes curvature THROUGHOUT this movement
        // while remaining inside curveMin..curveMax. sampledCurvePct is used by the <=100 subtle
        // WindMouse branch and retained in the Plan for diagnostics.
        bool arc = false;
        int arcHeightPx = 0;
        if (curveMax > 100 && dist >= 80)
        {
            arc = true;
            var arcDense = BuildArcPath(sx, sy, tx, ty, totalMs, curveMin, curveMax, rng,
                                        screenW, screenH, out var actualHeight,
                                        out var arcMs, out var arcLength);
            arcHeightPx = (int)Math.Round(actualHeight);
            dense.AddRange(arcDense);

            // Old-bridge fallback follows the SAME arc instead of inventing a straight path.
            int arcControls = Math.Clamp((int)Math.Ceiling(arcLength / 140.0), 4, 12);
            var ac = ToControlPoints(arcDense, arcControls);
            AssignDelays(ac, arcMs, rng);
            ctrl.AddRange(ac);
        }
        // Overshoot & correct: aim PAST the target along the approach line (plus a little
        // perpendicular error), pause briefly like a human re-aiming, then correct back.
        // Arc mode and overshoot are mutually exclusive — pick one curvature strategy per move.
        else if (dist >= 300 && c.OvershootChancePct > 0 && rng.Next(100) < c.OvershootChancePct)
        {
            overshot = true;
            double ux = (tx - sx) / dist, uy = (ty - sy) / dist;
            // Natural-v1: a rare, small miss followed by one correction.  Large
            // percentage-based overshoots looked synthetic on long screen moves.
            int over = Rand(rng, 2, 6);
            int perp = Rand(rng, -1, 1);
            int ox = Math.Clamp(tx + (int)(ux * over - uy * perp), 0, Math.Max(0, screenW - 1));
            int oy = Math.Clamp(ty + (int)(uy * over + ux * perp), 0, Math.Max(0, screenH - 1));
            int leg1Ms = Math.Max(40, (int)(totalMs * 0.8));
            int leg2Ms = Math.Max(30, totalMs - leg1Ms);

            var dense1 = BuildLeg(sx, sy, ox, oy, leg1Ms, rng, curve, curveMin, curveMax);
            dense.AddRange(dense1);
            overshootDenseIndex = dense.Count - 1; // hesitation is applied after dynamic timing
            var dense2 = BuildLeg(ox, oy, tx, ty, leg2Ms, rng, curve, curveMin, curveMax);
            dense.AddRange(dense2);

            var c1 = ToControlPoints(dense1, ControlCount(dist, rng));
            AssignDelays(c1, leg1Ms, rng);
            ctrl.AddRange(c1);
            overshootCtrlIndex = ctrl.Count - 1;
            var c2 = ToControlPoints(dense2, 1);   // correction: one short motion
            AssignDelays(c2, leg2Ms, rng);
            ctrl.AddRange(c2);
        }
        else
        {
            var direct = BuildLeg(sx, sy, tx, ty, totalMs, rng, curve, curveMin, curveMax);
            dense.AddRange(direct);
            var cc = ToControlPoints(direct, ControlCount(dist, rng));
            AssignDelays(cc, totalMs, rng);
            ctrl.AddRange(cc);
        }

        // Keep every point on-screen: the wind term can push points a few px past the edges.
        ClampAll(dense, screenW, screenH);
        ClampAll(ctrl, screenW, screenH);

        // v0.9.8 — speed is a CONTINUOUS profile, not one random speed for the whole move.
        // It alternates smoothly between low/high portions of [SpeedMin, SpeedMax].
        string selectedSpeedMode = "legacy";
        int effectiveMin = c.SpeedMinPxPerSec, effectiveMax = c.SpeedMaxPxPerSec;
        if (c.SpeedMode != "legacy")
        {
            (effectiveMin, effectiveMax, selectedSpeedMode) = MouseSpeedPolicy.Select(c.SpeedMode, effectiveMin, c.SpeedCap, c.SlowWeight, c.NormalWeight, c.FastWeight, c.CustomSpeedMin, c.CustomSpeedMax, rng);
            targetMoveMs = 0; // explicit speed policy supersedes legacy duration overrides
        }
        AssignDynamicStreamDelays(dense, sx, sy, effectiveMin, effectiveMax, rng, targetMoveMs);
        if (c.SpeedCap > 0)
        {
            int px = sx, py = sy;
            for (int i = 0; i < dense.Count; i++) {
                var wp = dense[i]; double length = Math.Sqrt((wp.X-px)*(double)(wp.X-px) + (wp.Y-py)*(double)(wp.Y-py));
                dense[i] = wp with { DelayMs = Math.Max(wp.DelayMs, (int)Math.Ceiling(length * 1000 / c.SpeedCap)) };
                px = wp.X; py = wp.Y;
            }
        }
        int dynamicTotalMs = dense.Sum(p => p.DelayMs);
        for (int i = 0; i < ctrl.Count; i++) ctrl[i] = ctrl[i] with { DelayMs = 0 };
        AssignDelays(ctrl, dynamicTotalMs, rng); // old-bridge fallback keeps equivalent total time
        if (overshootDenseIndex >= 0)
        {
            int reAimMs = Rand(rng, 70, 160);
            dense[overshootDenseIndex] = dense[overshootDenseIndex] with
                { DelayMs = dense[overshootDenseIndex].DelayMs + reAimMs };
            if (overshootCtrlIndex >= 0)
                ctrl[overshootCtrlIndex] = ctrl[overshootCtrlIndex] with
                    { DelayMs = ctrl[overshootCtrlIndex].DelayMs + reAimMs };
        }

        // Hesitation: fold ONE mid-path pause into a random interior point of BOTH lists.
        // Hesitation is rare and meaningful only on a long movement.  Short-move
        // pauses resemble transport stalls rather than hand behaviour.
        var mid = dist >= 300 ? pauses.MidPauseMs(c) : null;
        if (mid is > 0)
        {
            if (dense.Count >= 8)
            {
                int idx = 2 + rng.Next(dense.Count - 4);   // never the first/last points
                dense[idx] = dense[idx] with { DelayMs = dense[idx].DelayMs + mid.Value };
            }
            if (ctrl.Count >= 3)
            {
                int ci = 1 + rng.Next(ctrl.Count - 2);
                ctrl[ci] = ctrl[ci] with { DelayMs = ctrl[ci].DelayMs + mid.Value };
            }
        }

        return new Plan
        {
            BeforeMs = pauses.BeforeMoveMs(c),
            Waypoints = dense,
            ControlPoints = ctrl,
            AfterMs = pauses.AfterMoveMs(c),
            LongPauseMs = pauses.RollLongPauseMs(c),
            Overshot = overshot,
            Arced = arc,
            ArcHeightPx = arcHeightPx,
            SampledCurvePct = sampledCurvePct,
            CurveMinPct = curveMin,
            CurveMaxPct = curveMax,
            SelectedSpeedMode = selectedSpeedMode, SpeedCap = c.SpeedCap,
            TargetMoveMs = targetMoveMs,
        };
    }

    /// <summary>
    /// v0.9.7 — Builds one continuous irregular half-ellipse from start to target.
    /// curveMinPct..curveMaxPct is sampled repeatedly through a smooth alternating knot profile;
    /// therefore curvature changes during one movement, not merely once between movements.
    /// Values 101..200 map to roughly 8..50% of chord length as perpendicular height;
    /// sqrt mapping makes 120 visibly curved while 200 approaches a true semicircle.
    /// The side with usable screen room is chosen and the whole arc is scaled before generation,
    /// so points remain on-screen without flat sections caused by per-point clamping.
    /// </summary>
    public static List<Waypoint> BuildArcPath(int sx, int sy, int tx, int ty, int totalMs,
                                               int curveMinPct, int curveMaxPct, Random rng,
                                               int screenW, int screenH,
                                               out double actualHeight, out int arcMs,
                                               out double pathLength)
    {
        double dx = tx - sx, dy = ty - sy;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < 3)
        {
            actualHeight = 0;
            arcMs = totalMs;
            pathLength = dist;
            return new List<Waypoint> { new(tx, ty, Math.Max(0, totalMs)) };
        }

        curveMinPct = Math.Clamp(curveMinPct, 0, 200);
        curveMaxPct = Math.Clamp(curveMaxPct, 0, 200);
        if (curveMaxPct < curveMinPct) (curveMinPct, curveMaxPct) = (curveMaxPct, curveMinPct);
        double ux = dx / dist, uy = dy / dist;
        double nx = -uy, ny = ux;
        double skew = rng.NextDouble() * 0.14 - 0.07;          // apex slightly before/after middle
        double wobble = 0.025 + rng.NextDouble() * 0.045;      // smooth ±2.5–7% shape variation
        double phase = rng.NextDouble() * Math.PI * 2.0;
        int samples = Math.Clamp((int)Math.Ceiling(dist / 12.0), 32, 240);
        int knotCount = Math.Clamp(4 + (int)(dist / 320.0), 4, 8);
        var curveKnots = BuildCurveProfile(curveMinPct, curveMaxPct, knotCount, rng);

        double FitForSide(int side)
        {
            double fit = 1.0;
            for (int i = 1; i < samples; i++)
            {
                double t = i / (double)samples;
                double q = Math.Clamp(t + skew * Math.Sin(Math.PI * t), 0.0, 1.0);
                double along = 0.5 - 0.5 * Math.Cos(Math.PI * q);
                double shape = Math.Sin(Math.PI * q) *
                               (1.0 + wobble * Math.Sin(2.0 * Math.PI * t + phase));
                double bx = sx + ux * dist * along;
                double by = sy + uy * dist * along;
                double localCurve = SampleCurveProfile(curveKnots, t);
                double required = dist * CurveHeightRatio(localCurve) * Math.Max(0.0, shape);
                if (required < 1e-6) continue;
                double room = RayToScreenEdge(bx, by, nx * side, ny * side,
                                              screenW, screenH, margin: 2.0);
                fit = Math.Min(fit, room / required);
            }
            return Math.Clamp(fit * 0.94, 0.0, 1.0); // small safety margin before integer rounding
        }

        double fitPos = FitForSide(+1), fitNeg = FitForSide(-1);
        int chosenSide;
        double fitChosen;
        if (fitPos >= 0.90 && fitNeg >= 0.90)
        {
            chosenSide = rng.Next(2) == 0 ? -1 : +1;
            fitChosen = chosenSide > 0 ? fitPos : fitNeg;
        }
        else if (fitPos >= fitNeg) { chosenSide = +1; fitChosen = fitPos; }
        else { chosenSide = -1; fitChosen = fitNeg; }

        actualHeight = 0;
        var spine = new List<Waypoint>(samples + 1) { new(sx, sy, 0) };
        for (int i = 1; i < samples; i++)
        {
            double t = i / (double)samples;
            double q = Math.Clamp(t + skew * Math.Sin(Math.PI * t), 0.0, 1.0);
            double along = 0.5 - 0.5 * Math.Cos(Math.PI * q);
            double shape = Math.Sin(Math.PI * q) *
                           (1.0 + wobble * Math.Sin(2.0 * Math.PI * t + phase));
            double localCurve = SampleCurveProfile(curveKnots, t);
            double localHeight = dist * CurveHeightRatio(localCurve) * fitChosen;
            double normal = chosenSide * localHeight * Math.Max(0.0, shape);
            actualHeight = Math.Max(actualHeight, Math.Abs(normal));
            int x = (int)Math.Round(sx + ux * dist * along + nx * normal);
            int y = (int)Math.Round(sy + uy * dist * along + ny * normal);
            spine.Add(new Waypoint(x, y, 0));
        }
        spine.Add(new Waypoint(tx, ty, 0));

        pathLength = PolylineLength(spine);
        arcMs = totalMs <= 0
            ? 0
            : (int)Math.Clamp(Math.Round(totalMs * Math.Max(1.0, pathLength / dist)), 60, 30000);
        var dense = ResampleByArcVariable(spine, 2.0, 3.2, rng);
        AssignStreamDelays(dense, arcMs, rng); // overwritten by dynamic speed in PlanMove
        return dense;
    }

    /// <summary>v0.9.8 — fresh smooth curvature profile for one movement. Knots alternate
    /// between the lower and upper portions of the requested range; smoothstep interpolation
    /// eliminates corners/jerk while guaranteeing every value remains inside the range.</summary>
    public static double[] BuildCurveProfile(int minPct, int maxPct, int knotCount, Random rng)
        => BuildRangeProfile(minPct, maxPct, knotCount, rng);

    /// <summary>Generic smooth min/max profile shared by curvature, speed and spacing.
    /// A fresh alternating low/high sequence is created every movement.</summary>
    public static double[] BuildRangeProfile(double min, double max, int knotCount, Random rng,
                                              bool lowAtBothEnds = false)
    {
        if (max < min) (min, max) = (max, min);
        knotCount = Math.Clamp(knotCount, 2, 12);
        var knots = new double[knotCount];
        if (Math.Abs(max - min) < 1e-9)
        {
            Array.Fill(knots, min);
            return knots;
        }

        double span = max - min;
        bool upper = rng.Next(2) == 1;
        for (int i = 0; i < knots.Length; i++)
        {
            bool forceLow = lowAtBothEnds && (i == 0 || i == knots.Length - 1);
            double band = forceLow ? rng.NextDouble() * 0.18
                : upper ? 0.60 + rng.NextDouble() * 0.40
                : rng.NextDouble() * 0.40;
            knots[i] = min + span * band;
            upper = !upper;
        }
        return knots;
    }

    public static double SampleCurveProfile(IReadOnlyList<double> knots, double t)
    {
        if (knots.Count == 0) return 0;
        if (knots.Count == 1) return knots[0];
        double p = Math.Clamp(t, 0.0, 1.0) * (knots.Count - 1);
        int i = Math.Min(knots.Count - 2, (int)Math.Floor(p));
        double u = p - i;
        double smooth = u * u * (3.0 - 2.0 * u); // C1 smoothstep
        return knots[i] + (knots[i + 1] - knots[i]) * smooth;
    }

    /// <summary>v0.9.8 — variable-distance resampling. Micro-step spacing itself now glides
    /// through 2.0..3.2px instead of choosing one fixed spacing for the whole movement.</summary>
    public static List<Waypoint> ResampleByArcVariable(List<Waypoint> spine, double minSpacing,
                                                        double maxSpacing, Random rng)
    {
        var outp = new List<Waypoint>();
        if (spine.Count == 0) return outp;
        if (spine.Count == 1) { outp.Add(spine[0]); return outp; }
        if (maxSpacing < minSpacing) (minSpacing, maxSpacing) = (maxSpacing, minSpacing);
        minSpacing = Math.Max(0.5, minSpacing);
        maxSpacing = Math.Max(minSpacing, maxSpacing);
        double total = PolylineLength(spine);
        if (total < 1e-6) { outp.Add(spine[^1]); return outp; }
        var profile = BuildRangeProfile(minSpacing, maxSpacing, 5, rng);
        double nextAt = SampleCurveProfile(profile, 0), acc = 0;
        for (int i = 1; i < spine.Count; i++)
        {
            double x0 = spine[i - 1].X, y0 = spine[i - 1].Y;
            double seg = Math.Sqrt((spine[i].X - x0) * (spine[i].X - x0) +
                                   (spine[i].Y - y0) * (spine[i].Y - y0));
            if (seg < 1e-6) continue;
            while (acc + seg >= nextAt)
            {
                double u = (nextAt - acc) / seg;
                outp.Add(new Waypoint(
                    (int)Math.Round(x0 + (spine[i].X - x0) * u),
                    (int)Math.Round(y0 + (spine[i].Y - y0) * u), 0));
                double phase = Math.Clamp(nextAt / total, 0.0, 1.0);
                nextAt += Math.Max(0.5, SampleCurveProfile(profile, phase));
            }
            acc += seg;
        }
        var last = spine[^1];
        if (outp.Count == 0 || outp[^1].X != last.X || outp[^1].Y != last.Y)
            outp.Add(new Waypoint(last.X, last.Y, 0));
        return outp;
    }

    /// <summary>v0.9.8 — derives each micro-step delay from a smooth speed profile that stays
    /// inside the configured range. First/last knots are low for natural acceleration/deceleration;
    /// interior knots alternate low/high, so speed keeps changing throughout the move.
    /// v0.9.10 — optional targetTotalMs (per-step duration range) normalizes the same weights so
    /// the whole move lands on the freshly sampled duration; when app speed is disabled (max 0)
    /// a nominal 150–2300 px/s shape is used for pacing only. Floor: 1 ms per micro-step, so a
    /// target below the point count bottoms out at ~1ms × points.</summary>
    public static void AssignDynamicStreamDelays(List<Waypoint> pts, int sx, int sy,
                                                  int speedMin, int speedMax, Random rng,
                                                  int targetTotalMs = 0)
    {
        for (int i = 0; i < pts.Count; i++) pts[i] = pts[i] with { DelayMs = 0 };
        if (pts.Count == 0) return;
        if (speedMax <= 0 && targetTotalMs <= 0) return;
        bool shapeOnly = speedMax <= 0;
        int lo = shapeOnly ? 150 : Math.Max(150, Math.Min(speedMin, speedMax));
        int hi = shapeOnly ? 2000 : Math.Max(lo, Math.Max(speedMin, speedMax));   // v0.9.24 — shape-only fallback aligned
        int knotCount = Math.Clamp(4 + pts.Count / 120, 4, 9);
        var speedProfile = BuildRangeProfile(lo, hi, knotCount, rng, lowAtBothEnds: true);
        // Move the speed peak slightly earlier or later on every movement.  The
        // remap is monotonic and keeps endpoints exact; only the velocity shape changes.
        double phaseSkew = rng.NextDouble() * 0.44 - 0.22;

        double pathLength = 0; int px = sx, py = sy;
        var segLengths = new double[pts.Count];
        for (int i = 0; i < pts.Count; i++)
        {
            double seg = Math.Sqrt((pts[i].X - px) * (double)(pts[i].X - px) +
                                   (pts[i].Y - py) * (double)(pts[i].Y - py));
            segLengths[i] = seg; pathLength += seg; px = pts[i].X; py = pts[i].Y;
        }
        if (pathLength < 1e-6) return;

        var weights = new double[pts.Count];
        double weightSum = 0, travelled = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            double phase = Math.Clamp((travelled + segLengths[i] * 0.5) / pathLength, 0.0, 1.0);
            phase = Math.Clamp(phase + phaseSkew * phase * (1.0 - phase), 0.0, 1.0);
            double speed = Math.Clamp(SampleCurveProfile(speedProfile, phase), lo, hi);
            double w = segLengths[i] * 1000.0 / Math.Max(1.0, speed);
            weights[i] = w; weightSum += w;
            travelled += segLengths[i];
        }
        if (weightSum <= 0) return;
        double scale = targetTotalMs > 0 ? targetTotalMs / weightSum : 1.0;
        for (int i = 0; i < pts.Count; i++)
            pts[i] = pts[i] with { DelayMs = Math.Max(1, (int)Math.Round(weights[i] * scale)) };
    }

    private static double CurveHeightRatio(double curvePct)
    {
        double p = Math.Clamp(curvePct, 0.0, 200.0);
        if (p <= 100.0)
            return 0.08 * Math.Pow(p / 100.0, 1.15); // 0..8%: subtle bow range
        return 0.08 + 0.42 * Math.Sqrt((p - 100.0) / 100.0); // 8..50%: true arc range
    }

    private static double PolylineLength(IReadOnlyList<Waypoint> pts)
    {
        double length = 0;
        for (int i = 1; i < pts.Count; i++)
            length += Math.Sqrt((pts[i].X - pts[i - 1].X) * (double)(pts[i].X - pts[i - 1].X) +
                                (pts[i].Y - pts[i - 1].Y) * (double)(pts[i].Y - pts[i - 1].Y));
        return length;
    }

    private static double RayToScreenEdge(double x, double y, double dx, double dy,
                                           int screenW, int screenH, double margin)
    {
        double maxX = Math.Max(margin, screenW - 1.0 - margin);
        double maxY = Math.Max(margin, screenH - 1.0 - margin);
        double limit = double.PositiveInfinity;
        if (Math.Abs(dx) > 1e-9)
            limit = Math.Min(limit, dx > 0 ? (maxX - x) / dx : (margin - x) / dx);
        if (Math.Abs(dy) > 1e-9)
            limit = Math.Min(limit, dy > 0 ? (maxY - y) / dy : (margin - y) / dy);
        return double.IsFinite(limit) ? Math.Max(0.0, limit) : 0.0;
    }

    private static void ClampAll(List<Waypoint> pts, int screenW, int screenH)
    {
        for (int i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            int cxp = Math.Clamp(p.X, 0, Math.Max(0, screenW - 1));
            int cyp = Math.Clamp(p.Y, 0, Math.Max(0, screenH - 1));
            if (cxp != p.X || cyp != p.Y) pts[i] = p with { X = cxp, Y = cyp };
        }
    }

    /// <summary>
    /// v0.9.5 — tuned wind strength for one leg.
    ///   • curve < 0 (auto): default 0.30 (subtle human bow)
    ///   • 0.0 .. 1.0      : gradual bow  (wind ramps from 0.3× to 3.3× base)
    ///   • 1.0 .. 2.0      : arc mode     (wind ramps from 3.3× to 8.0×, plus perpendicular bias)
    /// Short moves still stay tighter — humans don't swing wildly on 20px steps.
    /// </summary>
    private static double TunedWind(double dist, double curve)
    {
        double cv = curve < 0 ? 0.30 : Math.Clamp(curve, 0, 2);
        // Base wind: 0.3× at cv=0 → 3.3× at cv=1 → 8.0× at cv=2
        double baseWind = cv < 1.0
            ? 0.3 + cv * 3.0                          // linear ramp 0.3 → 3.3
            : 3.3 + (cv - 1.0) * 4.7;                 // linear ramp 3.3 → 8.0
        return baseWind * Math.Clamp(dist / 400.0, 0.35, 1.0);
    }

    /// <summary>
    /// v0.9.2 — one movement leg as a dense human stream: the WindMouse spine resampled by arc
    /// length to ~3 px micro-steps (the recorded hand median), timed with a fluctuating velocity
    /// (ease-in-out base × 1–3 random submovement bumps × per-point jitter) at a ~5 ms median
    /// cadence — the stats measured from the user's real recording.
    /// </summary>
    public static List<Waypoint> BuildLeg(int sx, int sy, int tx, int ty, int totalMs, Random rng,
                                          double curve = -1, int curveMinPct = -1,
                                          int curveMaxPct = -1)
    {
        double dist0 = Math.Max(1.0, Math.Sqrt((tx - sx) * (double)(tx - sx) + (ty - sy) * (double)(ty - sy)));
        IReadOnlyList<double>? profile = null;
        if (curveMinPct >= 0 && curveMaxPct >= 0)
        {
            int knots = Math.Clamp(4 + (int)(dist0 / 320.0), 4, 8);
            profile = BuildCurveProfile(curveMinPct, curveMaxPct, knots, rng);
        }
        var spine = WindMouse(sx, sy, tx, ty, rng, TunedWind(dist0, curve), 14.0, curve, profile);
        var withStart = new List<Waypoint>(spine.Count + 1) { new(sx, sy, 0) };
        withStart.AddRange(spine);
        var pts = ResampleByArcVariable(withStart, 2.0, 3.2, rng);
        AssignStreamDelays(pts, totalMs, rng);
        return pts;
    }

    /// <summary>Walks the polyline at fixed arc-length spacing; always ends exactly on the target.</summary>
    public static List<Waypoint> ResampleByArc(List<Waypoint> spine, double spacing)
    {
        var outp = new List<Waypoint>();
        if (spine.Count == 0) return outp;
        if (spine.Count == 1) { outp.Add(spine[0]); return outp; }
        double nextAt = spacing, acc = 0;
        for (int i = 1; i < spine.Count; i++)
        {
            double x0 = spine[i - 1].X, y0 = spine[i - 1].Y;
            double seg = Math.Sqrt((spine[i].X - x0) * (spine[i].X - x0) + (spine[i].Y - y0) * (spine[i].Y - y0));
            if (seg < 1e-6) continue;
            while (acc + seg >= nextAt)
            {
                double t = (nextAt - acc) / seg;
                outp.Add(new Waypoint(
                    (int)Math.Round(x0 + (spine[i].X - x0) * t),
                    (int)Math.Round(y0 + (spine[i].Y - y0) * t), 0));
                nextAt += spacing;
            }
            acc += seg;
        }
        var lastP = spine[^1];
        if (outp.Count == 0 || outp[^1].X != lastP.X || outp[^1].Y != lastP.Y)
            outp.Add(new Waypoint(lastP.X, lastP.Y, 0));
        return outp;
    }

    /// <summary>
    /// v0.9.2 — fluctuating human velocity: delay per micro-step ∝ 1/speed(t), where speed(t) =
    /// ease-in-out base (0.35 + 1.1·sin(π·t^0.9)) × 1–3 Gaussian submovement bumps × ±25% jitter;
    /// normalized so the delays sum to totalMs. totalMs ≤ 0 (speed disabled) leaves all 0.
    /// </summary>
    public static void AssignStreamDelays(List<Waypoint> pts, int totalMs, Random rng)
    {
        int n = pts.Count;
        if (n == 0 || totalMs <= 0) return;
        int bumps = 1 + rng.Next(3);
        var bc = new double[bumps]; var bs = new double[bumps]; var ba = new double[bumps];
        for (int b = 0; b < bumps; b++)
        {
            bc[b] = 0.1 + rng.NextDouble() * 0.8;    // bump center (phase)
            bs[b] = 0.06 + rng.NextDouble() * 0.14;  // bump width
            ba[b] = 0.5 + rng.NextDouble() * 1.1;    // bump strength
        }
        var w = new double[n];
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            double t = (i + 0.5) / n;
            double speed = 0.35 + 1.1 * Math.Sin(Math.PI * Math.Pow(t, 0.9));   // slow ends, fast cruise
            double bump = 0;
            for (int b = 0; b < bumps; b++) { double z = (t - bc[b]) / bs[b]; bump += ba[b] * Math.Exp(-0.5 * z * z); }
            double dw = 1.0 / (speed * (1.0 + bump)) * (0.8 + rng.NextDouble() * 0.5);
            w[i] = dw; sum += dw;
        }
        int assigned = 0;
        for (int i = 0; i < n; i++)
        {
            int d = i == n - 1 ? Math.Max(1, totalMs - assigned) : Math.Max(1, (int)Math.Round(totalMs * w[i] / sum));
            assigned += d;
            pts[i] = pts[i] with { DelayMs = d };
        }
    }

    /// <summary>
    /// Total movement time for a distance, from the app's px/s speed range.
    /// v0.9.0 fix: the sampled speed is floored at 150 px/s — the old [0..2300] draw
    /// could land near 0 px/s and stall the script for tens of seconds on one move.
    /// </summary>
    public static int MoveTimeMs(double dist, Config c, Random rng)
    {
        if (c.SpeedMaxPxPerSec <= 0) return 0;   // timing disabled → firmware-paced move
        int lo = Math.Max(150, c.SpeedMinPxPerSec);
        int hi = Math.Max(lo + 1, c.SpeedMaxPxPerSec);
        int speed = rng.Next(lo, hi + 1);
        double ms = dist * 1000.0 / speed;
        ms *= 0.85 + rng.NextDouble() * 0.3;     // ±15% energy jitter per move
        return (int)Math.Clamp(ms, 60, 30000);   // safety rails: never <60ms, never >30s
    }

    /// <summary>
    /// WindMouse (SRL-style): gravity pulls the point at the target, a random "wind"
    /// perturbs it, step size adapts to the remaining distance. Produces a dense,
    /// never-straight, human-looking trail that always ends exactly on the target.
    ///
    /// v0.9.8 — WindMouse handles the subtle-bow range with continuously modulated wind.
    /// Strong Arc Mode is generated separately by BuildArcPath with a dynamic curve profile.
    /// </summary>
    public static List<Waypoint> WindMouse(int sx, int sy, int tx, int ty, Random rng,
                                           double wind = 3.0, double gravity = 9.0,
                                           double curve = -1,
                                           IReadOnlyList<double>? curveProfilePct = null)
    {
        var pts = new List<Waypoint>();
        double sqrt3 = Math.Sqrt(3), sqrt5 = Math.Sqrt(5);
        double x = sx, y = sy, vX = 0, vY = 0, wX = 0, wY = 0;
        double dist0 = Math.Max(1.0, Math.Sqrt((tx - sx) * (double)(tx - sx) + (ty - sy) * (double)(ty - sy)));
        if (dist0 < 3) { pts.Add(new Waypoint(tx, ty, 0)); return pts; }

        // v0.9.4 — wind/gravity are parameters now; tuned values (gravity 14) come from
        // TunedWind via BuildLeg/PlanMove. Defaults keep the original behavior for direct tests.
        double maxStep = Math.Clamp(dist0 / 15.0, 6, 30);
        double stopRadius = Math.Min(8.0, Math.Max(2.0, dist0 * 0.02));

        int guard = 0;
        while (guard++ < 2000)
        {
            double dist = Math.Sqrt((tx - x) * (tx - x) + (ty - y) * (ty - y));
            if (dist < stopRadius) break;

            double liveWind = wind;
            if (curveProfilePct is { Count: > 0 })
            {
                double progress = Math.Clamp(1.0 - dist / dist0, 0.0, 1.0);
                double liveCurve = SampleCurveProfile(curveProfilePct, progress) / 100.0;
                liveWind = TunedWind(dist0, liveCurve);
            }
            double wMag = Math.Min(liveWind, dist);
            if (dist >= stopRadius * 4)   // far: wind roams freely; near: wind calms down
            {
                wX = wX / sqrt3 + (2 * rng.NextDouble() - 1) * wMag / sqrt5;
                wY = wY / sqrt3 + (2 * rng.NextDouble() - 1) * wMag / sqrt5;
            }
            else
            {
                wX /= sqrt3; wY /= sqrt3;
                maxStep = Math.Max(2.5, maxStep / sqrt5);   // decelerate on approach
            }

            vX += wX + gravity * (tx - x) / dist;
            vY += wY + gravity * (ty - y) / dist;
            double vMag = Math.Sqrt(vX * vX + vY * vY);
            if (vMag > maxStep) { vX = vX / vMag * maxStep; vY = vY / vMag * maxStep; }

            x += vX; y += vY;
            pts.Add(new Waypoint((int)Math.Round(x), (int)Math.Round(y), 0));
        }
        pts.Add(new Waypoint(tx, ty, 0));   // land exactly on the target
        return pts;
    }

    /// <summary>
    /// v0.9.1 — samples the dense WindMouse trail down to `count` evenly-spaced control
    /// points (always ending exactly on the target). These few points are sent with
    /// firmware smoothstepping (human=1) instead of dozens of raw jumps.
    /// </summary>
    public static List<Waypoint> ToControlPoints(List<Waypoint> dense, int count)
    {
        if (dense.Count == 0) return dense;
        count = Math.Clamp(count, 1, dense.Count);
        if (count == dense.Count) return dense;
        var ctrl = new List<Waypoint>(count);
        for (int k = 0; k < count; k++)
        {
            int idx = (int)Math.Round((k + 1) * (dense.Count - 1) / (double)count);
            ctrl.Add(dense[Math.Clamp(idx, 0, dense.Count - 1)]);
        }
        return ctrl;
    }

    /// <summary>
    /// Ease-in-out timing: waypoint delays follow a bell weight (slow at both ends,
    /// ~4× faster mid-path) scaled so the delays sum to ≈ totalMs, plus ±25% jitter.
    /// totalMs ≤ 0 (speed disabled) leaves all delays at 0 — the board runs firmware-paced.
    /// </summary>
    public static void AssignDelays(List<Waypoint> pts, int totalMs, Random rng)
    {
        if (pts.Count == 0) return;
        if (totalMs <= 0) return;   // delays already 0

        int n = pts.Count;
        var w = new double[n];
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            double t = (i + 0.5) / n;
            double edge = (2 * t - 1) * (2 * t - 1);        // 1 at ends, 0 mid
            w[i] = 0.3 + 2.1 * edge;                        // ends ~8× slower than mid
            sum += w[i];
        }
        int assigned = 0;
        for (int i = 0; i < n; i++)
        {
            double share = totalMs * w[i] / sum;
            share *= 0.75 + rng.NextDouble() * 0.5;         // per-point jitter
            int d = i == n - 1 ? Math.Max(1, totalMs - assigned) : Math.Max(1, (int)share);
            assigned += d;
            pts[i] = pts[i] with { DelayMs = d };
        }
    }
}
