using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
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

    private Process? _ffmpegProcess;
    private string? _currentOutputPath;
    private string? _audioTempPath;
    private string? _videoOnlyPath;
    private DateTime _recordingStartTime;
    private bool _isRecording;
    private bool _disposed;
    private bool _ffmpegNotFound;
    private readonly TaskCompletionSource<bool> _stopCompletion = new();
    private int _stoppedTickCount;
    private const int StopAfterTicks = 90;
    private Window? _overlayWindow;
    private static string? _cachedEncoder;
    private static string? _cachedCaptureMethod;

    private WasapiLoopbackCapture? _audioCapture;
    private WaveFileWriter? _audioWriter;
    private double _audioVideoOffsetSec;

    public bool IsRecording => _isRecording;
    public string? CurrentOutputPath => _currentOutputPath;
    public DateTime RecordingStartTime => _recordingStartTime;

    public event Action<string, bool>? RecordingStateChanged;

    public static string RecordingsDirectory => RecordingsDir;

    public void OnTelemetryTick(float speedKmh)
    {
        if (_disposed || _ffmpegNotFound) return;

        if (speedKmh > 5.0f)
        {
            _stoppedTickCount = 0;
            if (!_isRecording)
                StartRecording();
        }
        else if (_isRecording)
        {
            _stoppedTickCount++;
            if (_stoppedTickCount >= StopAfterTicks)
                StopRecording();
        }
    }

    private void StartRecording()
    {
        if (_isRecording || _disposed) return;

        try
        {
            string? ffmpegPath = FindFFmpeg();
            if (ffmpegPath == null)
            {
                _ffmpegNotFound = true;
                RecordingStateChanged?.Invoke(
                    "Recording unavailable — ffmpeg.exe not found next to app", false);
                return;
            }
            Directory.CreateDirectory(RecordingsDir);
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            _videoOnlyPath = Path.Combine(RecordingsDir, $"acevo_session_{ts}_video.mp4");
            _audioTempPath = Path.Combine(RecordingsDir, $"acevo_session_{ts}_audio.wav");
            _currentOutputPath = Path.Combine(RecordingsDir, $"acevo_session_{ts}.mp4");
            _recordingStartTime = DateTime.Now;
            _stoppedTickCount = 0;

            string encoder = GetBestEncoder(ffmpegPath);
            string captureMethod = GetCaptureMethod(ffmpegPath);

            string args = captureMethod == "ddagrab"
                ? BuildDdagrabArgs(encoder, _videoOnlyPath)
                : BuildGdigrabArgs(encoder, _videoOnlyPath);

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();

            StartAudioCapture();
            long audioStartTicks = sw.ElapsedTicks;

            _ffmpegProcess = new Process { StartInfo = psi };
            _ffmpegProcess.EnableRaisingEvents = true;
            _ffmpegProcess.Exited += OnFFmpegExited;
            _ffmpegProcess.ErrorDataReceived += OnFFmpegError;
            _ffmpegProcess.Start();
            _ffmpegProcess.BeginErrorReadLine();
            long videoStartTicks = sw.ElapsedTicks;

            _audioVideoOffsetSec = (double)(audioStartTicks - videoStartTicks) / System.Diagnostics.Stopwatch.Frequency;

            System.Threading.Thread.Sleep(300);
            if (_ffmpegProcess.HasExited)
            {
                StopAudioCapture();
                try { if (_videoOnlyPath != null && File.Exists(_videoOnlyPath)) File.Delete(_videoOnlyPath); } catch { }
                CleanupProcess();
                RecordingStateChanged?.Invoke("Recording failed: FFmpeg exited immediately", false);
                return;
            }

            _isRecording = true;
            ShowOverlay();
            RecordingStateChanged?.Invoke(
                $"Recording AC EVO ({captureMethod}/{encoder}): {Path.GetFileName(_currentOutputPath)}", true);
        }
        catch (Exception ex)
        {
            StopAudioCapture();
            RecordingStateChanged?.Invoke($"Recording failed: {ex.Message}", false);
            CleanupProcess();
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

    public string? StopRecording()
    {
        if (!_isRecording || _ffmpegProcess == null) return null;

        _isRecording = false;
        _stoppedTickCount = 0;
        HideOverlay();

        StopAudioCapture();

        try { _ffmpegProcess.StandardInput.WriteLine("q"); } catch { }

        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                if (!_ffmpegProcess.WaitForExit(10000))
                    try { _ffmpegProcess.Kill(); } catch { }
            }
            catch { }

            CleanupProcess();
            string? muxedPath = MuxAudioVideo();
            CleanupTempFiles();

            string? finalPath = muxedPath ?? _currentOutputPath;
            if (finalPath != null && File.Exists(finalPath) && new FileInfo(finalPath).Length > 0)
                RecordingStateChanged?.Invoke($"Recording saved: {Path.GetFileName(finalPath)}", false);
            else
                RecordingStateChanged?.Invoke("Recording saved", false);

            _stopCompletion.TrySetResult(true);
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
            string itsoffset = Math.Abs(_audioVideoOffsetSec) > 0.005
                ? $"-itsoffset {_audioVideoOffsetSec:F4}"
                : "";

            string muxArgs = $"-y -hide_banner -loglevel error " +
                             $"-i \"{_videoOnlyPath}\" {itsoffset} -i \"{_audioTempPath}\" " +
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

    private static string BuildDdagrabArgs(string encoder, string outputPath)
    {
        var sb = new StringBuilder();
        sb.Append("-y -hide_banner -loglevel warning ");
        sb.Append("-init_hw_device d3d11va=dev ");
        sb.Append("-filter_complex \"ddagrab=framerate=30:output_fmt=8bit,hwdownload,format=bgra\" ");
        sb.Append($"-c:v {encoder} ");
        if (encoder == "libx264")
            sb.Append("-preset ultrafast -crf 23 ");
        else
            sb.Append("-b:v 15M ");
        sb.Append("-an ");
        sb.Append($"\"{outputPath}\"");
        return sb.ToString();
    }

    private static string BuildGdigrabArgs(string encoder, string outputPath)
    {
        var sb = new StringBuilder();
        sb.Append("-y -hide_banner -loglevel warning ");
        sb.Append("-f gdigrab -framerate 30 -i desktop ");
        sb.Append($"-c:v {encoder} ");
        if (encoder == "libx264")
            sb.Append("-preset ultrafast -crf 23 ");
        else
            sb.Append("-b:v 15M ");
        sb.Append("-an ");
        sb.Append($"\"{outputPath}\"");
        return sb.ToString();
    }

    private static string GetCaptureMethod(string ffmpegPath)
    {
        if (_cachedCaptureMethod != null)
            return _cachedCaptureMethod;

        _cachedCaptureMethod = ProbeDdagrab(ffmpegPath) ? "ddagrab" : "gdigrab";
        return _cachedCaptureMethod;
    }

    private static bool ProbeDdagrab(string ffmpegPath)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = "-hide_banner -loglevel error -init_hw_device d3d11va=dev -filter_complex \"ddagrab=framerate=1:output_fmt=8bit,hwdownload,format=bgra\" -c:v libx264 -preset ultrafast -t 0.1 -y -f null -",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            });
            proc?.WaitForExit(10000);
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string GetBestEncoder(string ffmpegPath)
    {
        if (_cachedEncoder != null)
            return _cachedEncoder;

        string[] encoders = { "h264_mf", "h264_nvenc", "h264_amf", "h264_qsv", "libx264" };

        foreach (var enc in encoders)
        {
            try
            {
                string filterArgs = enc == "libx264" || enc == "h264_mf"
                    ? "-init_hw_device d3d11va=dev -filter_complex \"ddagrab=framerate=10:output_fmt=8bit,hwdownload,format=bgra\""
                    : $"-f lavfi -i nullsrc=s=256x256:d=0.1";

                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-hide_banner -loglevel error {filterArgs} -c:v {enc} -t 0.2 -y -f null -",
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

    private static string? FindFFmpeg()
    {
        if (File.Exists(BundledFFmpegPath))
            return BundledFFmpegPath;

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

    private void OnFFmpegError(object sender, DataReceivedEventArgs e)
    {
    }

    private void OnFFmpegExited(object? sender, EventArgs e)
    {
        _isRecording = false;
        HideOverlay();
        _stopCompletion.TrySetResult(true);
    }

    private void CleanupProcess()
    {
        if (_ffmpegProcess != null)
        {
            try
            {
                _ffmpegProcess.Exited -= OnFFmpegExited;
                _ffmpegProcess.ErrorDataReceived -= OnFFmpegError;
                if (!_ffmpegProcess.HasExited)
                    try { _ffmpegProcess.Kill(); } catch { }
                _ffmpegProcess.Dispose();
            }
            catch { }
            _ffmpegProcess = null;
        }
    }

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
                            Text = "● Recording Telemetry Session",
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopRecording();
        HideOverlay();
        StopAudioCapture();
        _stopCompletion.Task.Wait(TimeSpan.FromSeconds(15));
        CleanupProcess();
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
