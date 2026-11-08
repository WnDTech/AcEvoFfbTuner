// PHF effect capture mode (launch with --phf).
//
// Records USB traffic (usbpcap, all hubs) for up to 30 minutes while the user
// fires individual pedal-haptic effects. Each effect button toggles a
// START/END marker with a UTC timestamp into markers.txt so every burst of
// USB traffic can be matched to the effect that produced it.

using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace WheelSetupCollector;

internal sealed class PhfCaptureForm : Form
{
    private const int MaxSeconds = 1800;

    private static readonly string CaptureDir =
        Path.Combine(Path.GetTempPath(), "phf_capture");

    private static readonly string[] BrakeEffects =
        ["ABS", "TC", "Engine", "Wheelspin", "Gear Shift", "Road Texture",
         "Brake Threshold", "Wheel Lockup"];

    private static readonly string[] ThrottleEffects =
        ["Engine", "TC", "Wheelspin", "Gear Shift", "Road Texture"];

    private readonly Button _start = new() { Text = "Start capture", Width = 160, Height = 36 };
    private readonly Button _stop = new() { Text = "Stop + save", Width = 160, Height = 36, Enabled = false };
    private readonly Label _status = new() { AutoSize = false, Width = 640, Height = 40, Text = "Press Start capture (one admin prompt)." };
    private readonly ListBox _log = new() { Width = 640, Height = 160 };
    private readonly List<Button> _effectButtons = [];
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };

    private Button? _active;

    public PhfCaptureForm()
    {
        Text = "PHF effect capture";
        Width = 700;
        Height = 640;
        StartPosition = FormStartPosition.CenterScreen;

        var root = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(12),
            AutoScroll = true
        };

        root.Controls.Add(new Label
        {
            AutoSize = false, Width = 640, Height = 54,
            Text = "1. Start capture.  2. Click an effect button, then fire that effect " +
                   "for ~5-10 s (try a couple of strengths).  3. Click the same button again " +
                   "to end it.  Repeat for each effect.  4. Stop + save."
        });

        var top = new FlowLayoutPanel { AutoSize = true };
        top.Controls.Add(_start);
        top.Controls.Add(_stop);
        root.Controls.Add(top);
        root.Controls.Add(_status);

        root.Controls.Add(Group("BRAKE pedal", "Brake", BrakeEffects));
        root.Controls.Add(Group("THROTTLE pedal", "Throttle", ThrottleEffects));
        root.Controls.Add(_log);

        Controls.Add(root);

        _start.Click += (_, _) => StartCapture();
        _stop.Click += (_, _) => StopCapture();
        _timer.Tick += (_, _) => Poll();
        FormClosing += (_, _) => { if (_stop.Enabled) StopCapture(); };
        SetEffectsEnabled(false);
    }

    private GroupBox Group(string title, string pedal, string[] effects)
    {
        var box = new GroupBox { Text = title, Width = 640, Height = 110 };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(4) };
        foreach (var e in effects)
        {
            var b = new Button { Text = e, Width = 118, Height = 32, Tag = (pedal, e) };
            b.Click += (_, _) => ToggleEffect(b);
            _effectButtons.Add(b);
            flow.Controls.Add(b);
        }
        box.Controls.Add(flow);
        return box;
    }

    private void SetEffectsEnabled(bool on)
    {
        foreach (var b in _effectButtons) b.Enabled = on;
    }

    private void StartCapture()
    {
        try
        {
            Directory.CreateDirectory(CaptureDir);
            foreach (var f in Directory.GetFiles(CaptureDir)) { try { File.Delete(f); } catch { } }

            var psi = new ProcessStartInfo(Environment.ProcessPath!,
                $"--capture-only \"{CaptureDir}\" {MaxSeconds} phf")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
            _start.Enabled = false;
            _stop.Enabled = true;
            _status.Text = "Starting capture...";
            _timer.Start();
        }
        catch (Exception ex)
        {
            _status.Text = "Could not start capture: " + ex.Message;
        }
    }

    private void Poll()
    {
        try
        {
            var statusFile = Path.Combine(CaptureDir, "status.txt");
            if (!File.Exists(statusFile)) return;
            var s = File.ReadAllText(statusFile);
            _status.Text = s.Replace("DRIVE THE GAME NOW", "fire effects now");
            bool capturing = s.StartsWith("Capturing on", StringComparison.Ordinal);
            if (capturing && !_effectButtons[0].Enabled && _stop.Enabled)
            {
                SetEffectsEnabled(true);
                Mark("CAPTURE", "-", "STARTED");
            }
            if (s.StartsWith("Capture done", StringComparison.Ordinal) || s.StartsWith("Capture failed", StringComparison.Ordinal))
            {
                _timer.Stop();
                SetEffectsEnabled(false);
                _stop.Enabled = false;
                _start.Enabled = true;
                _status.Text = s + $"  →  files in {CaptureDir}";
            }
        }
        catch { }
    }

    private void ToggleEffect(Button b)
    {
        var (pedal, effect) = ((string, string))b.Tag!;

        if (_active == b)
        {
            Mark(pedal, effect, "END");
            b.BackColor = SystemColors.Control;
            _active = null;
            return;
        }

        if (_active != null)
        {
            var (p0, e0) = ((string, string))_active.Tag!;
            Mark(p0, e0, "END");
            _active.BackColor = SystemColors.Control;
        }

        _active = b;
        b.BackColor = Color.LightGreen;
        Mark(pedal, effect, "START");
    }

    private void StopCapture()
    {
        if (_active != null) ToggleEffect(_active);
        Mark("CAPTURE", "-", "STOP");
        try { File.WriteAllText(Path.Combine(CaptureDir, "stop.flag"), "stop"); } catch { }
        _stop.Enabled = false;
        SetEffectsEnabled(false);
        _status.Text = "Stopping and saving...";
    }

    private void Mark(string pedal, string effect, string what)
    {
        // UTC ISO timestamp + Unix seconds: pcap timestamps are Unix time,
        // so markers line up with packets directly.
        var now = DateTimeOffset.UtcNow;
        var line = $"{now:O}\t{now.ToUnixTimeMilliseconds() / 1000.0:F3}\t{pedal}\t{effect}\t{what}";
        try { File.AppendAllText(Path.Combine(CaptureDir, "markers.txt"), line + Environment.NewLine); } catch { }
        _log.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {pedal} {effect} {what}");
    }
}
