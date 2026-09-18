namespace AcEvoFfbTuner.Core.DirectInput;

public sealed class MozaMotorControl : IDisposable
{
    private bool _installed;
    private bool _disposed;
    private readonly List<string> _log = new();

    public bool IsAvailable => _installed;
    public string DiagnosticLog => string.Join("\n", _log);

    public bool Initialize(bool sdkAlreadyInstalled)
    {
        if (_installed) return true;

        if (!sdkAlreadyInstalled)
        {
            Log("MozaMotor: SDK not installed, skipping motor control");
            return false;
        }

        _installed = true;
        Log("MozaMotor: initialized (SDK already installed by LED controller)");
        return true;
    }

    public bool SetFfbStrength(int value0to100)
    {
        if (!_installed) return false;
        try
        {
            var err = mozaAPI.mozaAPI.setMotorFfbStrength(Clamp(value0to100, 0, 100));
            return err == mozaAPI.ERRORCODE.NORMAL;
        }
        catch (Exception ex)
        {
            Log($"MozaMotor: SetFfbStrength failed: {ex.Message}");
            return false;
        }
    }

    public bool SetSpringStrength(int value0to100)
    {
        if (!_installed) return false;
        try
        {
            var err = mozaAPI.mozaAPI.setMotorSpringStrength(Clamp(value0to100, 0, 100));
            return err == mozaAPI.ERRORCODE.NORMAL;
        }
        catch (Exception ex)
        {
            Log($"MozaMotor: SetSpringStrength failed: {ex.Message}");
            return false;
        }
    }

    public bool SetNaturalDamper(int value0to100)
    {
        if (!_installed) return false;
        try
        {
            var err = mozaAPI.mozaAPI.setMotorNaturalDamper(Clamp(value0to100, 0, 100));
            return err == mozaAPI.ERRORCODE.NORMAL;
        }
        catch (Exception ex)
        {
            Log($"MozaMotor: SetNaturalDamper failed: {ex.Message}");
            return false;
        }
    }

    public bool SetNaturalFriction(int value0to100)
    {
        if (!_installed) return false;
        try
        {
            var err = mozaAPI.mozaAPI.setMotorNaturalFriction(Clamp(value0to100, 0, 100));
            return err == mozaAPI.ERRORCODE.NORMAL;
        }
        catch (Exception ex)
        {
            Log($"MozaMotor: SetNaturalFriction failed: {ex.Message}");
            return false;
        }
    }

    public bool SetSpeedDamping(int value0to100)
    {
        if (!_installed) return false;
        try
        {
            var err = mozaAPI.mozaAPI.setMotorSpeedDamping(Clamp(value0to100, 0, 100));
            return err == mozaAPI.ERRORCODE.NORMAL;
        }
        catch (Exception ex)
        {
            Log($"MozaMotor: SetSpeedDamping failed: {ex.Message}");
            return false;
        }
    }

    public bool SetSpeedDampingStartPoint(int value0to400)
    {
        if (!_installed) return false;
        try
        {
            var err = mozaAPI.mozaAPI.setMotorSpeedDampingStartPoint(Clamp(value0to400, 0, 400));
            return err == mozaAPI.ERRORCODE.NORMAL;
        }
        catch (Exception ex)
        {
            Log($"MozaMotor: SetSpeedDampingStartPoint failed: {ex.Message}");
            return false;
        }
    }

    public bool SetPeakTorque(int value50to100)
    {
        if (!_installed) return false;
        try
        {
            var err = mozaAPI.mozaAPI.setMotorPeakTorque(Clamp(value50to100, 50, 100));
            return err == mozaAPI.ERRORCODE.NORMAL;
        }
        catch (Exception ex)
        {
            Log($"MozaMotor: SetPeakTorque failed: {ex.Message}");
            return false;
        }
    }

    public bool SetRoadSensitivity(int value0to10)
    {
        if (!_installed) return false;
        try
        {
            var err = mozaAPI.mozaAPI.setMotorRoadSensitivity(Clamp(value0to10, 0, 10));
            return err == mozaAPI.ERRORCODE.NORMAL;
        }
        catch (Exception ex)
        {
            Log($"MozaMotor: SetRoadSensitivity failed: {ex.Message}");
            return false;
        }
    }

    public bool SetLimitAngle(int limitAngle, int gameMaxAngle)
    {
        if (!_installed) return false;
        try
        {
            var err = mozaAPI.mozaAPI.setMotorLimitAngle(
                Clamp(limitAngle, 90, 2000),
                Clamp(gameMaxAngle, 90, limitAngle));
            return err == mozaAPI.ERRORCODE.NORMAL;
        }
        catch (Exception ex)
        {
            Log($"MozaMotor: SetLimitAngle failed: {ex.Message}");
            return false;
        }
    }

    public bool SetFfbReverse(bool reversed)
    {
        if (!_installed) return false;
        try
        {
            var err = mozaAPI.mozaAPI.setMotorFfbReverse(reversed ? 1 : 0);
            return err == mozaAPI.ERRORCODE.NORMAL;
        }
        catch (Exception ex)
        {
            Log($"MozaMotor: SetFfbReverse failed: {ex.Message}");
            return false;
        }
    }

    public bool SetHandsOffProtection(bool enabled)
    {
        if (!_installed) return false;
        try
        {
            var err = mozaAPI.mozaAPI.setMotorHandsOffProtection(enabled ? 1 : 0);
            return err == mozaAPI.ERRORCODE.NORMAL;
        }
        catch (Exception ex)
        {
            Log($"MozaMotor: SetHandsOffProtection failed: {ex.Message}");
            return false;
        }
    }

    public bool SetNaturalInertia(int value100to500)
    {
        if (!_installed) return false;
        try
        {
            var err = mozaAPI.mozaAPI.setMotorNaturalInertia(Clamp(value100to500, 100, 500));
            return err == mozaAPI.ERRORCODE.NORMAL;
        }
        catch (Exception ex)
        {
            Log($"MozaMotor: SetNaturalInertia failed: {ex.Message}");
            return false;
        }
    }

    public bool SetNaturalInertiaRatio(int value100to4000)
    {
        if (!_installed) return false;
        try
        {
            var err = mozaAPI.mozaAPI.setMotorNaturalInertiaRatio(Clamp(value100to4000, 100, 4000));
            return err == mozaAPI.ERRORCODE.NORMAL;
        }
        catch (Exception ex)
        {
            Log($"MozaMotor: SetNaturalInertiaRatio failed: {ex.Message}");
            return false;
        }
    }

    public bool SetLimitWheelSpeed(int value10to100)
    {
        if (!_installed) return false;
        try
        {
            var err = mozaAPI.mozaAPI.setMotorLimitWheelSpeed(Clamp(value10to100, 10, 100));
            return err == mozaAPI.ERRORCODE.NORMAL;
        }
        catch (Exception ex)
        {
            Log($"MozaMotor: SetLimitWheelSpeed failed: {ex.Message}");
            return false;
        }
    }

    public Dictionary<string, int>? GetEqualizerAmp()
    {
        if (!_installed) return null;
        try
        {
            var err = mozaAPI.ERRORCODE.NORMAL;
            return mozaAPI.mozaAPI.getMotorEqualizerAmp(ref err);
        }
        catch { return null; }
    }

    public bool SetEqualizerAmp(Dictionary<string, int> amp)
    {
        if (!_installed) return false;
        try
        {
            var err = mozaAPI.mozaAPI.setMotorEqualizerAmp(amp);
            return err == mozaAPI.ERRORCODE.NORMAL;
        }
        catch (Exception ex)
        {
            Log($"MozaMotor: SetEqualizerAmp failed: {ex.Message}");
            return false;
        }
    }

    public void SaveDiagnosticSnapshot()
    {
        if (!_installed) return;

        try
        {
            var err = mozaAPI.ERRORCODE.NORMAL;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== MOZA Motor Config Snapshot ===");

            sb.AppendLine($"FfbStrength: {mozaAPI.mozaAPI.getMotorFfbStrength(ref err)} (err={err})");
            sb.AppendLine($"SpringStrength: {mozaAPI.mozaAPI.getMotorSpringStrength(ref err)} (err={err})");
            sb.AppendLine($"NaturalDamper: {mozaAPI.mozaAPI.getMotorNaturalDamper(ref err)} (err={err})");
            sb.AppendLine($"NaturalFriction: {mozaAPI.mozaAPI.getMotorNaturalFriction(ref err)} (err={err})");
            sb.AppendLine($"SpeedDamping: {mozaAPI.mozaAPI.getMotorSpeedDamping(ref err)} (err={err})");
            sb.AppendLine($"SpeedDampingStartPoint: {mozaAPI.mozaAPI.getMotorSpeedDampingStartPoint(ref err)} (err={err})");
            sb.AppendLine($"PeakTorque: {mozaAPI.mozaAPI.getMotorPeakTorque(ref err)} (err={err})");
            sb.AppendLine($"RoadSensitivity: {mozaAPI.mozaAPI.getMotorRoadSensitivity(ref err)} (err={err})");
            sb.AppendLine($"LimitWheelSpeed: {mozaAPI.mozaAPI.getMotorLimitWheelSpeed(ref err)} (err={err})");
            sb.AppendLine($"NaturalInertia: {mozaAPI.mozaAPI.getMotorNaturalInertia(ref err)} (err={err})");
            sb.AppendLine($"NaturalInertiaRatio: {mozaAPI.mozaAPI.getMotorNaturalInertiaRatio(ref err)} (err={err})");
            sb.AppendLine($"FfbReverse: {mozaAPI.mozaAPI.getMotorFfbReverse(ref err)} (err={err})");
            sb.AppendLine($"HandsOffProtection: {mozaAPI.mozaAPI.getMotorHandsOffProtection(ref err)} (err={err})");

            var anglePair = mozaAPI.mozaAPI.getMotorLimitAngle(ref err);
            sb.AppendLine($"LimitAngle: {anglePair?.Item1}-{anglePair?.Item2} (err={err})");

            var eq = mozaAPI.mozaAPI.getMotorEqualizerAmp(ref err);
            if (eq != null)
            {
                foreach (var kv in eq)
                    sb.AppendLine($"EQ[{kv.Key}]: {kv.Value}");
            }

            string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AcEvoFfbTuner");
            Directory.CreateDirectory(logDir);
            File.WriteAllText(Path.Combine(logDir, "moza_motor_snapshot.txt"), sb.ToString());
            Log("MozaMotor: diagnostic snapshot saved");
        }
        catch (Exception ex)
        {
            Log($"MozaMotor: snapshot failed: {ex.Message}");
        }
    }

    private static int Clamp(int value, int min, int max) =>
        value < min ? min : value > max ? max : value;

    private void Log(string msg)
    {
        _log.Add(msg);
        try
        {
            string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AcEvoFfbTuner");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir, "moza_sdk.log"), $"{DateTime.Now:HH:mm:ss.fff} | {msg}\n");
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _installed = false;
        Log("MozaMotor: disposed");
    }
}
