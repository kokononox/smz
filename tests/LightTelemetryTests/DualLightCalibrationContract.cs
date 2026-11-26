using System.Runtime.CompilerServices;
using Ams.UI.Models;
using Ams.UI.Services;

internal static class DualLightCalibrationContract
{
    [ModuleInitializer]
    internal static void Execute()
    {
        var passed = 0;
        var failed = 0;
        void Check(bool condition, string name)
        {
            if (condition) { passed++; Console.WriteLine("PASS: " + name); }
            else { failed++; Console.WriteLine("FAIL: " + name); }
        }
        static bool Near(double actual, double expected) => Math.Abs(actual - expected) < 1e-9;

        var suggestion = DualLightCalibration.Suggest(new[]
        {
            (10.0, 20.0), (10.2, 20.2), (9.8, 19.9), (10.1, 20.2), (10.0, 20.1),
        });
        Check(Near(suggestion.MeanCenterLux, 15.05)
              && Near(suggestion.DifferenceCenterLux, 10.1)
              && Near(suggestion.Sensor1CenterLux, 10.0)
              && Near(suggestion.Sensor1MinimumLux, 9.8)
              && Near(suggestion.Sensor1MaximumLux, 10.2)
              && Near(suggestion.Sensor2CenterLux, 20.1)
              && Near(suggestion.Sensor2MinimumLux, 19.9)
              && Near(suggestion.Sensor2MaximumLux, 20.2)
              && suggestion.SampleCount == 5,
            "paired proposal stores raw L1/L2 ranges and the synchronized M/Δ signature");
        Check(suggestion.MeanToleranceLux >= suggestion.MeanMinimumToleranceLux
              && suggestion.DifferenceToleranceLux >= suggestion.DifferenceMinimumToleranceLux
              && suggestion.MeanToleranceLux >= DualLightCalibration.MinimumAxisToleranceLux
              && suggestion.DifferenceToleranceLux >= DualLightCalibration.MinimumAxisToleranceLux
              && LightStateProfile.IsValidDualAxisTolerance(
                  suggestion.MeanToleranceLux, LightStateProfile.MeanLuxToleranceUnitsPerLux)
              && LightStateProfile.IsValidDualAxisTolerance(
                  suggestion.DifferenceToleranceLux,
                  LightStateProfile.DifferenceLuxToleranceUnitsPerLux),
            "automatic tolerance proposals respect robust spread, the 3-Lux floor and board precision");
        var outlierSuggestion = DualLightCalibration.Suggest(
            Enumerable.Range(0, 8).Select(_ => (10.0, 20.0))
                .Append((50.0, 60.0)));
        Check(Near(outlierSuggestion.MeanCenterLux, 15)
              && Near(outlierSuggestion.DifferenceCenterLux, 10)
              && Near(outlierSuggestion.MeanToleranceLux, 3)
              && Near(outlierSuggestion.DifferenceToleranceLux, 3),
            "median/MAD proposals resist a single transient calibration outlier");

        var adjustable = Profile("adjustable", mean: 30, difference: 5, tolerance: 3);
        adjustable.MeanLuxTolerance = 4.25;
        adjustable.DifferenceLuxTolerance = 3.7;
        Check(adjustable.HasValidDualAxisTolerances,
            "independent M and Δ tolerances accept supported board precision above 3 Lux");
        adjustable.MeanLuxTolerance = 2.95;
        Check(!adjustable.HasValidDualAxisTolerances,
            "paired tolerance validation rejects values below 3 Lux");
        adjustable.MeanLuxTolerance = 4.25;
        adjustable.DifferenceLuxTolerance = 3.73;
        Check(!adjustable.HasValidDualAxisTolerances,
            "paired tolerance validation rejects values the board fixed-point format cannot represent");
        try
        {
            DualLightCalibration.Suggest(new[] { (1.0, 2.0), (1.0, 2.0) });
            Check(false, "paired proposal refuses too few samples");
        }
        catch (ArgumentException) { Check(true, "paired proposal refuses too few samples"); }

        var first = Profile("first", mean: 20, difference: 0, tolerance: 3);
        var contrastSeparated = Profile("contrast", mean: 20, difference: 10, tolerance: 3);
        var safe = DualLightCalibration.Validate(new[] { first, contrastSeparated });
        Check(safe.IsValid,
            "overlap on mean alone is accepted when contrast gives the required combined-space clearance");

        var tooClose = Profile("too-close", mean: 20, difference: 6.9, tolerance: 3);
        Check(!DualLightCalibration.Validate(new[] { first, tooClose }).IsValid,
            "per-profile tolerances plus the extra inter-profile margin reject insufficient clearance");
        var spreadSuggestion = DualLightCalibration.Suggest(new[]
        {
            (10.0, 20.0), (10.0, 21.0), (10.0, 19.0), (10.0, 22.0),
            (10.0, 18.0), (10.0, 21.0), (10.0, 19.0),
        });
        var autoCandidate = Profile("auto-fit", mean: 20, difference: 7.5, tolerance: 3);
        Check(DualLightCalibration.TryFitAutomaticTolerances(autoCandidate,
                  new[] { first }, spreadSuggestion, out var fittedValidation, out var reduced)
              && reduced && fittedValidation.IsValid
              && autoCandidate.DifferenceLuxTolerance
                 >= spreadSuggestion.DifferenceMinimumToleranceLux
              && autoCandidate.DifferenceLuxTolerance
                 < spreadSuggestion.DifferenceToleranceLux
              && Math.Abs(autoCandidate.DifferenceLuxTolerance
                          * LightStateProfile.DifferenceLuxToleranceUnitsPerLux
                          - Math.Round(autoCandidate.DifferenceLuxTolerance
                                       * LightStateProfile.DifferenceLuxToleranceUnitsPerLux)) < 1e-9,
            "automatic fitting reduces tolerance to the widest safe supported value without going below sample spread");
        var impossibleCandidate = Profile("auto-impossible", mean: 20, difference: 6.9, tolerance: 3);
        var tightSuggestion = DualLightCalibration.Suggest(
            Enumerable.Repeat((10.0, 20.0), DualLightCalibration.MinimumSampleCount));
        Check(!DualLightCalibration.TryFitAutomaticTolerances(impossibleCandidate,
                  new[] { first }, tightSuggestion, out var impossibleValidation, out _)
              && !impossibleValidation.IsValid
              && Near(impossibleCandidate.DifferenceLuxTolerance,
                  tightSuggestion.DifferenceMinimumToleranceLux),
            "automatic fitting reports a real conflict instead of shrinking below the noise floor");
        var tangent = Profile("tangent", mean: 27, difference: 0, tolerance: 3);
        Check(!DualLightCalibration.Validate(new[] { first, tangent }).IsValid,
            "a pair exactly on the required safety boundary fails closed");

        var at = new DateTimeOffset(2026, 11, 21, 20, 0, 0, TimeSpan.Zero);
        first = Profile("first", mean: 20, difference: 0, tolerance: 3);
        contrastSeparated = Profile("contrast", mean: 20, difference: 10, tolerance: 3);
        var classifier = new LightStateClassifier(new[] { first, contrastSeparated });
        Check(classifier.Update(20, 20, at).Kind == LightStateClassificationKind.Candidate
              && classifier.Update(20, 20, at.AddMilliseconds(750)).Kind == LightStateClassificationKind.Stable
              && classifier.Update(15, 25, at.AddMilliseconds(800)).Profile?.Id == "contrast",
            "paired classifier applies the 2D ellipse and existing stable-duration behavior");
        var overlapping = new LightStateClassifier(new[]
        {
            Profile("overlap-a", mean: 20, difference: 0, tolerance: 3),
            Profile("overlap-b", mean: 20, difference: 0, tolerance: 3),
        }).Update(20, 20, at);
        Check(overlapping.Kind == LightStateClassificationKind.Ambiguous
              && overlapping.Matches?.Count == 2,
            "runtime reports both matches instead of selecting by profile order");
        Check(new LightStateClassifier(new[] { first }).Update(20, null, at).Kind
              == LightStateClassificationKind.Unknown,
            "paired profile never falls back to one sensor when the other reading is missing");
        var legacyClassifier = new LightStateClassifier(new[]
        {
            new LightStateProfile { Id = "legacy", Name = "legacy", LuxCenter = 40, LuxTolerance = 1 },
        });
        Check(legacyClassifier.Update(40, null, at).Kind == LightStateClassificationKind.Candidate,
            "legacy single-sensor profiles still classify when sensor two is absent");
        var mixed = new LightStateClassifier(new[]
        {
            first, new LightStateProfile { Id = "legacy", Name = "legacy", LuxCenter = 40, LuxTolerance = 1 },
        }).Update(20, 20, at);
        Check(mixed.Kind == LightStateClassificationKind.Unknown && mixed.Diagnostic is not null,
            "mixed enabled single/dual profiles fail closed until calibration modes are consistent");
        var newLayoutProfiles = LightStateDefaults.CreateInitialProfiles();
        foreach (var profile in newLayoutProfiles) profile.DualSensorCalibrationRequired = true;
        var newLayoutClassifier = new LightStateClassifier(newLayoutProfiles);
        Check(newLayoutClassifier.Update(59, at).Kind == LightStateClassificationKind.Unknown
              && newLayoutClassifier.Update(59, 61, at).Diagnostic is not null,
            "a reset sensor layout cannot silently fall back to legacy L1 during recalibration");

        var directory = Path.Combine(Path.GetTempPath(), "ams-dual-light-contract-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "profiles.json");
        try
        {
            var original = LightStateDefaults.CreateInitialProfiles();
            LightStateProfileStore.Save(original, path);

            var calibrated = LightStateDefaults.CreateInitialProfiles();
            var roundtrip = calibrated.Single(profile => profile.Id == "desktop");
            roundtrip.HasDualSensorCalibration = true;
            roundtrip.DualSensorCalibrationRequired = true;
            roundtrip.UseAutomaticDualTolerance = false;
            roundtrip.MeanLuxCenter = 30;
            roundtrip.MeanLuxTolerance = 4.25;
            roundtrip.DifferenceLuxCenter = -4;
            roundtrip.DifferenceLuxTolerance = 3.7;
            LightStateProfileStore.Save(calibrated, path);
            var loaded = LightStateProfileStore.Load(path).Single(profile => profile.Id == "desktop");
            Check(loaded.HasDualSensorCalibration
                  && loaded.DualSensorCalibrationRequired
                  && !loaded.UseAutomaticDualTolerance
                  && Near(loaded.MeanLuxCenter, 30) && Near(loaded.MeanLuxTolerance, 4.25)
                  && Near(loaded.DifferenceLuxCenter, -4) && Near(loaded.DifferenceLuxTolerance, 3.7),
                "paired calibration values round-trip without altering legacy schema fields");
            var bytesBeforeConflict = File.ReadAllBytes(path);

            var conflicting = LightStateDefaults.CreateInitialProfiles();
            SetDual(conflicting.Single(profile => profile.Id == "desktop"), 20, 0, 3);
            SetDual(conflicting.Single(profile => profile.Id == "game"), 20, 6.9, 3);
            try
            {
                LightStateProfileStore.Save(conflicting, path);
                Check(false, "profile store refuses a conflicting paired save");
            }
            catch (InvalidOperationException)
            {
                Check(true, "profile store refuses a conflicting paired save");
            }

            var remains = LightStateProfileStore.Load(path).Single(profile => profile.Id == "desktop");
            Check(remains.HasDualSensorCalibration && Near(remains.MeanLuxCenter, 30)
                  && bytesBeforeConflict.SequenceEqual(File.ReadAllBytes(path)),
                "rejected save does not overwrite previously stored paired calibration");
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); } catch { }
        }

        Console.WriteLine($"=== Dual light calibration results: {passed} passed, {failed} failed ===");
        if (failed > 0) throw new InvalidOperationException($"Dual light calibration contract failed: {failed}");
    }

    private static LightStateProfile Profile(string id, double mean, double difference, double tolerance)
        => new()
        {
            Id = id,
            Name = id,
            HasDualSensorCalibration = true,
            DualSensorCalibrationRequired = true,
            MeanLuxCenter = mean,
            MeanLuxTolerance = tolerance,
            DifferenceLuxCenter = difference,
            DifferenceLuxTolerance = tolerance,
            LuxCenter = 20,
            LuxTolerance = 1,
            StableDurationMs = 750,
        };

    private static void SetDual(LightStateProfile profile, double mean, double difference, double tolerance)
    {
        profile.HasDualSensorCalibration = true;
        profile.DualSensorCalibrationRequired = true;
        profile.MeanLuxCenter = mean;
        profile.MeanLuxTolerance = tolerance;
        profile.DifferenceLuxCenter = difference;
        profile.DifferenceLuxTolerance = tolerance;
    }
}