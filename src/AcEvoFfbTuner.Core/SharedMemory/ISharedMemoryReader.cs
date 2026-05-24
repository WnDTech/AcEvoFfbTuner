using AcEvoFfbTuner.Core.SharedMemory.Structs;

namespace AcEvoFfbTuner.Core.SharedMemory;

public interface ISharedMemoryReader : IDisposable
{
    bool IsConnected { get; }
    event Action? GameConnected;
    event Action? GameDisconnected;
    bool TryConnect();
    void Disconnect();
    bool TryReadPhysics(out SPageFilePhysicsEvo physics);
    bool TryReadGraphics(out SPageFileGraphicEvo graphics);
    bool TryReadStatic(out SPageFileStaticEvo staticData);
}
