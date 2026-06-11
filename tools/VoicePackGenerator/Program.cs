using System.Net.Http;

var appDataDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "AcEvoFfbTuner", "voice-cache");

var projectDir = Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory, "..", "..", "..", "..", "src", "AcEvoFfbTuner", "VoicePack"));

var outDirs = new[] { appDataDir, projectDir };
foreach (var d in outDirs) Directory.CreateDirectory(d);

var phrases = new Dictionary<string, string>
{
    // Event announcements
    { "wheelbase connected", "wheelbase-connected.mp3" },
    { "wheelbase disconnected", "wheelbase-disconnected.mp3" },
    { "game connected", "game-connected.mp3" },
    { "game disconnected", "game-disconnected.mp3" },
    { "snapshot saved", "snapshot-saved.mp3" },
    { "telemetry started", "telemetry-started.mp3" },
    { "telemetry stopped", "telemetry-stopped.mp3" },
    { "natural voices installed", "natural-voices-installed.mp3" },

    // Wizard step announcements
        { "step 1. welcome & safety.", "step-1-welcome-safety.mp3" },
        { "step 2. drive & calibrate.", "step-2-drive-calibrate.mp3" },
        { "step 3. intensity preference.", "step-3-intensity-preference.mp3" },
        { "step 4. save profile.", "step-4-save-profile.mp3" },

    // Wizard voice prompts
    { "welcome. when you are ready, drive onto the track and click next.", "wiz-welcome.mp3" },
    { "drive through a few corners. i will check your centering direction and set your force strength.", "wiz-centering-detect.mp3" },
    { "calibration complete. choose how heavy or light you want this car to feel, then click next.", "wiz-force-strength-done.mp3" },
    { "save profile. give your profile a name and click save and finish.", "wiz-save-profile.mp3" },
        { "profile loaded", "profile-loaded.mp3" },
        { "profile saved. setup complete.", "profile-saved.mp3" },
};

Console.WriteLine($"Generating voice pack in:\n  {appDataDir}\n  {projectDir}\n");

using var http = new HttpClient();
http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

int success = 0;
int skipped = 0;

foreach (var kvp in phrases)
{
    Console.Write($" [FETCH] {kvp.Key} ... ");
    try
    {
        var encoded = Uri.EscapeDataString(kvp.Key);
        var url = $"https://translate.google.com/translate_tts?ie=UTF-8&q={encoded}&tl=en&client=tw-ob";
        using var response = await http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var data = await response.Content.ReadAsByteArrayAsync();

        foreach (var dir in outDirs)
        {
            var dest = Path.Combine(dir, kvp.Value);
            await File.WriteAllBytesAsync(dest, data);
        }

        Console.WriteLine($"OK ({data.Length} bytes)");
        success++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAILED: {ex.Message}");
    }

    await Task.Delay(2000);
}

Console.WriteLine($"\nDone: {success} generated, {skipped} skipped, {phrases.Count - success - skipped} failed");
