using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace AcEvoFfbTuner.Services;

public sealed class GameRecordingService : IDisposable
{
    private static readonly string RecordingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "recordings");

    private NativeVideoRecorder? _recorder;
    private string? _currentOutputPath;
    private DateTime _recordingStartTime;
    private bool _isRecording;
    private bool _disposed;
    private readonly TaskCompletionSource<bool> _stopCompletion = new();
    private int _stoppedTickCount;
    private const int StopAfterTicks = 90;
    private Window? _overlayWindow;
    private bool _recorderFailed;

    public bool IsRecording => _isRecording;
    public string? CurrentOutputPath => _currentOutputPath;
    public DateTime RecordingStartTime => _recordingStartTime;

    public event Action<string, bool>? RecordingStateChanged;

    public static string RecordingsDirectory => RecordingsDir;

    public void OnTelemetryTick(float speedKmh)
    {
        if (_disposed || _recorderFailed) return;

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
            Directory.CreateDirectory(RecordingsDir);
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            _currentOutputPath = Path.Combine(RecordingsDir, $"acevo_session_{ts}.mp4");
            _recordingStartTime = DateTime.Now;
            _stoppedTickCount = 0;

            _recorder = new NativeVideoRecorder(fps: 30, bitrate: 15_000_000);
            bool ok = _recorder.Start(_currentOutputPath);

            if (!ok)
            {
                _recorderFailed = true;
                RecordingStateChanged?.Invoke(
                    $"Recording failed: {_recorder.Error ?? "Unknown error"}", false);
                _recorder.Dispose();
                _recorder = null;
                return;
            }

            _isRecording = true;
            ShowOverlay();
            RecordingStateChanged?.Invoke(
                $"Recording AC EVO (MF/H.264): {Path.GetFileName(_currentOutputPath)}", true);
        }
        catch (Exception ex)
        {
            RecordingStateChanged?.Invoke($"Recording failed: {ex.Message}", false);
            _recorder?.Dispose();
            _recorder = null;
        }
    }

    public string? StopRecording()
    {
        if (!_isRecording || _recorder == null) return null;

        _isRecording = false;
        _stoppedTickCount = 0;
        HideOverlay();

        var recorder = _recorder;
        _recorder = null;

        _ = Task.Run(() =>
        {
            try
            {
                recorder.Stop();
                recorder.Dispose();
            }
            catch { }

            if (_currentOutputPath != null && File.Exists(_currentOutputPath) && new FileInfo(_currentOutputPath).Length > 0)
                RecordingStateChanged?.Invoke($"Recording saved: {Path.GetFileName(_currentOutputPath)}", false);
            else
                RecordingStateChanged?.Invoke("Recording saved", false);

            _stopCompletion.TrySetResult(true);
        });

        RecordingStateChanged?.Invoke("Recording stopped — finalizing...", false);
        return _currentOutputPath;
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
            { new StringContent("fileupload"), "reqtype" },
            { new StringContent("72h"), "time" },
            { new StreamContent(progressStream), "fileToUpload", Path.GetFileName(filePath) }
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
        _recorder?.Dispose();
        _recorder = null;
        _stopCompletion.Task.Wait(TimeSpan.FromSeconds(15));
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
