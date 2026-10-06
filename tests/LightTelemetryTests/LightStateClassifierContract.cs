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
        Check(profiles.Count == 9
              && profiles.Select(x => x.Id).ToHashSet().SetEquals(new[]
              {
                  "desktop", "login-or-dc", "character-dashboard",
                  "entering-game-loading", "game", "targeted",
                  "targeted-repeat", "whisper", "whisper-repeat",
              }),
            "nine editable Guard profiles include Targeted New and Repeat");

        var desktop = profiles.Single(x => x.Id == "desktop");
        Check(desktop.LuxMin == 58 && desktop.LuxMax == 60,
            "approved desktop range remains 59 plus/minus one Lux");

        var login = profiles.Single(x => x.Id == "login-or-dc");
        var game = profiles.Single(x => x.Id == "game");
        Check(login.LuxMin == 60.5 && login.LuxMax == 87.5
              && game.LuxMin == 32.3 && game.LuxMax == 33.7,
            "approved login and game ranges remain user-editable and unmodified");
        var targeted = profiles.Single(x => x.Id == "targeted");
        var targetedRepeat = profiles.Single(x => x.Id == "targeted-repeat");
        Check(targeted.LightCooldownMs == 0
              && targetedRepeat.LightCooldownMs == 60000
              && targeted.CalibrationCuePattern != targetedRepeat.CalibrationCuePattern,
            "Targeted New has no cooldown and Repeat has a distinct cue and cooldown");

        // Classification mechanics use an intentionally overlapping synthetic
        // set; approved hardware defaults are designed not to overlap.
        var classifierProfiles = new[]
        {
            Profile("login-or-dc", 25),
            Profile("game", 26),
            Profile("character-dashboard", 31),
        };

        var t0 = new DateTimeOffset(2026, 9, 15, 5, 0, 0, TimeSpan.Zero);
        var overlap = new LightStateClassifier(classifierProfiles).Update(25, t0);
        Check(overlap.Kind == LightStateClassificationKind.Ambiguous
              && overlap.Matches?.Select(x => x.Id).OrderBy(x => x)
                  .SequenceEqual(new[] { "game", "login-or-dc" }) == true,
            "overlap guard reports every match and never chooses by profile order");

        var stable = new LightStateClassifier(classifierProfiles);
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

        var reordered = new LightStateClassifier(
            classifierProfiles.Reverse<LightStateProfile>());
        Check(reordered.Update(25, t0).Kind == LightStateClassificationKind.Ambiguous,
            "overlap result is invariant under profile ordering");

        return checks;

        static LightStateProfile Profile(string id, double center) => new()
        {
            Id = id,
            Name = id,
            LuxCenter = center,
            LuxTolerance = 2,
            StableDurationMs = 750,
            HysteresisLux = 1,
        };
    }
}
