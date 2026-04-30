namespace AcEvoFfbTuner.Core.Profiles;

public enum WheelCharacteristics
{
    DirectDrive,
    BeltDriven,
    GearDriven
}

public static class WheelbaseAutoConfigurator
{
    public static WheelCharacteristics DetectWheelType(string deviceName)
    {
        if (string.IsNullOrEmpty(deviceName)) return WheelCharacteristics.BeltDriven;
        var n = deviceName.ToUpperInvariant();

        if (n.Contains("G29") || n.Contains("G920") || n.Contains("G27") || n.Contains("DFGT") || n.Contains("DRIVING FORCE"))
            return WheelCharacteristics.GearDriven;

        if (n.Contains("T150") || n.Contains("T300") || n.Contains("TX ") || n.Contains("TMX") || n.Contains("T248"))
            return WheelCharacteristics.BeltDriven;

        return WheelCharacteristics.DirectDrive;
    }

    public static FfbProfile GenerateProfile(float torqueNm, string deviceName, EvoDetectedSettings? evoSettings)
    {
        var wheelType = DetectWheelType(deviceName);
        bool isDD = wheelType == WheelCharacteristics.DirectDrive;

        float outputGain = Math.Clamp(1.7f / MathF.Sqrt(Math.Max(torqueNm, 1f)), 0.25f, 1.0f);
        float normScale = Math.Clamp(250f + MathF.Sqrt(Math.Max(torqueNm, 1f)) * 140f, 200f, 900f);
        float compPower = Math.Clamp(1.2f + torqueNm * 0.02f, 1.1f, 2.0f);
        float slewRate = isDD
            ? Math.Clamp(0.15f - torqueNm * 0.003f, 0.05f, 0.15f)
            : 0.20f;

        float speedDamp = isDD
            ? Math.Clamp(0.3f + torqueNm * 0.05f, 0.5f, 1.5f)
            : Math.Clamp(0.2f + torqueNm * 0.1f, 0.2f, 0.5f);
        float friction = isDD
            ? Math.Clamp(0.2f + torqueNm * 0.02f, 0.3f, 0.8f)
            : Math.Clamp(0.15f + torqueNm * 0.05f, 0.15f, 0.4f);
        float inertia = isDD
            ? Math.Clamp(0.1f + torqueNm * 0.01f, 0.15f, 0.4f)
            : Math.Clamp(0.05f + torqueNm * 0.02f, 0.05f, 0.15f);

        float centerSupp = 0.5f;  // Physics Mz preserved; only narrow zero-crossing fade needed
        float blendDeg = 0.5f;    // Reduced: Mz is primary signal, Fy blend zone minimal
        float hystThresh = isDD ? 0.02f : 0.01f;
        float noiseFloor = isDD ? 0.005f : 0.003f;
        float boost = isDD ? 3.0f : 2.0f;

        float vibMaster = Math.Clamp(0.6f - torqueNm * 0.015f, 0.15f, 0.6f);
        float suspRoadGain = Math.Clamp(1.2f - torqueNm * 0.03f, 0.3f, 1.2f);

        var profile = new FfbProfile
        {
            Name = $"Auto - {deviceName} ({torqueNm:F1}Nm)",
            OutputGain = outputGain,
            NormalizationScale = normScale,
            ForceScale = 1.0f,
            SoftClipThreshold = 0.8f,
            CompressionPower = compPower,
            SignCorrectionEnabled = true,
            WheelMaxTorqueNm = torqueNm,
            MzFront = new ChannelConfig { Gain = 1.0f, Enabled = true },
            FxFront = new ChannelConfig { Gain = 0.12f, Enabled = true },
            FyFront = new ChannelConfig { Gain = 0.15f, Enabled = true },
            MzRear = new ChannelConfig { Gain = 0.0f, Enabled = false },
            FxRear = new ChannelConfig { Gain = 0.0f, Enabled = false },
            FyRear = new ChannelConfig { Gain = 0.0f, Enabled = false },
            FinalFf = new ChannelConfig { Gain = 0.0f, Enabled = false },
            WheelLoadWeighting = 0.0f,
            MzScale = 30f,
            FxScale = 500f,
            FyScale = 5000f,
            LutCurve = LutCurveDto.Linear(),
            SteeringLockDegrees = 900,
            Damping = new DampingConfig
            {
                SpeedDamping = speedDamp,
                Friction = friction,
                Inertia = inertia,
                MaxSpeedReference = 200f,
                LowSpeedDampingBoost = boost,
                LowSpeedThreshold = 20f
            },
            Slip = new SlipConfig
            {
                SlipRatioGain = 0.08f,
                SlipAngleGain = 0.15f,
                SlipThreshold = 0.05f,
                UseFrontOnly = true
            },
            Dynamic = new DynamicConfig
            {
                LateralGGain = 0f,
                LongitudinalGGain = 0f,
                SuspensionGain = 0.3f,
                YawRateGain = 0f
            },
            AutoGain = new AutoGainConfig { Enabled = false, Scale = 1.0f },
            Vibrations = new VibrationConfig
            {
                KerbGain = 0.8f,
                SlipGain = 0.6f,
                RoadGain = 0.4f,
                AbsGain = 0.8f,
                MasterGain = vibMaster,
                SuspensionRoadGain = suspRoadGain
            },
            Advanced = new AdvancedConfig
            {
                MaxSlewRate = slewRate,
                CenterSuppressionDegrees = centerSupp,
                CenterKneePower = 1.0f,
                HysteresisThreshold = hystThresh,
                NoiseFloor = noiseFloor,
                HysteresisWatchdogFrames = 5,
                CenterBlendDegrees = blendDeg,
                SteerVelocityReference = 10.0f,
                VelocityDeadzone = 0.05f,
                LowSpeedSmoothKmh = 10.0f
            }
        };

        if (evoSettings != null && evoSettings.IsValid)
        {
            EvoSettingsDetector.ApplyToProfile(profile, evoSettings);
        }

        return profile;
    }
}
