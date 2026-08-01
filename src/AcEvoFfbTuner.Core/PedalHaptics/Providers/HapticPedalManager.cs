using System.IO.Ports;

namespace AcEvoFfbTuner.Core.PedalHaptics.Providers;

public sealed class HapticPedalManager : IPedalHapticProvider
{
    private const byte HeaderByte = 0xFF;
    private const byte FooterByte = 0xEE;
    private const int PacketLength = 4;
    private const int DefaultBaudRate = 115200;

    private SerialPort? _serialPort;
    private readonly object _lock = new();
    private bool _disposed;

    public string DeviceName => _serialPort?.PortName ?? "Osoyoo Uno (disconnected)";
    public bool IsAvailable => _serialPort?.IsOpen == true && !_disposed;
    public bool IsBrakeSupported => true;
    public bool IsGasSupported => true;
    public bool IsClutchSupported => false;

    private static readonly string SerialLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "osoyoo_serial_log.txt");

    private static void LogSerial(string entry)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {entry}";
            File.AppendAllText(SerialLogPath, line + Environment.NewLine);
            System.Diagnostics.Debug.WriteLine(line);
        }
        catch { }
    }

    public bool ConnectToPedal(string comPortName)
    {
        Disconnect();

        try
        {
            var port = new SerialPort(comPortName, DefaultBaudRate, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 500,
                WriteTimeout = 500,
                DtrEnable = false,
                RtsEnable = false
            };

            port.Open();

            lock (_lock)
            {
                _serialPort = port;
            }

            LogSerial($"CONNECT OK on {comPortName} (115200 8N1, IsOpen={port.IsOpen})");
            return true;
        }
        catch (Exception ex)
        {
            LogSerial($"CONNECT FAILED on {comPortName}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public void SendHapticPacket(byte brakeIntensity, byte throttleIntensity)
    {
        if (!IsAvailable)
        {
            LogSerial($"SEND SKIPPED (not available): brake={brakeIntensity} gas={throttleIntensity} port={_serialPort?.PortName ?? "null"} isOpen={_serialPort?.IsOpen}");
            return;
        }

        byte[] packet = [HeaderByte, brakeIntensity, throttleIntensity, FooterByte];

        lock (_lock)
        {
            try
            {
                _serialPort!.Write(packet, 0, PacketLength);
                LogSerial($"SEND OK: brake={brakeIntensity} gas={throttleIntensity} bytes={BitConverter.ToString(packet)}");
            }
            catch (Exception ex)
            {
                LogSerial($"SEND FAILED: brake={brakeIntensity} gas={throttleIntensity} bytes={BitConverter.ToString(packet)} — {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    public void SetBrakeHaptic(float intensity, HapticSignalType signal)
    {
        if (!IsAvailable) return;
        byte brake = (byte)Math.Clamp((int)(intensity * 255f), 0, 255);
        SendHapticPacket(brake, 0);
    }

    public void SetGasHaptic(float intensity, HapticSignalType signal)
    {
        if (!IsAvailable) return;
        byte gas = (byte)Math.Clamp((int)(intensity * 255f), 0, 255);
        SendHapticPacket(0, gas);
    }

    public void SetClutchHaptic(float intensity, HapticSignalType signal) { }

    public void StopAll()
    {
        SendHapticPacket(0, 0);
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            if (_serialPort is { IsOpen: true })
            {
                try
                {
                    byte[] stopPacket = [HeaderByte, 0, 0, FooterByte];
                    _serialPort.Write(stopPacket, 0, PacketLength);
                    LogSerial($"DISCONNECT: stop packet sent ({BitConverter.ToString(stopPacket)})");
                }
                catch (Exception ex)
                {
                    LogSerial($"DISCONNECT: stop packet failed — {ex.GetType().Name}: {ex.Message}");
                }

                try
                {
                    _serialPort.Close();
                    LogSerial($"DISCONNECT: port {_serialPort.PortName} closed");
                }
                catch (Exception ex)
                {
                    LogSerial($"DISCONNECT: close failed — {ex.GetType().Name}: {ex.Message}");
                }

                try
                {
                    _serialPort.Dispose();
                }
                catch (Exception ex)
                {
                    LogSerial($"DISCONNECT: dispose failed — {ex.GetType().Name}: {ex.Message}");
                }
            }

            _serialPort = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }
}
