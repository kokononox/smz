using System.Runtime.CompilerServices;
using Ams.UI.Models;
using Ams.UI.Services;

internal static class LightStateClassifierContract
{
    [ModuleInitializer]
    internal static void Execute()
    {
        var checks = Run();
        foreach (var check in checks)
            Console.WriteLine((check.Passed ? "PASS: " : "FAIL: ") + check.Name);
        var failed = checks.Count(x => !x.Passed);
        Console.WriteLine($"=== Light state profile results: {checks.Count - failed} passed, {failed} failed ===");
        if (failed > 0) throw new InvalidOperationException($"Light state profile contract failed: {failed}");
    }

    private static IReadOnlyList<(bool Passed, string Name)> Run()
    {
        var checks = new List<(bool, string)>();
        void Check(bool condition, string name) => checks.Add((condition, name));

        var profiles = LightStateDefaults.CreateInitialProfiles();
        Check(profiles.Count == 7 && profiles.All(x => x.LuxTolerance == 2),
            "seven editable Guard profiles default to plus/minus two Lux");

        var desktop = profiles.Single(x => x.Id == "desktop");
        Check(desktop.LuxMin == 0 && desktop.LuxMax == 2,
            "zero-Lux desktop range is clamped at the physical lower bound");

        var login = profiles.Single(x => x.Id == "login-or-dc");
        var game = profiles.Single(x => x.Id == "game");
        Check(login.LuxMin == 23 && login.LuxMax == 27 && game.LuxMin == 24 && game.LuxMax == 28,
            "hypothetical login and game ranges remain user-editable and unmodified");

        var t0 = new DateTimeOffset(2026, 9, 15, 5, 0, 0, TimeSpan.Zero);
        var overlap = new LightStateClassifier(profiles).Update(25, t0);
        Check(overlap.Kind == LightStateClassificationKind.Ambiguous
              && overlap.Matches?.Select(x => x.Id).OrderBy(x => x)
                  .SequenceEqual(new[] { "game", "login-or-dc" }) == true,
            "overlap guard reports every match and never chooses by profile order");

        var stable = new LightStateClassifier(profiles);
        Check(stable.Update(31, t0).Kind == LightStateClassificationKind.Candidate,
            "first matching sample starts a candidate");
        Check(stable.Update(31, t0.AddMilliseconds(749)).Kind == LightStateClassificationKind.Candidate,
            "candidate does not stabilize before StableDurationMs");
        var accepted = stable.Update(31, t0.AddMilliseconds(750));
        Check(accepted.Kind == LightStateClassificationKind.Stable
              && accepted.Profile?.Id == "character-dashboard"
              && accepted.Confidence == 1,
            "candidate becomes stable exactly at StableDurationMs");
        Check(stable.Update(33.5, t0.AddMilliseconds(800)).Kind == LightStateClassificationKind.Stable,
            "hysteresis holds the already stable state just outside its base range");
        Check(stable.Update(31, t0.AddMilliseconds(900), sampleIsFresh: false).Kind == LightStateClassificationKind.Unknown,
            "stale samples clear state and classify as unknown");
        Check(stable.Update(-0.1, t0.AddSeconds(1)).Kind == LightStateClassificationKind.Invalid,
            "negative Lux is invalid");

        var reordered = new LightStateClassifier(profiles.Reverse<LightStateProfile>());
        Check(reordered.Update(25, t0).Kind == LightStateClassificationKind.Ambiguous,
            "overlap result is invariant under profile ordering");

        return checks;
    }
}
