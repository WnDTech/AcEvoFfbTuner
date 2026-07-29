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

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HapticPedalManager] Connect failed on {comPortName}: {ex.Message}");
            return false;
        }
    }

    public void SendHapticPacket(byte brakeIntensity, byte throttleIntensity)
    {
        if (!IsAvailable) return;

        byte[] packet = [HeaderByte, brakeIntensity, throttleIntensity, FooterByte];

        lock (_lock)
        {
            try
            {
                _serialPort!.Write(packet, 0, PacketLength);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HapticPedalManager] Send failed: {ex.Message}");
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
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[HapticPedalManager] Stop packet write failed: {ex.Message}");
                }

                try
                {
                    _serialPort.Close();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[HapticPedalManager] Close failed: {ex.Message}");
                }

                try
                {
                    _serialPort.Dispose();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[HapticPedalManager] Dispose failed: {ex.Message}");
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
