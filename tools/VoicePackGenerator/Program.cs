using System.Net.Http;
using System.Text.Json;

var cacheDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "AcEvoFfbTuner", "voice-cache");

var phrases = new Dictionary<string, string>
{
    { "wheelbase connected", "wheelbase-connected.mp3" },
    { "wheelbase disconnected", "wheelbase-disconnected.mp3" },
    { "game connected", "game-connected.mp3" },
    { "game disconnected", "game-disconnected.mp3" },
    { "snapshot saved", "snapshot-saved.mp3" },
    { "telemetry started", "telemetry-started.mp3" },
    { "telemetry stopped", "telemetry-stopped.mp3" },
    { "natural voices installed", "natural-voices-installed.mp3" },
    { "setup wizard loaded. drive safely and follow the on screen instructions.", "setup-wizard-loaded.mp3" },
    { "profile saved. setup complete.", "profile-saved.mp3" },
    { "phase 1 of 3. drive straight and hold the wheel steady.", "phase-1-of-3.mp3" },
    { "phase 2 of 3. drive through turns normally.", "phase-2-of-3.mp3" },
    { "phase 3 of 3. fine tuning center response.", "phase-3-of-3.mp3" },
    { "centering auto tune complete.", "centering-autotune-complete.mp3" },
    { "core tyre forces tuned.", "core-tyre-forces-tuned.mp3" },
    { "damping and friction tuning complete.", "damping-tuning-complete.mp3" },
    { "force level calibrated.", "force-calibrated.mp3" },
    { "vibration levels sampled.", "vibration-sampled.mp3" },

    { "step 1. welcome & safety.", "step-1-welcome-safety.mp3" },
    { "step 2. wheel centering.", "step-2-wheel-centering.mp3" },
    { "step 3. core tyre forces.", "step-3-core-tyre-forces.mp3" },
    { "step 4. master output gain.", "step-4-master-output-gain.mp3" },
    { "step 5. damping & friction.", "step-5-damping-friction.mp3" },
    { "step 6. curb & vibration.", "step-6-curb-vibration.mp3" },
    { "step 7. review & confirm.", "step-7-review-confirm.mp3" },
    { "step 8. save profile.", "step-8-save-profile.mp3" },
};

Directory.CreateDirectory(cacheDir);
Console.WriteLine($"Generating voice pack in: {cacheDir}");
Console.WriteLine();

using var http = new HttpClient();
http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

int success = 0;
int skipped = 0;

foreach (var kvp in phrases)
{
    var path = Path.Combine(cacheDir, kvp.Value);

    if (File.Exists(path))
    {
        Console.WriteLine($" [SKIP] {kvp.Value} (already exists)");
        skipped++;
        continue;
    }

    Console.Write($" [FETCH] {kvp.Value} ... ");
    try
    {
        var encoded = Uri.EscapeDataString(kvp.Key);
        var url = $"https://translate.google.com/translate_tts?ie=UTF-8&q={encoded}&tl=en&client=tw-ob";
        using var response = await http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var data = await response.Content.ReadAsByteArrayAsync();
        await File.WriteAllBytesAsync(path, data);
        Console.WriteLine($"OK ({data.Length} bytes)");
        success++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAILED: {ex.Message}");
    }

    await Task.Delay(2000);
}

Console.WriteLine();
Console.WriteLine($"Done: {success} generated, {skipped} skipped, {phrases.Count - success - skipped} failed");
