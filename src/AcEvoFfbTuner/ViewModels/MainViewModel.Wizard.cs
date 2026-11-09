using System.IO;
using AcEvoFfbTuner.Core.FfbProviders;
using AcEvoFfbTuner.Core.Profiles;

namespace AcEvoFfbTuner.ViewModels;

/// <summary>
/// Support surface for the Setup Wizard (the single guided setup flow).
/// Everything here is game-agnostic plumbing: game keys, the deterministic
/// wheel baseline, manual test force, and profile save/restore. No per-game
/// FFB logic lives here — each game's pipeline reads its own profile fields.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The string auto-load compares <see cref="FfbProfile.GameMatch"/>
    /// against (see OnCarModelDetected): the lowercase SupportedGame name,
    /// e.g. "acevo", "raceroom", "lemansultimate".</summary>
    public string CurrentGameMatchKey => SelectedGame is SupportedGame.None or SupportedGame.Unsupported
        ? ""
        : SelectedGame.ToString().ToLowerInvariant();

    /// <summary>Short game tag for profile names: <c>Game - CarType - Track</c>.</summary>
    public string GameShortName => SelectedGame switch
    {
        SupportedGame.AcEvo => "EVO",
        SupportedGame.Raceroom => "R3E",
        SupportedGame.AssettoCorsa => "AC",
        SupportedGame.LeMansUltimate => "LMU",
        SupportedGame.AssettoCorsaCompetizione => "ACC",
        _ => "All"
    };

    /// <summary>Manual game choice — same semantics as the header game pill
    /// (leaves auto mode so detection doesn't immediately override it).</summary>
    public void SelectGameManually(SupportedGame game)
    {
        if (game == SelectedGame) return;
        GameAutoMode = false;
        _gameDetectorManualOverride = true;
        SelectedGame = game;
    }

    /// <summary>Starts telemetry + force output (same as pressing START).</summary>
    public void StartTelemetryIfStopped()
    {
        if (IsRunning) return;
        IsRunning = true;
        ToggleTelemetry();
    }

    public string WizardCarClass => _telemetryLoop.DetectedCarClass ?? "";

    public bool IsTrueForceOutput => _telemetryLoop.ActiveProvider is LogitechTrueForceProvider;

    // ── Manual test force (shared by the Devices wheel test and the wizard) ──

    /// <summary>Sends a constant test force through the SAME output path the
    /// telemetry loop uses while driving (active provider when initialized,
    /// otherwise DirectInput with the device-level ForceInvert applied).
    /// Telemetry output is suppressed until <see cref="EndManualTestForce"/>.
    /// Positive force = wheel turns LEFT on correctly configured hardware.
    /// Returns an error message, or null on success.</summary>
    public string? BeginManualTestForce(float force, out bool viaTrueForce)
    {
        viaTrueForce = false;
        if (!IsDeviceConnected || _deviceManager == null || !_deviceManager.IsDeviceAcquired)
            return "Wheel not connected — connect a device first";

        // On the RS50/G PRO the TrueForce stream overrides DirectInput, so the
        // test must go through the stream to validate the path the app uses
        // while driving (a DI-only test would pass but prove nothing).
        if (_telemetryLoop.ActiveProvider is LogitechTrueForceProvider tf)
        {
            _telemetryLoop.SuppressOutput = true;
            // Lazy-engage: if the loop isn't running the session is in standby —
            // start it for the test, and EndManualTestForce releases it again.
            if (!_telemetryLoop.IsRunning) tf.Engage();
            tf.UpdateTorque(force);
            viaTrueForce = true;
            return null;
        }

        // The DI gate only applies to DI-force wheels: TrueForce wheels are
        // input-only at the DI layer, but their force rides the stream.
        if (!_deviceManager.SupportsPeriodicEffects)
            return "Device reports no force feedback support";

        _telemetryLoop.SuppressOutput = true;
        var provider = _telemetryLoop.ActiveProvider;
        if (provider != null && provider.IsInitialized)
            provider.UpdateTorque(force);
        else
            _deviceManager.SendConstantForce(force);
        return null;
    }

    public void EndManualTestForce()
    {
        if (_telemetryLoop.ActiveProvider is LogitechTrueForceProvider tf)
        {
            tf.UpdateTorque(0f);
            // Release the session back to standby unless the telemetry
            // loop is running (it re-engages via Start and needs the stream).
            if (!_telemetryLoop.IsRunning) tf.Disengage();
        }
        else
        {
            var provider = _telemetryLoop.ActiveProvider;
            if (provider != null && provider.IsInitialized) provider.ZeroTorque();
            _deviceManager?.SendConstantForce(0f);
        }
        _telemetryLoop.SuppressOutput = false;
    }

    // ── Baseline ──

    /// <summary>Builds the deterministic starting point for this wheel and
    /// applies it LIVE (pipeline + VM) without saving or selecting it. The
    /// wheel table supplies torque-scaled output gain and damping; game-
    /// specific fields keep their code defaults, which each game's pipeline
    /// reads on its own (pipeline isolation is preserved).</summary>
    public FfbProfile ApplyWizardBaseline(float wheelTorqueNm, bool forceInvert)
    {
        EvoDetectedSettings? evo = null;
        if (SelectedGame == SupportedGame.AcEvo && _telemetryLoop.LatestRaw is { } raw)
            evo = EvoSettingsDetector.DetectFromRaw(raw);

        string device = string.IsNullOrWhiteSpace(DeviceName) ? "Wheel" : DeviceName;
        // Only the steering lock is taken from the EVO detector — same as the
        // old Auto Setup, which deliberately did not apply its gain guesses.
        var baseline = WheelbaseAutoConfigurator.GenerateProfile(wheelTorqueNm, device, null);
        if (evo != null)
            baseline.SteeringLockDegrees = Math.Clamp(evo.RecommendedSteeringLock, 180, 1080);
        baseline.WheelMaxTorqueNm = wheelTorqueNm;
        baseline.ForceInvertEnabled = forceInvert;

        baseline.ApplyToPipeline(_pipeline);
        baseline.ApplyToStaticFriction(_telemetryLoop.StaticFriction);
        LoadProfileValues(baseline);
        // LoadProfileValues sets these from the profile; re-assert so the
        // device-level output matches the wizard's direction test result.
        WheelMaxTorqueNm = wheelTorqueNm;
        ForceInvertEnabled = forceInvert;
        return baseline;
    }

    // ── Save / restore ──

    /// <summary>Saves the live state as a profile with its auto-load scope
    /// wired correctly. General = no matches at all (a General profile with
    /// any match loads for nothing). Overwriting an existing user profile
    /// keeps a <c>*.json.bak</c> copy first. Returns an error or null.</summary>
    public string? WizardSaveProfile(string name, ProfileScope scope, string carMatch, string trackMatch)
    {
        var existing = _profileManager.Profiles.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing?.IsBuiltIn == true)
            return $"\"{name}\" is a built-in profile — choose another name.";

        if (existing != null)
        {
            try
            {
                string path = ProfileManager.GetProfileFilePath(existing.Name);
                if (File.Exists(path))
                    File.Copy(path, $"{path}.{DateTime.Now:yyyyMMdd_HHmmss}.bak", overwrite: false);
            }
            catch (Exception ex)
            {
                return $"Could not back up the existing profile: {ex.Message}";
            }
            name = existing.Name; // keep the on-disk casing
        }

        PushValuesToPipeline();
        var profile = _profileManager.SaveProfileFromPipeline(_pipeline, name);
        profile.Scope = scope;
        profile.GameMatch = scope == ProfileScope.General ? "" : CurrentGameMatchKey;
        profile.CarMatch = scope is ProfileScope.PerCar or ProfileScope.PerCarAndTrack ? carMatch : "";
        profile.TrackMatch = scope is ProfileScope.PerTrack or ProfileScope.PerCarAndTrack ? trackMatch : "";

        SaveNonPipelineSettings(profile);
        _profileManager.SaveProfile(profile);
        _profileManager.SetActiveProfile(profile);
        RefreshProfiles();
        SelectedProfile = Profiles.FirstOrDefault(p => p.Name == profile.Name) ?? profile;
        ProfileCarMatch = profile.CarMatch;
        ProfileTrackMatch = profile.TrackMatch;
        StatusText = $"Setup profile '{profile.Name}' saved and active";
        AddSystemLog($"SETUP WIZARD: saved '{profile.Name}' scope={scope} game='{profile.GameMatch}' car='{profile.CarMatch}' track='{profile.TrackMatch}'");
        return null;
    }

    /// <summary>Discards the wizard's live changes by re-applying the named
    /// profile from disk (the wizard never writes it, so disk is the truth).</summary>
    public void RestoreProfileAfterWizard(string? profileName)
    {
        if (string.IsNullOrEmpty(profileName)) return;
        var fresh = _profileManager.ReloadProfileFromDisk(profileName)
                    ?? _profileManager.Profiles.FirstOrDefault(p => p.Name == profileName);
        if (fresh == null) return;

        _applyingFreshProfile = true;
        try
        {
            RefreshProfiles();
            var item = Profiles.FirstOrDefault(p =>
                string.Equals(p.Name, fresh.Name, StringComparison.OrdinalIgnoreCase)) ?? fresh;
            SelectedProfile = item;
            ApplyProfileCore(item);
        }
        finally { _applyingFreshProfile = false; }
        StatusText = $"Setup Wizard closed — restored '{fresh.Name}'";
    }
}
