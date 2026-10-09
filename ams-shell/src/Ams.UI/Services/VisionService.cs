using System.Buffers;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Forms;

namespace Ams.UI.Services;

/// <summary>
/// PC-side image search for the Find Image step (design doc §3.3: detection is 100%
/// PC-side; only the resulting coordinates go to the board).
/// Screen capture via GDI (CopyFromScreen), template match via normalized
/// cross-correlation on grayscale with integral-image window statistics
/// (adaptive coarse step, then refine). v0.9.18: colour-aware — luminance-weighted gray for
/// the NCC stage plus a per-channel colour gate, a distinctiveness gate, and a non-linear
/// similarity->threshold mapping. See docs/image-search-accuracy-v0.9.18.md.
/// v0.9.19: FOREGROUND-weighted colour gate — the v0.9.18 whole-template average let a
/// small wrong-coloured glyph hide inside a big matching background (colour looked
/// "undetected"); the duplicate rescue now verifies the runner-up's colour too, and
/// sliver templates (thinner than 6 px) are rejected. See docs/image-search-color-gate-v0.9.19.md.
///
/// v0.7.9 — RESTORED the v0.7.6 Warden-hardening (9 actions; docs/image-search-hardening.md)
/// that was lost in the v0.7.8 wholesale replace:
///  1 cadence jitter 80–160ms (no fixed 120ms FFT pattern) · 2 stochastic template skip ~30% ·
///  3 region jitter ±5px · 4 adaptive polling on static screens · 5 ArrayPool byte buffers (LOH) ·
///  6 human-like click delay (caller side — RunEngine) · 7 GDI handle health guard ·
///  8 stable SHA-256 template IDs in the log · 9 nested try-finally GDI-safe dispose.
/// </summary>
public static class VisionService
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    private static readonly Random Rng = new();
    private static readonly ArrayPool<byte> BytePool = ArrayPool<byte>.Shared;   // action 5

    // action 7 — GDI handle health guard (Warden can count our GDI objects)
    private const uint GR_GDIOBJECTS = 0;
    private const int GdiObjectLimit = 4000;   // Windows default cap is ~10,000
    [DllImport("gdi32.dll")] private static extern int GetGuiResources(IntPtr hProcess, uint uiFlags);

    /// <summary>AMS app-window bounds as a nullable System.Drawing.Rectangle;
    /// null means "query hasn't been performed yet". Empty means "app window not found / covers full screen — skip search".</summary>
    private static System.Drawing.Rectangle? _amsAppBounds;
    private static readonly object _boundsLock = new();
    private static System.Drawing.Rectangle? GetAmsAppBounds()
    {
        if (_amsAppBounds.HasValue) return _amsAppBounds;
        lock (_boundsLock)
        {
            if (_amsAppBounds.HasValue) return _amsAppBounds;
            // Use GetWindowRect via PInvoke instead of WPF Window.Left/Top/Width/Height,
            // because WPF's properties exclude the non-client area (title bar + borders)
            // while GetWindowRect returns the true screen rectangle including those.
            var mw = System.Windows.Application.Current?.MainWindow;
            if (mw is null) { _amsAppBounds = System.Drawing.Rectangle.Empty; return _amsAppBounds; }
            if (!GetWindowRect(new System.Windows.Interop.WindowInteropHelper(mw).Handle, out RECT r))
            {
                // Fallback to WPF properties if PInvoke fails (rare).
                _amsAppBounds = new System.Drawing.Rectangle(
                    (int)mw.Left, (int)mw.Top,
                    Math.Max(1, (int)mw.Width),
                    Math.Max(1, (int)mw.Height));
                return _amsAppBounds;
            }
            _amsAppBounds = new System.Drawing.Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            return _amsAppBounds;
        }
    }

    // action 4 — adaptive polling state (static screen → slower rate)
    private const int StaticThreshold = 3;
    private static long _lastFrameSig;
    private static int _staticFrames;

    /// <summary>
    /// Searches for any of the given pictures (OR logic, §3.3.1 group A) until the
    /// timeout. Returns the center point in SCREEN coordinates, or null on timeout.
    /// </summary>
    public static Point? FindOnScreen(
        IReadOnlyList<string> picturePaths, int similarity, string scope,
        Rectangle? region, int timeoutMs, bool neverTimeout,
        Action<string> log, CancellationToken ct)
    {
        var templates = LoadTemplates(picturePaths, log);
        if (templates.Count == 0) { log("find image: no picture files found — check paths"); return null; }

        log($"find image: searching {templates.Count} template(s), similarity={similarity}%");
        foreach (var tpl in templates) log($"find image:   template: {tpl.Width}x{tpl.Height} · " + picturePaths[templates.IndexOf(tpl)]);

        // v0.9.18 — the similarity % is no longer used as a raw NCC threshold. MatchCore maps
        // it non-linearly (MapSimilarity) and verifies colour separately (ColorTolerance), so
        // the only thing computed here is the log line. Templates under 32x32 are additionally
        // floored at 0.95 inside MatchCore.
        double mappedNcc = MapSimilarity(similarity);
        double colorTol = ColorTolerance(similarity);
        log($"find image: scope={scope} region={(region.HasValue ? $"{region.Value.X},{region.Value.Y} {region.Value.Width}x{region.Value.Height}" : "null")} ncc>={mappedNcc:F3} colorTol<={colorTol:F1} app-clip={GetAmsAppBounds()?.IsEmpty == false}");
        var vs = SystemInformation.VirtualScreen;   // for fullscreen detection in caller
        // action 2 note (docs §2.2): the ~30% stochastic skip stretches the effective window
        long effectiveTimeout = (long)(timeoutMs / 0.7);
        var sw = Stopwatch.StartNew();
        int polls = 0;
        try
        {
            // Determine search regions: if app is open and not fullscreen, split the screen
            // into 4 strips around the app so we never match on the app's own UI.
            var appB = GetAmsAppBounds();
            Rectangle[] searchRegions = ComputeSearchRegions(scope, region, appB);
            bool appClipped = appB.HasValue && !appB.Value.IsEmpty
                              && !(appB.Value.Left <= vs.Left && appB.Value.Right >= vs.Right
                                   && appB.Value.Top <= vs.Top && appB.Value.Bottom >= vs.Bottom);

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var roundSw = Stopwatch.StartNew();   // v0.9.28 — per-round timing (evidence for slow entireScreen rounds)
                long frameSig = 0;
                foreach (var t in templates)
                {
                    if (polls > 0 && Rng.NextDouble() < 0.3) continue;   // action 2 — stochastic sampling (~30% skip; v0.9.28: never on the first round — a just-started run must detect an already-visible image immediately)
                    foreach (var sr in searchRegions)
                    {
                        ct.ThrowIfCancellationRequested();   // v0.9.28 — a heavy entireScreen round used to ignore Stop for its whole duration
                        if (!neverTimeout && sw.ElapsedMilliseconds >= effectiveTimeout) return null;   // v0.9.28 — honest timeout BETWEEN strips, not only between rounds
                        var result = MatchOnce(t, similarity, "region", sr, out frameSig);
                        if (result is null) continue;
                        // v0.8.3 — when strips are too narrow (app nearly fullscreen), post-filter
                        // any HIT that falls INSIDE the app window. Strips alone can't exclude
                        // the app when it covers >95% of the screen (e.g. 1040/1080px = 40px gap).
                        if (appClipped && appB is Rectangle appBounds && HitInsideApp(result.Value.p, appBounds)) continue;
                        log($"find image: HIT score={result.Value.score:F3} · {t.Width}x{t.Height}");
                        return result.Value.p;
                    }
                }
                // action 4 — adaptive polling: static screen → slower rate (~2.5–5 FPS)
                if (frameSig != 0 && frameSig == _lastFrameSig) _staticFrames++;
                else { _staticFrames = 0; if (frameSig != 0) _lastFrameSig = frameSig; }
                if (!neverTimeout && sw.ElapsedMilliseconds >= effectiveTimeout) return null;
                if (polls == 0 || roundSw.ElapsedMilliseconds > 500)
                    log($"find image: poll round {polls + 1} took {roundSw.ElapsedMilliseconds} ms · {templates.Count} template(s) · {searchRegions.Length} region(s)");   // v0.9.28 — timing evidence
                if (++polls % 50 == 0) CheckGdiHealth();   // action 7 — every 50 polls
                Thread.Sleep(_staticFrames >= StaticThreshold ? Rng.Next(200, 400) : Rng.Next(80, 160));   // actions 1 + 4
            }
        }
        finally
        {
            foreach (var t in templates) t.Dispose();
        }
    }

    // ─────────────────────────── internals ───────────────────────────

    private static List<Bitmap> LoadTemplates(IReadOnlyList<string> paths, Action<string> log)
    {
        var list = new List<Bitmap>();
        foreach (var raw in paths)
        {
            var p = raw.Trim();
            if (p.Length == 0) continue;
            try
            {
                if (!System.IO.File.Exists(p)) { log($"find image: picture not found: {p}"); continue; }
                // action 8 — stable template ID (SHA-256 of the file) logged per template
                string tid = Convert.ToHexString(SHA256.HashData(System.IO.File.ReadAllBytes(p)))[..8];
                using var src = new Bitmap(p);
                if (Math.Min(src.Width, src.Height) < 6)
                {
                    log($"find image: template #{tid} skipped · {src.Width}x{src.Height} — thinner than 6 px; it correlates with thousands of screen locations and can never match reliably. Recapture a larger area.");
                    continue;
                }
                // normalize to 24bpp so ToRgbPooled can assume 3 bytes per pixel
                var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(bmp)) g.DrawImageUnscaled(src, 0, 0);
                list.Add(bmp);
                log($"find image: template #{tid} · {src.Width}x{src.Height}");
            }
            catch (Exception ex) { log($"find image: failed to load {p}: {ex.Message}"); }
        }
        return list;
    }

    // ───────────── v0.9.18 — similarity calibration & gates ─────────────

    /// <summary>v0.9.18 — UI "% similar" -> NCC threshold.
    /// The pre-v0.9.18 code used similarity/100 directly, but NCC lives in [-1,1] and
    /// 0.70 is a very loose bar: unrelated UI patches routinely reach it, which is why
    /// a 70% setting produced constant false positives. Maps 70% -> 0.921, 100% -> 0.995,
    /// and never drops below 0.85.</summary>
    public static double MapSimilarity(int similarity)
    {
        double s = Math.Clamp(similarity, 1, 100) / 100.0;
        return 0.85 + 0.145 * s * s;
    }

    /// <summary>v0.9.18 — max allowed mean absolute per-channel colour difference (0-255).
    /// NCC is blind to brightness AND contrast, so this is the gate that actually rejects
    /// "same shape, wrong colour" hits. 70% -> 18.5 levels, 100% -> 8 levels.</summary>
    public static double ColorTolerance(int similarity)
    {
        int s = Math.Clamp(similarity, 1, 100);
        return 8.0 + (100 - s) * 0.35;
    }

    /// <summary>v0.9.18 — templates under 32x32 are inherently ambiguous at UI scale.
    /// (The old guard said "px &lt; 32", i.e. an area smaller than 6x6, so a 16x16 icon
    /// never tripped it and got the raw loose threshold.)</summary>
    private const int SmallTemplateArea = 32 * 32;

    private static (Point p, double score)? MatchOnce(Bitmap needle, int similarity, string scope, Rectangle? region, out long frameSig)
    {
        frameSig = 0;
        // search area per scope (§3.3.1 group C)
        Rectangle area;
        var vs = SystemInformation.VirtualScreen;
        switch (scope)
        {
            case "region" when region.HasValue:
                area = Rectangle.Intersect(region.Value, vs);
                break;
            case "fromCursor":
                var c = Cursor.Position;
                area = Rectangle.Intersect(new Rectangle(c.X, c.Y, 800, 600), vs);
                break;
            default:
                // ComputeSearchRegions already gave us per-strip areas; this case is unused here.
                area = vs;
                break;
        }
        // action 3 — region jitter ±5px (hides the algorithmic search pattern)
        area.Offset(Rng.Next(-5, 6), Rng.Next(-5, 6));
        area = Rectangle.Intersect(area, vs);
        if (area.Width < needle.Width || area.Height < needle.Height) return null;

        int nw = needle.Width, nh = needle.Height;   // declared before try (post-fix rule)
        Bitmap? hay = null;
        Graphics? g = null;
        byte[]? hayRgb = null, needleRgb = null;
        try   // action 9 — nested safe dispose (an exception mid-match can never leak GDI objects)
        {
            hay = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb);
            g = Graphics.FromImage(hay);
            g.CopyFromScreen(area.Left, area.Top, 0, 0, hay.Size);

            // v0.9.18 — capture in COLOUR. The old pipeline collapsed to grayscale here and
            // the colour information was gone before any comparison happened.
            hayRgb = ToRgbPooled(hay, out int hw, out int hh);
            needleRgb = ToRgbPooled(needle, out nw, out nh);

            var hit = MatchCore(hayRgb, hw, hh, needleRgb, nw, nh, similarity, out frameSig);
            if (hit is null) return null;
            // bitmap coords → screen coords (center of the match, for the click point)
            return (new Point(hit.Value.x + area.Left + nw / 2, hit.Value.y + area.Top + nh / 2), hit.Value.score);
        }
        finally
        {
            g?.Dispose();
            hay?.Dispose();
            if (hayRgb is not null) BytePool.Return(hayRgb);       // action 5
            if (needleRgb is not null) BytePool.Return(needleRgb); // action 5
        }
    }

    /// <summary>v0.9.18 — the full three-gate match pipeline, shared by the live search and
    /// by TestRunner via <see cref="MatchForTest"/>. Buffers are packed BGR, stride = width*3.
    ///  Gate 1 — luminance NCC must reach <see cref="MapSimilarity"/>.
    ///  Gate 2 — the peak must be distinctive: either it beats the best non-overlapping
    ///           runner-up by 0.02, or the runner-up is itself a near-perfect match
    ///           (a genuine duplicate on screen, not an ambiguous plateau).
    ///  Gate 3 — FOREGROUND-weighted colour difference must be within
    ///           <see cref="ColorTolerance"/> (v0.9.19 — v0.9.18 averaged over the whole
    ///           template, so a small wrong-coloured glyph hid inside the matching
    ///           background and the colour check looked disabled).</summary>
    public static (int x, int y, double score)? MatchCore(
        byte[] hayRgb, int hw, int hh, byte[] needleRgb, int nw, int nh, int similarity,
        out long frameSig)
    {
        frameSig = 0;
        if (nw <= 0 || nh <= 0 || nw > hw || nh > hh) return null;
        // v0.9.19 — a sliver thinner than 6 px correlates with thousands of screen
        // locations; it can never be a reliable template. Reject instead of clicking wrong.
        if (Math.Min(nw, nh) < 6) return null;

        double minScore = MapSimilarity(similarity);
        if ((double)nw * nh < SmallTemplateArea) minScore = Math.Max(minScore, 0.95);

        byte[]? hayGray = null, needleGray = null;
        try
        {
            hayGray = GrayFromRgbPooled(hayRgb, hw, hh);
            needleGray = GrayFromRgbPooled(needleRgb, nw, nh);
            frameSig = CheapSig(hayGray, hw, hh);   // action 4 — screen-change signature

            var cand = MatchGray(hayGray, hw, hh, needleGray, nw, nh, minScore);
            if (cand is null) return null;
            double colorTol = ColorTolerance(similarity);

            // Gate 2 — ambiguity / distinctiveness. v0.9.19: the "genuine duplicate" rescue
            // (runner-up >= 0.97) must now VERIFY the runner-up's colour — v0.9.18 accepted
            // two same-shape wrong-colour icons as "duplicates" and passed the hit.
            double margin = cand.Value.score - cand.Value.second;
            bool distinctive = margin >= 0.02;
            if (!distinctive && cand.Value.second >= 0.97 && cand.Value.secondP.X >= 0)
                distinctive = ColorGate(hayRgb, hw, cand.Value.secondP.X, cand.Value.secondP.Y,
                                        needleRgb, nw, nh, colorTol);
            if (!distinctive) return null;

            // Gate 3 — colour (foreground-weighted; v0.9.19)
            if (!ColorGate(hayRgb, hw, cand.Value.p.X, cand.Value.p.Y, needleRgb, nw, nh, colorTol))
                return null;

            return (cand.Value.p.X, cand.Value.p.Y, cand.Value.score);
        }
        finally
        {
            if (hayGray is not null) BytePool.Return(hayGray);
            if (needleGray is not null) BytePool.Return(needleGray);
        }
    }

    /// <summary>v0.9.18 — deterministic test entry point for the pure matching pipeline
    /// (no screen capture, no RNG). Used by TestRunner steps 19-20.</summary>
    public static (int x, int y, double score)? MatchForTest(
        byte[] hayRgb, int hw, int hh, byte[] needleRgb, int nw, int nh, int similarity)
        => MatchCore(hayRgb, hw, hh, needleRgb, nw, nh, similarity, out _);

    /// <summary>v0.9.19 — foreground-weighted colour gate. v0.9.18 averaged the per-channel
    /// difference over the WHOLE template, so a template that is mostly background (a small
    /// coloured glyph on a plain field — the normal shape of a captured icon) diluted a
    /// completely wrong glyph colour down below the tolerance and colour looked disabled.
    /// Two independent checks now:
    ///  1. mean per-channel difference over FOREGROUND pixels only (background = the mean
    ///     colour of the template's 1px border ring; a pixel is foreground when it differs
    ///     from that by more than 16 in any channel). No detectable background → compare all.
    ///  2. at most 25% of ALL pixels may be grossly wrong (>32 in any channel) — this also
    ///     covers the case where the border ring itself is the coloured part.</summary>
    private static bool ColorGate(byte[] hayRgb, int hw, int x0, int y0,
                                  byte[] needleRgb, int nw, int nh, double tol)
    {
        // template background estimate: mean of the 1px border ring
        long sb = 0, sg = 0, sr = 0; int border = 0;
        for (int x = 0; x < nw; x++)
        {
            int top = x * 3, bot = ((nh - 1) * nw + x) * 3;
            sb += needleRgb[top] + needleRgb[bot];
            sg += needleRgb[top + 1] + needleRgb[bot + 1];
            sr += needleRgb[top + 2] + needleRgb[bot + 2];
            border += 2;
        }
        for (int y = 1; y < nh - 1; y++)
        {
            int lft = (y * nw) * 3, rgt = (y * nw + nw - 1) * 3;
            sb += needleRgb[lft] + needleRgb[rgt];
            sg += needleRgb[lft + 1] + needleRgb[rgt + 1];
            sr += needleRgb[lft + 2] + needleRgb[rgt + 2];
            border += 2;
        }
        int bb = (int)(sb / border), bg = (int)(sg / border), br = (int)(sr / border);

        long fgSum = 0, allSum = 0;
        int fgCount = 0, gross = 0, total = nw * nh;
        for (int j = 0; j < nh; j++)
        {
            int hrow = ((y0 + j) * hw + x0) * 3;
            int nrow = j * nw * 3;
            for (int i = 0; i < nw; i++)
            {
                int no = nrow + i * 3, ho = hrow + i * 3;
                int db = Math.Abs(hayRgb[ho] - needleRgb[no]);
                int dg = Math.Abs(hayRgb[ho + 1] - needleRgb[no + 1]);
                int dr = Math.Abs(hayRgb[ho + 2] - needleRgb[no + 2]);
                allSum += db + dg + dr;
                if (Math.Max(db, Math.Max(dg, dr)) > 32) gross++;
                int nb = Math.Abs(needleRgb[no] - bb);
                int ng = Math.Abs(needleRgb[no + 1] - bg);
                int nr = Math.Abs(needleRgb[no + 2] - br);
                if (Math.Max(nb, Math.Max(ng, nr)) > 16) { fgCount++; fgSum += db + dg + dr; }
            }
        }
        if ((double)gross / total > 0.25) return false;
        // no detectable background (full-bleed template) → whole-template mean
        if (fgCount == 0) return (double)allSum / ((double)total * 3) <= tol;
        return (double)fgSum / ((double)fgCount * 3) <= tol;
    }

    /// <summary>v0.9.28 — JIT/allocator warm-up for the vision hot path. The first real search
    /// of a run pays tier-0 JIT + cold pool/LOH costs; on entireScreen that once made poll round 1
    /// take ~15 s, so playAudio fired ~15 s after the image was already visible. One small
    /// synthetic match primes downscale, integral images, the coarse NCC loop, refine and the
    /// candidate list. Returns true when the planted needle is found (TestRunner step 28).</summary>
    public static bool Warmup()
    {
        const int hw = 96, hh = 64, nw = 36, nh = 36, nx = 24, ny = 18;
        var hay = new byte[hw * hh];
        new Random(28).NextBytes(hay);
        var ndl = new byte[nw * nh];
        for (int y = 0; y < nh; y++)
            Array.Copy(hay, (ny + y) * hw + nx, ndl, y * nw, nw);
        var hit = MatchGray(hay, hw, hh, ndl, nw, nh, 0.90);
        if (hit is null) return false;
        int f = PyramidFactor(nw, nh);
        return Math.Abs(hit.Value.p.X - nx) <= f + 1 && Math.Abs(hit.Value.p.Y - ny) <= f + 1;
    }

    /// <summary>Returns the best location plus the best NON-OVERLAPPING runner-up score and
    /// ITS location, so the caller can reject ambiguous plateaus (v0.9.19: and colour-check
    /// the runner-up before accepting it as a genuine duplicate).</summary>
    private static (Point p, double score, double second, Point secondP)? MatchGray(
        byte[] hay, int hw, int hh, byte[] needle, int nw, int nh, double minScore)
    {
        if (nw > hw || nh > hh) return null;

        // needle stats (fixed) — length-bounded because buffers are pooled (may be larger)
        double nMean = Mean(needle, nw * nh);
        double nDen = Den(needle, nMean, nw * nh);
        if (nDen <= 0) return null;   // flat template can never match meaningfully

        // integral images for O(1) window stats on the haystack
        var integ = BuildIntegral(hay, hw, hh, squared: false);
        var integ2 = BuildIntegral(hay, hw, hh, squared: true);
        double n = (double)nw * nh;

        int exclude = Math.Max(nw, nh);

        // v0.9.18 — PYRAMID coarse stage, replacing the old fixed step-3 strided scan.
        //
        // Strided sampling ALIASES badly on sharp-edged UI art: a window offset by even one
        // pixel from the true location can drop below NCC 0.62, so a step-3 grid routinely
        // never sampled the true peak at all. Combined with the old "accept anything above
        // threshold" rule, that is how the search ended up locked onto a wrong location.
        // Box-averaging is shift-tolerant instead of aliasing, and it is also ~9x CHEAPER
        // than the step-3 scan because the coarse search runs on 1/f^2 of the pixels.
        int f = PyramidFactor(nw, nh);
        double coarseMin = Math.Max(0.40, minScore - 0.25);

        const int CandidateCount = 8;
        var cx = new int[CandidateCount];
        var cy = new int[CandidateCount];
        var cs = new double[CandidateCount];
        for (int k = 0; k < CandidateCount; k++) cs[k] = double.NegativeInfinity;

        byte[]? chay = null, cndl = null;
        try
        {
            int chw = hw, chh = hh, cnw = nw, cnh = nh;
            byte[] ch = hay, cn = needle;
            if (f > 1)
            {
                chay = DownscalePooled(hay, hw, hh, f, out chw, out chh);
                cndl = DownscalePooled(needle, nw, nh, f, out cnw, out cnh);
                if (cnw < 2 || cnh < 2 || cnw > chw || cnh > chh)
                {
                    // degenerate downscale — fall back to a full-resolution coarse pass
                    BytePool.Return(chay); chay = null;
                    BytePool.Return(cndl); cndl = null;
                    f = 1; chw = hw; chh = hh; cnw = nw; cnh = nh;
                }
                else { ch = chay; cn = cndl; }
            }

            double cnMean = Mean(cn, cnw * cnh);
            double cnDen = Den(cn, cnMean, cnw * cnh);
            if (cnDen <= 0) return null;   // flat at coarse scale

            var cinteg = BuildIntegral(ch, chw, chh, squared: false);
            var cinteg2 = BuildIntegral(ch, chw, chh, squared: true);
            double cArea = (double)cnw * cnh;

            // EXHAUSTIVE at the coarse scale — no grid holes for the peak to hide in.
            for (int y = 0; y + cnh <= chh; y++)
            for (int x = 0; x + cnw <= chw; x++)
            {
                WindowStats(cinteg, cinteg2, chw, x, y, cnw, cnh, cArea,
                            out double cMean, out double cDen);
                if (cDen <= 0) continue;
                double score = Ncc(ch, chw, x, y, cn, cnw, cnh, cnMean, cMean, cnDen * cDen);
                if (score < coarseMin) continue;
                InsertCandidate(cx, cy, cs, x * f, y * f, score, exclude);
            }
        }
        finally
        {
            if (chay is not null) BytePool.Return(chay);
            if (cndl is not null) BytePool.Return(cndl);
        }

        // full-resolution refine of every surviving candidate; the coarse location is only
        // accurate to ±f px, so the refine box must cover at least that.
        int radius = f + 1;
        var rp = new Point[CandidateCount];
        var rs = new double[CandidateCount];
        int m = 0;
        for (int k = 0; k < CandidateCount; k++)
        {
            if (double.IsNegativeInfinity(cs[k])) continue;
            var r = RefineAt(hay, hw, hh, needle, nw, nh, integ, integ2, n, nMean, nDen,
                             cx[k], cy[k], radius);
            rp[m] = r.p; rs[m] = r.score; m++;
        }
        if (m == 0) return null;

        int bi = 0;
        for (int k = 1; k < m; k++) if (rs[k] > rs[bi]) bi = k;
        if (rs[bi] < minScore) return null;   // the real bar, applied AFTER refinement

        // candidates are kept spatially separated by InsertCandidate, so every other entry is
        // already a valid non-overlapping runner-up for the distinctiveness gate.
        double second = 0; int si = -1;
        for (int k = 0; k < m; k++) if (k != bi && rs[k] > second) { second = rs[k]; si = k; }

        return (rp[bi], rs[bi], second, si >= 0 ? rp[si] : new Point(-1, -1));
    }

    /// <summary>v0.9.18 — coarse-stage downscale factor. Templates thinner than 12 px in
    /// either axis are searched at full resolution (f = 1) because there is nothing left to
    /// average away without destroying the signal.</summary>
    private static int PyramidFactor(int nw, int nh)
        => Math.Max(1, Math.Min(4, Math.Min(nw, nh) / 12));

    /// <summary>v0.9.18 — box-average downscale by an integer factor, into a POOLED buffer
    /// (stride = dw). Averaging is what makes the coarse stage shift-tolerant; plain
    /// subsampling would reintroduce the aliasing this replaced.</summary>
    private static byte[] DownscalePooled(byte[] srcBuf, int w, int h, int f,
                                         out int dw, out int dh)
    {
        dw = w / f; dh = h / f;
        var dst = BytePool.Rent(Math.Max(1, dw * dh));
        int area = f * f;
        for (int y = 0; y < dh; y++)
        for (int x = 0; x < dw; x++)
        {
            int total = 0;
            for (int jj = 0; jj < f; jj++)
            {
                int row = (y * f + jj) * w + x * f;
                for (int ii = 0; ii < f; ii++) total += srcBuf[row + ii];
            }
            dst[y * dw + x] = (byte)(total / area);
        }
        return dst;
    }

    /// <summary>v0.9.18 — maintains a small top-K list of SPATIALLY SEPARATED coarse
    /// candidates. Windows within <paramref name="exclude"/> px of an existing entry collapse
    /// into it (keeping the better score) so the list can never fill up with eight views of
    /// the same blob — which would leave the real second-best location undiscovered.</summary>
    private static void InsertCandidate(int[] cx, int[] cy, double[] cs,
                                        int x, int y, double score, int exclude)
    {
        for (int k = 0; k < cs.Length; k++)
        {
            if (double.IsNegativeInfinity(cs[k])) continue;
            if (Math.Abs(cx[k] - x) <= exclude && Math.Abs(cy[k] - y) <= exclude)
            {
                if (score > cs[k]) { cs[k] = score; cx[k] = x; cy[k] = y; }
                return;
            }
        }
        int worst = 0;
        for (int k = 1; k < cs.Length; k++) if (cs[k] < cs[worst]) worst = k;
        if (score > cs[worst]) { cs[worst] = score; cx[worst] = x; cy[worst] = y; }
    }

    /// <summary>v0.9.18 — exhaustive step-1 search in a ±radius box around a coarse
    /// candidate, so a peak that fell between coarse grid lines is still found exactly.</summary>
    private static (Point p, double score) RefineAt(
        byte[] hay, int hw, int hh, byte[] needle, int nw, int nh,
        double[] integ, double[] integ2, double n, double nMean, double nDen,
        int cxx, int cyy, int radius)
    {
        int x0 = Math.Max(0, cxx - radius), x1 = Math.Min(hw - nw, cxx + radius);
        int y0 = Math.Max(0, cyy - radius), y1 = Math.Min(hh - nh, cyy + radius);
        var bp = new Point(cxx, cyy);
        double bs = double.NegativeInfinity;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            WindowStats(integ, integ2, hw, x, y, nw, nh, n, out double hMean, out double hDen);
            if (hDen <= 0) continue;
            double score = Ncc(hay, hw, x, y, needle, nw, nh, nMean, hMean, nDen * hDen);
            if (score > bs) { bs = score; bp = new Point(x, y); }
        }
        return (bp, bs);
    }

    private static double Ncc(byte[] hay, int hw, int x0, int y0,
                              byte[] needle, int nw, int nh,
                              double nMean, double hMean, double den)
    {
        double num = 0;
        for (int j = 0; j < nh; j++)
        {
            int hrow = (y0 + j) * hw + x0;
            int nrow = j * nw;
            for (int i = 0; i < nw; i++)
                num += (hay[hrow + i] - hMean) * (needle[nrow + i] - nMean);
        }
        return num / den;
    }

    private static double Mean(byte[] v, int len)
    {
        long sum = 0;
        for (int i = 0; i < len; i++) sum += v[i];
        return (double)sum / len;
    }

    private static double Den(byte[] v, double mean, int len)
    {
        double sum = 0;
        for (int i = 0; i < len; i++) { double d = v[i] - mean; sum += d * d; }
        return Math.Sqrt(sum);
    }

    private static double[] BuildIntegral(byte[] src, int w, int h, bool squared)
    {
        var ii = new double[(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            double rowSum = 0;
            int srow = y * w;
            int irow = (y + 1) * (w + 1);
            for (int x = 0; x < w; x++)
            {
                double val = src[srow + x];
                if (squared) val *= val;
                rowSum += val;
                ii[irow + x + 1] = ii[y * (w + 1) + x + 1] + rowSum;
            }
        }
        return ii;
    }

    private static void WindowStats(double[] integ, double[] integ2, int w,
                                    int x, int y, int ww, int wh, double n,
                                    out double mean, out double den)
    {
        int stride = w + 1;
        int a = y * stride + x, b = y * stride + x + ww, c = (y + wh) * stride + x, d = (y + wh) * stride + x + ww;
        double sum = integ[a] - integ[b] - integ[c] + integ[d];
        double sum2 = integ2[a] - integ2[b] - integ2[c] + integ2[d];
        mean = sum / n;
        den = Math.Sqrt(Math.Max(0, sum2 - n * mean * mean));
    }

    /// <summary>Action 5 + v0.9.18 — packed BGR into a POOLED buffer (stride = width*3,
    /// GDI row padding removed). The caller MUST return the array via BytePool.Return.
    /// Buffer may exceed width*height*3 — all readers are length-bounded.</summary>
    private static byte[] ToRgbPooled(Bitmap bmp, out int width, out int height)
    {
        width = bmp.Width; height = bmp.Height;
        var rect = new Rectangle(0, 0, width, height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        byte[]? raw = null;
        try
        {
            int stride = Math.Abs(data.Stride);
            raw = BytePool.Rent(stride * height);
            Marshal.Copy(data.Scan0, raw, 0, stride * height);
            var rgb = BytePool.Rent(width * height * 3);
            int rowBytes = width * 3;
            for (int y = 0; y < height; y++)
                Array.Copy(raw, y * stride, rgb, y * rowBytes, rowBytes);
            return rgb;
        }
        finally
        {
            bmp.UnlockBits(data);
            if (raw is not null) BytePool.Return(raw);
        }
    }

    /// <summary>v0.9.18 — LUMINANCE-weighted grayscale (0.114B + 0.587G + 0.299R) from a
    /// packed BGR buffer, into a POOLED buffer.
    ///
    /// The pre-v0.9.18 code used (B+G+R)/3, which maps pure red, pure green and pure blue
    /// ALL to 85 — so differently-coloured icons became byte-identical to the matcher and
    /// scored NCC = 1.000. That was the primary false-positive source.</summary>
    private static byte[] GrayFromRgbPooled(byte[] rgb, int w, int h)
    {
        var gray = BytePool.Rent(w * h);
        for (int y = 0; y < h; y++)
        {
            int srow = y * w * 3;
            int drow = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = srow + x * 3;
                gray[drow + x] = (byte)((rgb[i] * 114 + rgb[i + 1] * 587 + rgb[i + 2] * 299) / 1000);
            }
        }
        return gray;
    }

    /// <summary>Action 4 — cheap frame signature for screen-change detection
    /// (64 sampled bytes; ~0 cost). Only used to slow the poll rate on static screens.</summary>
    private static long CheapSig(byte[] gray, int w, int h)
    {
        long sig = w * 31L + h;
        int len = w * h, step = Math.Max(1, len / 64);
        for (int i = 0; i < len; i += step) sig = sig * 131 + gray[i];
        return sig;
    }

    /// <summary>Action 7 — Warden can count GDI handles; we watch our own count and
    /// fail loud instead of leaking.</summary>
    private static void CheckGdiHealth()
    {
        int handles = GetGuiResources(Process.GetCurrentProcess().Handle, GR_GDIOBJECTS);
        if (handles > GdiObjectLimit)
            throw new InvalidOperationException($"GDI object leak detected: {handles} handles");
    }

    /// <summary>Given the desired scope/region and an optional app-window bound, returns an array
    /// of search rectangles. When the app is not covering the full screen, the returned regions are
    /// the 4 strips around the app (top, bottom, left, right) — ensuring we never match on the
    /// app's own UI panels. When the app is fullscreen or absent, returns a single region = virtual screen.</summary>
    private static Rectangle[] ComputeSearchRegions(string scope, Rectangle? region,
        System.Drawing.Rectangle? appB)
    {
        var vs = SystemInformation.VirtualScreen;

        // First resolve the base area from scope/region (same logic as old MatchOnce).
        Rectangle baseArea;
        switch (scope)
        {
            case "region" when region.HasValue:
                baseArea = Rectangle.Intersect(region.Value, vs);
                break;
            case "fromCursor":
                var c = Cursor.Position;
                baseArea = Rectangle.Intersect(new Rectangle(c.X, c.Y, 800, 600), vs);
                break;
            default:
                baseArea = vs;
                break;
        }

        // If no app window or app covers full screen → search entire base area.
        if (appB is null || appB.Value.IsEmpty)
            return new[] { baseArea };
        var appRect = appB.Value;
        if (appRect.Left <= vs.Left && appRect.Right >= vs.Right
            && appRect.Top <= vs.Top && appRect.Bottom >= vs.Bottom)
            return new[] { baseArea };

        // App is floating — split into strips around it.
        // Each strip is clipped to baseArea ∩ vs.
        List<Rectangle> strips = new List<Rectangle>();

        // top strip: above app, full base width
        int tTop = Math.Max(baseArea.Top, vs.Top);
        int tBot = Math.Min(baseArea.Bottom, appRect.Top);
        int tL = Math.Max(baseArea.Left, vs.Left);
        int tR = Math.Min(baseArea.Right, vs.Right);
        if (tBot > tTop && tR > tL) strips.Add(new Rectangle(tL, tTop, tR - tL, tBot - tTop));

        // bottom strip: below app
        int bTop = Math.Max(baseArea.Top, appRect.Bottom);
        int bBot = Math.Min(baseArea.Bottom, vs.Bottom);
        int bL = Math.Max(baseArea.Left, vs.Left);
        int bR = Math.Min(baseArea.Right, vs.Right);
        if (bBot > bTop && bR > bL) strips.Add(new Rectangle(bL, bTop, bR - bL, bBot - bTop));

        // left strip: to the left of app (only between app's top and bottom edges)
        int lTop = Math.Max(baseArea.Top, Math.Max(vs.Top, appRect.Top));
        int lBot = Math.Min(baseArea.Bottom, Math.Min(vs.Bottom, appRect.Bottom));
        int lL = Math.Max(baseArea.Left, vs.Left);
        int lR = Math.Min(baseArea.Right, appRect.Left);
        if (lBot > lTop && lR > lL) strips.Add(new Rectangle(lL, lTop, lR - lL, lBot - lTop));

        // right strip: to the right of app
        int rTop = Math.Max(baseArea.Top, Math.Max(vs.Top, appRect.Top));
        int rBot = Math.Min(baseArea.Bottom, Math.Min(vs.Bottom, appRect.Bottom));
        int rL = Math.Max(baseArea.Left, appRect.Right);
        int rR = Math.Min(baseArea.Right, vs.Right);
        if (rBot > rTop && rR > rL) strips.Add(new Rectangle(rL, rTop, rR - rL, rBot - rTop));

        return strips.Count == 0 ? new[] { baseArea } : strips.ToArray();
    }

    /// <summary>v0.8.3 — post-filter: returns true if the hit point falls INSIDE the app window.
    /// Used as a fallback when ComputeSearchRegions strips are too narrow (app nearly fullscreen)
    /// and can't fully exclude the app area by geometry alone.</summary>
    private static bool HitInsideApp(Point p, System.Drawing.Rectangle appRect)
        => p.X >= appRect.Left && p.X < appRect.Right
        && p.Y >= appRect.Top && p.Y < appRect.Bottom;

    /// <summary>Subtract <paramref name="hole"/> from <paramref name="rect"/> using
    /// 4 rectangular strips around the hole. Returns the SINGLE largest strip
    /// (or Rectangle.Empty if nothing remains). Kept for backward compat; see
    /// ComputeSearchRegions for the multi-region approach.</summary>
    private static Rectangle ClipRect(Rectangle rect, Rectangle hole)
    {
        // Build each possible remaining strip; keep only valid (positive-area) ones.
        var strips = new List<Rectangle>();

        // top strip (above the hole)
        if (rect.Top < hole.Bottom && rect.Bottom > hole.Top && rect.Left < hole.Right && rect.Right > hole.Left)
            strips.Add(new Rectangle(
                rect.Left, rect.Top,
                rect.Width,
                Math.Max(0, Math.Min(rect.Bottom, hole.Top) - rect.Top)));

        // bottom strip (below the hole)
        if (rect.Top < hole.Bottom && rect.Bottom > hole.Top && rect.Left < hole.Right && rect.Right > hole.Left)
            strips.Add(new Rectangle(
                rect.Left, Math.Max(rect.Top, hole.Bottom),
                rect.Width,
                Math.Max(0, rect.Bottom - Math.Max(rect.Top, hole.Bottom))));

        // left strip (to the left of the hole)
        if (rect.Left < hole.Right && rect.Right > hole.Left && rect.Top < hole.Bottom && rect.Bottom > hole.Top)
            strips.Add(new Rectangle(
                rect.Left, Math.Max(rect.Top, hole.Top),
                Math.Max(0, Math.Min(rect.Right, hole.Left) - rect.Left),
                Math.Min(rect.Bottom, hole.Bottom) - Math.Max(rect.Top, hole.Top)));

        // right strip (to the right of the hole)
        if (rect.Left < hole.Right && rect.Right > hole.Left && rect.Top < hole.Bottom && rect.Bottom > hole.Top)
            strips.Add(new Rectangle(
                Math.Max(rect.Left, hole.Right), Math.Max(rect.Top, hole.Top),
                Math.Max(0, rect.Right - Math.Max(rect.Left, hole.Right)),
                Math.Min(rect.Bottom, hole.Bottom) - Math.Max(rect.Top, hole.Top)));

        // remove zero-area strips and pick the one with the largest area
        Rectangle best = Rectangle.Empty;
        long bestArea = 0;
        foreach (var r in strips)
        {
            long area = (long)r.Width * r.Height;
            if (area > bestArea) { bestArea = area; best = r; }
        }
        return best;
    }
}
