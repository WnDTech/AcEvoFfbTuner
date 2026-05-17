using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AcEvoFfbTuner.Services;

public sealed class GameRecordingService : IDisposable
{
    private static readonly string RecordingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "recordings");

    private static readonly string BundledFFmpegPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");

    private string? _currentOutputPath;
    private string? _audioTempPath;
    private string? _videoOnlyPath;
    private DateTime _recordingStartTime;
    private bool _isRecording;
    private bool _disposed;

    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _captureSession;
    private GraphicsCaptureItem? _captureItem;
    private IDirect3DDevice? _winrtDevice;
    private IntPtr _d3dDeviceNative;
    private IntPtr _dxgiDeviceNative;
    private IntPtr _d3dContextNative;
    private IntPtr _stagingTexture;

    private Process? _ffmpegProcess;
    private int _videoWidth;
    private int _videoHeight;
    private const int TargetFps = 30;
    private static readonly TimeSpan FrameInterval = TimeSpan.FromSeconds(1.0 / TargetFps);
    private long _framesWritten;
    private DateTime _lastFrameTime;
    private readonly object _frameLock = new();
    private volatile bool _captureActive;

    private WasapiLoopbackCapture? _audioCapture;
    private WaveFileWriter? _audioWriter;

    private Window? _overlayWindow;
    private int _stoppedTickCount;
    private const int StopAfterTicks = 90;
    private static string? _cachedEncoder;

    public bool IsRecording => _isRecording;
    public string? CurrentOutputPath => _currentOutputPath;
    public DateTime RecordingStartTime => _recordingStartTime;
    public static string RecordingsDirectory => RecordingsDir;

    public event Action<string, bool>? RecordingStateChanged;

    public void OnTelemetryTick(float speedKmh)
    {
        if (_disposed || !_isRecording) return;

        if (speedKmh <= 5.0f)
        {
            _stoppedTickCount++;
            if (_stoppedTickCount >= StopAfterTicks)
                StopRecording();
        }
        else
        {
            _stoppedTickCount = 0;
        }
    }

    public void StartRecording()
    {
        if (_isRecording || _disposed) return;

        try
        {
            if (!GraphicsCaptureSession.IsSupported())
            {
                RecordingStateChanged?.Invoke("Recording unavailable — Windows.Graphics.Capture not supported (needs Win10 1903+)", false);
                return;
            }

            IntPtr hWnd = FindAcevoWindow();
            if (hWnd == IntPtr.Zero)
            {
                RecordingStateChanged?.Invoke("Recording unavailable — AC EVO window not found. Start the game first.", false);
                return;
            }

            string? ffmpegPath = FindFFmpeg();
            if (ffmpegPath == null)
            {
                RecordingStateChanged?.Invoke("Recording unavailable — ffmpeg.exe not found", false);
                return;
            }

            Directory.CreateDirectory(RecordingsDir);
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            _videoOnlyPath = Path.Combine(RecordingsDir, $"acevo_session_{ts}_video.mp4");
            _audioTempPath = Path.Combine(RecordingsDir, $"acevo_session_{ts}_audio.wav");
            _currentOutputPath = Path.Combine(RecordingsDir, $"acevo_session_{ts}.mp4");
            _recordingStartTime = DateTime.Now;
            _stoppedTickCount = 0;
            _framesWritten = 0;
            _captureActive = false;

            if (!CreateD3D11DeviceAndContext(out _d3dDeviceNative, out _dxgiDeviceNative, out _d3dContextNative))
            {
                RecordingStateChanged?.Invoke("Recording failed: could not create D3D11 device", false);
                return;
            }

            _winrtDevice = CreateWinRTDeviceFromDXGI(_dxgiDeviceNative);

            _captureItem = CreateCaptureItemForWindow(hWnd);
            if (_captureItem == null)
            {
                CleanupD3D();
                RecordingStateChanged?.Invoke("Recording failed: could not create capture item for AC EVO window", false);
                return;
            }

            _videoWidth = _captureItem.Size.Width;
            _videoHeight = _captureItem.Size.Height;

            if (!CreateStagingTexture(_videoWidth, _videoHeight))
            {
                CleanupD3D();
                RecordingStateChanged?.Invoke("Recording failed: could not create staging texture", false);
                return;
            }

            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _winrtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                _captureItem.Size);

            _captureSession = _framePool.CreateCaptureSession(_captureItem);
            _captureSession.IsCursorCaptureEnabled = false;

            string encoder = GetBestEncoder(ffmpegPath);
            StartFFmpeg(ffmpegPath, encoder);

            System.Threading.Thread.Sleep(200);
            if (_ffmpegProcess == null || _ffmpegProcess.HasExited)
            {
                CleanupStagingTexture();
                CleanupCapture();
                CleanupD3D();
                RecordingStateChanged?.Invoke("Recording failed: FFmpeg exited immediately", false);
                return;
            }

            _captureActive = true;
            _lastFrameTime = DateTime.MinValue;
            _framePool.FrameArrived += OnFrameArrived;
            _captureSession.StartCapture();

            StartAudioCapture();

            _isRecording = true;
            ShowOverlay();
            RecordingStateChanged?.Invoke(
                $"Recording AC EVO (WinRT/{_videoWidth}x{_videoHeight}/{encoder}): {Path.GetFileName(_currentOutputPath)}", true);
        }
        catch (Exception ex)
        {
            StopAudioCapture();
            CleanupStagingTexture();
            CleanupCapture();
            CleanupD3D();
            RecordingStateChanged?.Invoke($"Recording failed: {ex.Message}", false);
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (!_captureActive) return;

        try
        {
            var now = DateTime.UtcNow;
            if (_lastFrameTime != DateTime.MinValue && now - _lastFrameTime < FrameInterval)
                return;
            _lastFrameTime = now;

            using Direct3D11CaptureFrame? frame = sender.TryGetNextFrame();
            if (frame == null) return;

            int w = frame.ContentSize.Width;
            int h = frame.ContentSize.Height;
            if (w <= 0 || h <= 0) return;

            byte[]? pixels = CopyFrameToCPU(frame, w, h);
            if (pixels == null) return;

            lock (_frameLock)
            {
                if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
                {
                    try
                    {
                        _ffmpegProcess.StandardInput.BaseStream.Write(pixels, 0, pixels.Length);
                        _ffmpegProcess.StandardInput.BaseStream.Flush();
                        _framesWritten++;
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    private byte[]? CopyFrameToCPU(Direct3D11CaptureFrame frame, int width, int height)
    {
        if (_stagingTexture == IntPtr.Zero || _d3dContextNative == IntPtr.Zero)
            return null;

        try
        {
            var dxgiAccess = (IDirect3DDxgiInterfaceAccess)frame.Surface;
            Guid texture2DGuid = new("6f15aaf2-d208-4e89-9ab4-489595d4444c"); // ID3D11Texture2D
            IntPtr srcTexturePtr = dxgiAccess.GetInterface(ref texture2DGuid);
            if (srcTexturePtr == IntPtr.Zero) return null;

            try
            {
                D3D11Vtable.CopyResource(_d3dContextNative, _stagingTexture, srcTexturePtr);

                var mapped = new D3D11_MAPPED_SUBRESOURCE();
                int hr = D3D11Vtable.Map(_d3dContextNative, _stagingTexture, 0, 1, 0, ref mapped);
                if (hr < 0) return null;

                try
                {
                    int srcPitch = (int)mapped.RowPitch;
                    int destPitch = width * 4;
                    byte[] pixels = new byte[destPitch * height];

                    unsafe
                    {
                        byte* src = (byte*)mapped.pData;
                        fixed (byte* dst = pixels)
                        {
                            for (int y = 0; y < height; y++)
                            {
                                Buffer.MemoryCopy(src + y * srcPitch, dst + y * destPitch, destPitch, destPitch);
                            }
                        }
                    }

                    return pixels;
                }
                finally
                {
                    D3D11Vtable.Unmap(_d3dContextNative, _stagingTexture, 0);
                }
            }
            finally
            {
                Marshal.Release(srcTexturePtr);
            }
        }
        catch
        {
            return null;
        }
    }

    private bool CreateStagingTexture(int width, int height)
    {
        try
        {
            var desc = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = 87, // DXGI_FORMAT_B8G8R8A8_UNORM
                SampleDesc_Count = 1,
                SampleDesc_Quality = 0,
                Usage = 3, // D3D11_USAGE_STAGING
                BindFlags = 0,
                CPUAccessFlags = 0x10000, // D3D11_CPU_ACCESS_READ
                MiscFlags = 0
            };

            IntPtr texture;
            int hr = D3D11Vtable.CreateTexture2D(_d3dDeviceNative, ref desc, IntPtr.Zero, out texture);
            if (hr < 0) return false;

            _stagingTexture = texture;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void CleanupStagingTexture()
    {
        if (_stagingTexture != IntPtr.Zero)
        {
            Marshal.Release(_stagingTexture);
            _stagingTexture = IntPtr.Zero;
        }
    }

    private void StartFFmpeg(string ffmpegPath, string encoder)
    {
        string args = BuildFFmpegArgs(encoder, _videoWidth, _videoHeight, _videoOnlyPath!);

        _ffmpegProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            },
            EnableRaisingEvents = true
        };
        _ffmpegProcess.ErrorDataReceived += (s, e) => { };
        _ffmpegProcess.Start();
        _ffmpegProcess.BeginErrorReadLine();
    }

    private static string BuildFFmpegArgs(string encoder, int width, int height, string outputPath)
    {
        var sb = new StringBuilder();
        sb.Append("-y -hide_banner -loglevel warning ");
        sb.Append($"-f rawvideo -pix_fmt bgra -s {width}x{height} -r {TargetFps} -i - ");
        sb.Append($"-c:v {encoder} ");
        if (encoder == "libx264")
            sb.Append("-preset ultrafast -crf 23 ");
        else
            sb.Append("-b:v 15M ");
        sb.Append("-an ");
        sb.Append($"\"{outputPath}\"");
        return sb.ToString();
    }

    private static string GetBestEncoder(string ffmpegPath)
    {
        if (_cachedEncoder != null)
            return _cachedEncoder;

        string[] encoders = { "h264_amf", "h264_mf", "h264_nvenc", "h264_qsv", "libx264" };

        foreach (var enc in encoders)
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-hide_banner -loglevel error -f lavfi -i nullsrc=s=256x256:d=0.1 -c:v {enc} -t 0.2 -y -f null -",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                });
                proc?.WaitForExit(10000);
                if (proc?.ExitCode == 0)
                {
                    _cachedEncoder = enc;
                    return enc;
                }
            }
            catch { }
        }

        _cachedEncoder = "libx264";
        return _cachedEncoder;
    }

    public string? StopRecording()
    {
        if (!_isRecording) return null;

        _isRecording = false;
        _captureActive = false;
        _stoppedTickCount = 0;
        HideOverlay();

        try { _captureSession?.Dispose(); } catch { }
        if (_framePool != null)
            _framePool.FrameArrived -= OnFrameArrived;

        System.Threading.Thread.Sleep(100);
        StopAudioCapture();

        lock (_frameLock)
        {
            try
            {
                if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
                {
                    _ffmpegProcess.StandardInput.BaseStream.Flush();
                    _ffmpegProcess.StandardInput.Close();
                }
            }
            catch { }
        }

        _ = Task.Run(() =>
        {
            try
            {
                if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
                    _ffmpegProcess.WaitForExit(15000);
            }
            catch { }

            CleanupFFmpeg();
            CleanupStagingTexture();
            CleanupCapture();
            CleanupD3D();

            string? muxedPath = MuxAudioVideo();
            CleanupTempFiles();

            string? finalPath = muxedPath ?? _currentOutputPath;
            if (finalPath != null && File.Exists(finalPath) && new FileInfo(finalPath).Length > 0)
                RecordingStateChanged?.Invoke($"Recording saved: {Path.GetFileName(finalPath)}", false);
            else
                RecordingStateChanged?.Invoke("Recording saved (video may be empty)", false);
        });

        RecordingStateChanged?.Invoke("Recording stopped — finalizing...", false);
        return _currentOutputPath;
    }

    private string? MuxAudioVideo()
    {
        if (_videoOnlyPath == null || !File.Exists(_videoOnlyPath) || new FileInfo(_videoOnlyPath).Length == 0)
            return null;

        bool hasAudio = _audioTempPath != null && File.Exists(_audioTempPath) && new FileInfo(_audioTempPath).Length > 44;

        if (!hasAudio)
        {
            try { File.Move(_videoOnlyPath, _currentOutputPath!, overwrite: true); }
            catch { try { File.Copy(_videoOnlyPath, _currentOutputPath!, true); } catch { } }
            return _currentOutputPath;
        }

        string? ffmpegPath = FindFFmpeg();
        if (ffmpegPath == null)
        {
            try { File.Copy(_videoOnlyPath, _currentOutputPath!, true); } catch { }
            return _currentOutputPath;
        }

        try
        {
            string muxArgs = $"-y -hide_banner -loglevel error " +
                             $"-i \"{_videoOnlyPath}\" -i \"{_audioTempPath}\" " +
                             $"-c:v copy -c:a aac -b:a 192k -shortest \"{_currentOutputPath}\"";

            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = muxArgs,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            });
            proc?.WaitForExit(30000);

            if (proc?.ExitCode == 0 && _currentOutputPath != null &&
                File.Exists(_currentOutputPath) && new FileInfo(_currentOutputPath).Length > 0)
                return _currentOutputPath;
        }
        catch { }

        try { File.Copy(_videoOnlyPath, _currentOutputPath!, true); } catch { }
        return _currentOutputPath;
    }

    private void CleanupTempFiles()
    {
        try { if (_videoOnlyPath != null && File.Exists(_videoOnlyPath)) File.Delete(_videoOnlyPath); } catch { }
        try { if (_audioTempPath != null && File.Exists(_audioTempPath)) File.Delete(_audioTempPath); } catch { }
        _videoOnlyPath = null;
        _audioTempPath = null;
    }

    private void CleanupFFmpeg()
    {
        if (_ffmpegProcess != null)
        {
            try
            {
                if (!_ffmpegProcess.HasExited)
                    try { _ffmpegProcess.Kill(); } catch { }
                _ffmpegProcess.Dispose();
            }
            catch { }
            _ffmpegProcess = null;
        }
    }

    private void CleanupCapture()
    {
        try { _framePool?.Dispose(); } catch { }
        _framePool = null;

        try { _captureSession?.Dispose(); } catch { }
        _captureSession = null;

        _captureItem = null;
        _winrtDevice = null;
    }

    private void CleanupD3D()
    {
        if (_d3dContextNative != IntPtr.Zero)
        {
            Marshal.Release(_d3dContextNative);
            _d3dContextNative = IntPtr.Zero;
        }
        if (_d3dDeviceNative != IntPtr.Zero)
        {
            Marshal.Release(_d3dDeviceNative);
            _d3dDeviceNative = IntPtr.Zero;
        }
        if (_dxgiDeviceNative != IntPtr.Zero)
        {
            Marshal.Release(_dxgiDeviceNative);
            _dxgiDeviceNative = IntPtr.Zero;
        }
    }

    private void StartAudioCapture()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _audioCapture = new WasapiLoopbackCapture(device);
            _audioWriter = new WaveFileWriter(_audioTempPath, _audioCapture.WaveFormat);

            _audioCapture.DataAvailable += (s, e) =>
            {
                try { _audioWriter?.Write(e.Buffer, 0, e.BytesRecorded); }
                catch { }
            };

            _audioCapture.RecordingStopped += (s, e) =>
            {
                try { _audioWriter?.Flush(); } catch { }
                try { _audioWriter?.Dispose(); } catch { }
                _audioWriter = null;
            };

            _audioCapture.StartRecording();
        }
        catch
        {
            _audioTempPath = null;
        }
    }

    private void StopAudioCapture()
    {
        try
        {
            if (_audioCapture != null && _audioCapture.CaptureState != CaptureState.Stopped)
                _audioCapture.StopRecording();
        }
        catch { }

        try { _audioCapture?.Dispose(); } catch { }
        _audioCapture = null;
    }

    #region Window & Capture Item Helpers

    private static IntPtr FindAcevoWindow()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, lParam) =>
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                if (proc.ProcessName.Equals("AC2", StringComparison.OrdinalIgnoreCase) ||
                    proc.ProcessName.Equals("acevo", StringComparison.OrdinalIgnoreCase) ||
                    proc.ProcessName.Equals("AssettoCorsaEvo", StringComparison.OrdinalIgnoreCase))
                {
                    if (IsWindowVisible(hWnd) && GetWindow(hWnd, GW_OWNER) == IntPtr.Zero)
                    {
                        found = hWnd;
                        return false;
                    }
                }
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static GraphicsCaptureItem? CreateCaptureItemForWindow(IntPtr hWnd)
    {
        try
        {
            Guid interopGuid = typeof(IGraphicsCaptureItemInterop).GUID;
            RoGetActivationFactory("Windows.Graphics.Capture.GraphicsCaptureItem", ref interopGuid, out object factory);
            var interop = (IGraphicsCaptureItemInterop)factory;
            interop.CreateForWindow(hWnd, typeof(GraphicsCaptureItem).GUID, out object raw);
            return (GraphicsCaptureItem)raw;
        }
        catch
        {
            return null;
        }
    }

    private static bool CreateD3D11DeviceAndContext(out IntPtr device, out IntPtr dxgiDevice, out IntPtr context)
    {
        device = IntPtr.Zero;
        dxgiDevice = IntPtr.Zero;
        context = IntPtr.Zero;

        try
        {
            int[] driverTypes = { 1, 5 }; // HARDWARE, WARP
            uint flags = 0x20; // BGRA_SUPPORT

            foreach (var dt in driverTypes)
            {
                IntPtr dev = IntPtr.Zero;
                IntPtr ctx = IntPtr.Zero;

                int hr = D3D11CreateDevice(
                    IntPtr.Zero, dt, IntPtr.Zero, flags,
                    IntPtr.Zero, 0, 7,
                    ref dev, IntPtr.Zero, ref ctx);

                if (hr >= 0 && dev != IntPtr.Zero)
                {
                    device = dev;
                    context = ctx;

                    Guid dxgiGuid = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
                    int qiHr = Marshal.QueryInterface(dev, ref dxgiGuid, out IntPtr dxgi);
                    if (qiHr >= 0)
                        dxgiDevice = dxgi;

                    return dxgiDevice != IntPtr.Zero;
                }
            }
        }
        catch { }

        return false;
    }

    private static IDirect3DDevice CreateWinRTDeviceFromDXGI(IntPtr dxgiDevice)
    {
        CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out object device);
        return (IDirect3DDevice)device;
    }

    #endregion

    #region P/Invoke

    private const int GW_OWNER = 4;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, int uCmd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice")]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter, int DriverType, IntPtr Software, uint Flags,
        IntPtr pFeatureLevels, uint FeatureLevels, uint SDKVersion,
        ref IntPtr ppDevice, IntPtr pFeatureLevel, ref IntPtr ppImmediateContext);

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice, [Out, MarshalAs(UnmanagedType.IUnknown)] out object graphicsDevice);

    [DllImport("combase.dll", PreserveSig = false)]
    private static extern void RoGetActivationFactory(
        [MarshalAs(UnmanagedType.HString)] string activatableClassId,
        [In] ref Guid iid,
        [Out, MarshalAs(UnmanagedType.IUnknown)] out object factory);

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [ComVisible(true)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(
            [In] IntPtr window,
            [In] ref Guid riid,
            [Out, MarshalAs(UnmanagedType.IUnknown)] out object result);

        IntPtr CreateForMonitor(
            [In] IntPtr monitor,
            [In] ref Guid riid,
            [Out, MarshalAs(UnmanagedType.IUnknown)] out object result);
    }

    [ComImport]
    [Guid("A9B3D012-31C8-4A25-9E8E-3DA1C4FBB0A5")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [ComVisible(true)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface(ref Guid riid);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_TEXTURE2D_DESC
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public int Format;
        public uint SampleDesc_Count;
        public uint SampleDesc_Quality;
        public int Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_MAPPED_SUBRESOURCE
    {
        public IntPtr pData;
        public uint RowPitch;
        public uint DepthPitch;
    }

    private static class D3D11Vtable
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateTexture2DFn(IntPtr self, ref D3D11_TEXTURE2D_DESC desc, IntPtr initData, out IntPtr texture);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int MapFn(IntPtr self, IntPtr resource, uint subresource, int mapType, uint mapFlags, ref D3D11_MAPPED_SUBRESOURCE mapped);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void UnmapFn(IntPtr self, IntPtr resource, uint subresource);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void CopyResourceFn(IntPtr self, IntPtr dst, IntPtr src);

        public static int CreateTexture2D(IntPtr device, ref D3D11_TEXTURE2D_DESC desc, IntPtr initData, out IntPtr texture)
        {
            IntPtr vtable = Marshal.ReadIntPtr(device);
            IntPtr fnPtr = Marshal.ReadIntPtr(vtable, 5 * IntPtr.Size);
            var fn = Marshal.GetDelegateForFunctionPointer<CreateTexture2DFn>(fnPtr);
            return fn(device, ref desc, initData, out texture);
        }

        public static void CopyResource(IntPtr context, IntPtr dst, IntPtr src)
        {
            IntPtr vtable = Marshal.ReadIntPtr(context);
            IntPtr fnPtr = Marshal.ReadIntPtr(vtable, 47 * IntPtr.Size);
            var fn = Marshal.GetDelegateForFunctionPointer<CopyResourceFn>(fnPtr);
            fn(context, dst, src);
        }

        public static int Map(IntPtr context, IntPtr resource, uint subresource, int mapType, uint mapFlags, ref D3D11_MAPPED_SUBRESOURCE mapped)
        {
            IntPtr vtable = Marshal.ReadIntPtr(context);
            IntPtr fnPtr = Marshal.ReadIntPtr(vtable, 14 * IntPtr.Size);
            var fn = Marshal.GetDelegateForFunctionPointer<MapFn>(fnPtr);
            return fn(context, resource, subresource, mapType, mapFlags, ref mapped);
        }

        public static void Unmap(IntPtr context, IntPtr resource, uint subresource)
        {
            IntPtr vtable = Marshal.ReadIntPtr(context);
            IntPtr fnPtr = Marshal.ReadIntPtr(vtable, 15 * IntPtr.Size);
            var fn = Marshal.GetDelegateForFunctionPointer<UnmapFn>(fnPtr);
            fn(context, resource, subresource);
        }
    }

    #endregion

    #region Overlay

    private void ShowOverlay()
    {
        try
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (_overlayWindow != null) return;
                _overlayWindow = new Window
                {
                    Width = 300,
                    Height = 36,
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    Topmost = true,
                    AllowsTransparency = true,
                    Background = null,
                    IsHitTestVisible = false,
                    Foreground = System.Windows.Media.Brushes.Transparent,
                    Title = "",
                    Content = new System.Windows.Controls.Border
                    {
                        Background = new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromArgb(180, 0, 0, 0)),
                        CornerRadius = new System.Windows.CornerRadius(4),
                        Padding = new System.Windows.Thickness(10, 6, 10, 6),
                        Child = new System.Windows.Controls.TextBlock
                        {
                            Text = "\u25CF Recording Telemetry Session",
                            Foreground = new System.Windows.Media.SolidColorBrush(
                                System.Windows.Media.Color.FromRgb(229, 57, 53)),
                            FontSize = 14,
                            FontWeight = System.Windows.FontWeights.Bold,
                            FontFamily = new System.Windows.Media.FontFamily("Segoe UI")
                        }
                    }
                };
                _overlayWindow.Left = 12;
                _overlayWindow.Top = 12;
                _overlayWindow.Show();
            });
        }
        catch { }
    }

    private void HideOverlay()
    {
        try
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (_overlayWindow == null) return;
                _overlayWindow.Close();
                _overlayWindow = null;
            });
        }
        catch { }
    }

    #endregion

    #region FFmpeg Discovery

    private static string? FindFFmpeg()
    {
        if (File.Exists(BundledFFmpegPath))
            return BundledFFmpegPath;

        string? downloaded = FfmpegDownloader.FfmpegExePath;
        if (downloaded != null)
            return downloaded;

        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            });
            if (proc != null)
            {
                proc.WaitForExit(3000);
                if (proc.ExitCode == 0)
                    return "ffmpeg";
            }
        }
        catch { }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] commonPaths =
        {
            Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "ffmpeg.exe"),
            @"C:\ffmpeg\bin\ffmpeg.exe",
            Path.Combine(localAppData, "Programs", "ffmpeg", "bin", "ffmpeg.exe"),
            @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
            @"C:\Program Files\FFmpeg\bin\ffmpeg.exe",
            @"C:\ProgramData\chocolatey\bin\ffmpeg.exe"
        };

        foreach (var p in commonPaths)
            if (File.Exists(p))
                return p;

        string wingetPackages = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(wingetPackages))
        {
            try
            {
                var found = Directory.GetFiles(wingetPackages, "ffmpeg.exe", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (found != null)
                    return found;
            }
            catch { }
        }

        return null;
    }

    #endregion

    #region Upload

    public static async Task<string?> UploadRecordingAsync(string filePath, IProgress<string>? progress = null)
    {
        if (!File.Exists(filePath)) return null;

        var fileSizeMb = new FileInfo(filePath).Length / (1024.0 * 1024.0);
        progress?.Report($"Uploading video ({fileSizeMb:F0} MB) to gofile...");
        try
        {
            string? link = await UploadToGoFile(filePath, progress);
            if (link != null)
            {
                progress?.Report($"Video uploaded: {link}");
                return link;
            }
        }
        catch (Exception ex)
        {
            progress?.Report($"Gofile upload failed: {ex.Message}, trying litterbox...");
        }

        progress?.Report($"Uploading video ({fileSizeMb:F0} MB) to litterbox...");
        try
        {
            string? link = await UploadToLitterbox(filePath, progress);
            if (link != null)
            {
                progress?.Report($"Video uploaded: {link}");
                return link;
            }
        }
        catch (Exception ex)
        {
            progress?.Report($"Litterbox upload failed: {ex.Message}");
        }

        return null;
    }

    private static async Task<string?> UploadToGoFile(string filePath, IProgress<string>? progress)
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        await using var fileStream = File.OpenRead(filePath);
        var fileSize = fileStream.Length;
        using var progressStream = new ProgressStream(fileStream, bytesRead =>
        {
            var pct = (int)(bytesRead * 100 / fileSize);
            progress?.Report($"Uploading to gofile... {pct}%");
        });
        using var content = new MultipartFormDataContent
        {
            { new StreamContent(progressStream), "file", Path.GetFileName(filePath) }
        };

        var response = await httpClient.PostAsync("https://store1.gofile.io/uploadFile", content);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var status = doc.RootElement.GetProperty("status").GetString();
        if (status != "ok") return null;
        return doc.RootElement.GetProperty("data").GetProperty("downloadPage").GetString();
    }

    private static async Task<string?> UploadToLitterbox(string filePath, IProgress<string>? progress)
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        await using var fileStream = File.OpenRead(filePath);
        var fileSize = fileStream.Length;
        using var progressStream = new ProgressStream(fileStream, bytesRead =>
        {
            var pct = (int)(bytesRead * 100 / fileSize);
            progress?.Report($"Uploading to litterbox... {pct}%");
        });
        using var content = new MultipartFormDataContent
        {
            { new StreamContent(progressStream), "fileToUpload", Path.GetFileName(filePath) },
            { new StringContent("fileupload"), "reqtype" },
            { new StringContent("72h"), "time" }
        };

        var response = await httpClient.PostAsync("https://litterbox.catbox.moe/resources/internals/api.php", content);
        var link = (await response.Content.ReadAsStringAsync()).Trim();
        return link.StartsWith("https://") ? link : null;
    }

    private sealed class ProgressStream : Stream
    {
        private readonly Stream _inner;
        private readonly Action<long> _onProgress;
        private long _totalRead;

        public ProgressStream(Stream inner, Action<long> onProgress)
        {
            _inner = inner;
            _onProgress = onProgress;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _inner.Read(buffer, offset, count);
            if (read > 0) { _totalRead += read; _onProgress(_totalRead); }
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int read = await _inner.ReadAsync(buffer, offset, count, cancellationToken);
            if (read > 0) { _totalRead += read; _onProgress(_totalRead); }
            return read;
        }

        public override void Flush() => _inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    }

    #endregion

    #region Manifest

    public static RecordingManifest? BuildManifest()
    {
        if (!Directory.Exists(RecordingsDir)) return null;

        var files = Directory.GetFiles(RecordingsDir, "*.mp4")
            .Where(f => !Path.GetFileName(f).Contains("_video"))
            .OrderByDescending(f => File.GetCreationTime(f))
            .Take(10)
            .Select(f => new RecordingEntry
            {
                FileName = Path.GetFileName(f),
                FilePath = f,
                FileSizeBytes = new FileInfo(f).Length,
                CreatedUtc = File.GetCreationTimeUtc(f)
            })
            .ToList();

        if (files.Count == 0) return null;

        return new RecordingManifest
        {
            GeneratedAt = DateTime.Now,
            Recordings = files
        };
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _captureActive = false;
        StopRecording();
        HideOverlay();
        StopAudioCapture();
        CleanupFFmpeg();
        CleanupStagingTexture();
        CleanupCapture();
        CleanupD3D();
        CleanupTempFiles();
    }
}

public sealed class RecordingManifest
{
    public DateTime GeneratedAt { get; set; }
    public List<RecordingEntry> Recordings { get; set; } = new();
}

public sealed class RecordingEntry
{
    public string FileName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string FileSizeDisplay
    {
        get
        {
            double mb = FileSizeBytes / (1024.0 * 1024.0);
            return mb >= 1024 ? $"{mb / 1024.0:F1} GB" : $"{mb:F1} MB";
        }
    }
}
