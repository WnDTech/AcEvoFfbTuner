using System.Text;

namespace FakeWheelApp;

internal sealed class MainForm : Form
{
    private const string LogPath = @"C:\Windows\Temp\FakeRs50.log";

    private readonly PictureBox _wheelImage = new();
    private readonly Label _status = new();
    private readonly Label _counters = new();
    private readonly TextBox _logBox = new();
    private readonly System.Windows.Forms.Timer _tick = new();
    private readonly HidWatch _hid = new();
    private FileStream? _logStream;
    private long _logOffset;
    private int _sinkCount;
    private bool _tailErrorNoted;

    public MainForm()
    {
        Text = "FakeWheel — virtual RS50";
        ClientSize = new Size(1200, 640);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(900, 500);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 460,
        };

        _wheelImage.Dock = DockStyle.Fill;
        _wheelImage.SizeMode = PictureBoxSizeMode.Zoom;
        LoadWheelImage();
        split.Panel1.Controls.Add(_wheelImage);

        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6) };

        _status.Dock = DockStyle.Top;
        _status.Height = 52;
        _status.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
        _status.Text = "Fake RS50: scanning…";

        _counters.Dock = DockStyle.Top;
        _counters.Height = 22;
        _counters.Font = new Font("Consolas", 9f);
        _counters.Text = "RX: 0   TX(queue): 0";

        _logBox.Dock = DockStyle.Fill;
        _logBox.Multiline = true;
        _logBox.ReadOnly = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.Font = new Font("Consolas", 9f);
        _logBox.WordWrap = false;

        right.Controls.Add(_logBox);
        right.Controls.Add(_counters);
        right.Controls.Add(_status);
        _counters.BringToFront();
        _status.BringToFront();

        split.Panel2.Controls.Add(right);
        Controls.Add(split);

        _tick.Interval = 500;
        _tick.Tick += (_, _) => Tick();
        _tick.Start();

        Shown += (_, _) => Tick();
    }

    private void LoadWheelImage()
    {
        // ../../../assets/rs50/... relative to the app folder
        var here = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(here, @"..\..\..\..\assets\rs50\rs50-base-wheel-hub-front-angle-gallery-1.png")),
            Path.GetFullPath(Path.Combine(here, @"..\assets\rs50\rs50-base-wheel-hub-front-angle-gallery-1.png")),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                try
                {
                    _wheelImage.Image = Image.FromStream(new MemoryStream(File.ReadAllBytes(candidate)));
                    return;
                }
                catch
                {
                    // fall through to the text label
                }
            }
        }

        _wheelImage.Visible = false;
        _status.Text = "Fake RS50: (wheel image missing — see assets/rs50)";
    }

    private void Tick()
    {
        var present = _hid.Rs50Present();

        _status.ForeColor = present ? Color.ForestGreen : Color.Firebrick;
        _status.Text = present
            ? "Fake RS50 PRESENT (VID_046D PID_C276 detected)" +
              (_tailErrorNoted ? "  — log tail unavailable" : "")
            : "Fake RS50: not present";

        TailLog();
        _counters.Text = $"RX bytes logged: {_sinkCount}    (log: {LogPath})";
    }

    private void TailLog()
    {
        try
        {
            _logStream ??= new FileStream(LogPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            _logStream.Seek(_logOffset, SeekOrigin.Begin);

            var buffer = new byte[65536];
            int read;
            while ((read = _logStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                _logOffset += read;
                _sinkCount += read;
                var text = Encoding.Unicode.GetString(buffer, 0, read);
                _logBox.AppendText(text);
                TrimLog();
            }
        }
        catch (Exception)
        {
            _tailErrorNoted = true;
        }
    }

    private void TrimLog()
    {
        if (_logBox.TextLength <= 200_000)
        {
            return;
        }

        var keep = _logBox.TextLength - 100_000;
        _logBox.Text = _logBox.Text.Substring(keep);
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.ScrollToCaret();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tick.Dispose();
            _logStream?.Dispose();
            _hid.Dispose();
        }

        base.Dispose(disposing);
    }
}