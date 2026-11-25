using System.Runtime.CompilerServices;
using Ams.UI.Models;
using Ams.UI.Services;

internal static class PipelineWorkspaceLightProfileContract
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

        var profiles = LightStateDefaults.CreateInitialProfiles();
        for (var i = 0; i < profiles.Count; i++)
        {
            var profile = profiles[i];
            profile.HasDualSensorCalibration = true;
            profile.DualSensorCalibrationRequired = true;
            profile.MeanLuxCenter = 20.5 + i * 20;
            profile.MeanLuxTolerance = 0.5;
            profile.DifferenceLuxCenter = 0;
            profile.DifferenceLuxTolerance = 0.5;
            profile.Sensor1LuxCenter = 10 + i;
            profile.Sensor1LuxMinimum = 9 + i;
            profile.Sensor1LuxMaximum = 11 + i;
            profile.Sensor2LuxCenter = 10 + i;
            profile.Sensor2LuxMinimum = 9 + i;
            profile.Sensor2LuxMaximum = 11 + i;
            profile.DualSensorSampleCount = 5;
        }

        var workspace = new PipelineWorkspace { LightStateProfiles = profiles };
        var json = PipelineWorkspaceSerializer.Serialize(workspace);
        var loaded = PipelineWorkspaceSerializer.Deserialize(json);
        var paired = loaded.LightStateProfiles?.Single(profile => profile.Id == "desktop");
        Check(json.Contains("\"lightStateProfiles\"", StringComparison.Ordinal)
              && json.Contains("\"pipelineVersion\": 15", StringComparison.Ordinal)
              && paired is { HasDualSensorCalibration: true, DualSensorCalibrationRequired: true }
              && paired.MeanLuxCenter == 20.5
              && paired.DifferenceLuxCenter == 0
              && paired.Sensor1LuxCenter == 10
              && paired.Sensor1LuxMinimum == 9
              && paired.Sensor1LuxMaximum == 11
              && paired.Sensor2LuxCenter == 10
              && paired.Sensor2LuxMinimum == 9
              && paired.Sensor2LuxMaximum == 11
              && paired.DualSensorSampleCount == 5,
            ".amsj round-trips dual layout flags, M/Δ and both raw sensor summaries");

        const string legacy = "{\"app\":\"AMS\",\"pipelineVersion\":14,\"pipelines\":{\"Desktop\":[]}}";
        var legacyLoaded = PipelineWorkspaceSerializer.Deserialize(legacy);
        Check(legacyLoaded.LightStateProfiles is null,
            "legacy .amsj without project calibration keeps the app-level profile fallback");

        Console.WriteLine($"=== Pipeline light-profile results: {passed} passed, {failed} failed ===");
        if (failed > 0) throw new InvalidOperationException($"Pipeline light-profile contract failed: {failed}");
    }
}
