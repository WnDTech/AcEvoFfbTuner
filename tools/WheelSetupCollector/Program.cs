// WheelSetupCollector — GUI wheelbase data collector for the AC Evo FFB Tuner.
//
// An end user selects their wheelbase manufacturer(s), clicks Collect, and
// the tool gathers everything useful for implementing that wheelbase in the
// FFB app: the manufacturer's software config (G HUB / FanaLab / Pithouse /
// SimPro / TrueDrive / TM / Asetek...), the Windows device tree, registry
// install state, running processes, game FFB configs, and the tuner's own
// logs/profiles. One zip, no guessing.

using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WheelSetupCollector;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var form = new CollectorForm();
        var args = Environment.GetCommandLineArgs();

        if (args.Length >= 3 && args[1] == "--capture-only")
        {
            // Elevated capture helper: launched by the main instance with
            // runas (one UAC). Captures the selected USBPcap hub(s) filtered
            // by the given duration, then exits.
            var dir = args[2];
            int seconds = args.Length >= 4 && int.TryParse(args[3], out var s) ? s : 60;
            var hubSpec = args.Length >= 5 ? args[4] : "all";
            Environment.ExitCode = RunUsbCapture(dir, seconds, hubSpec) ? 0 : 1;
            return;
        }

        if (args.Contains("--list-usb"))
        {
            // Debug: dump the capture-point list the UI would show.
            var sb = new StringBuilder();
            foreach (var (hubNumber, devices, _) in CollectorForm.BuildHubDeviceList())
                sb.AppendLine($"USB {hubNumber}: {devices}");
            if (!string.IsNullOrEmpty(CollectorForm.LastBuildError))
                sb.AppendLine("ERROR: " + CollectorForm.LastBuildError);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "collector_usb_list.txt"), sb.ToString());
            return;
        }

        if (args.Contains("--auto"))
        {
            // Headless mode (used by the developer / automated runs): collect
            // everything, no dialogs, exit code 0 = success.
            form.Headless = true;
            form.RunCollection(CollectorForm.AllManufacturerNames);
            Environment.ExitCode = form.Success ? 0 : 1;
            return;
        }
        Application.Run(form);
    }

    /// <summary>Elevated capture helper body. Two methods:
    /// 1) A SPECIFIC device selected → per-hub usbpcap pcaps (compact,
    ///    per-hub files; the selected device's traffic is isolated during
    ///    analysis). Requires usbpcap already installed.
    /// 2) "All USB hubs" (or no usbpcap) → Windows' own USB tracing (ETW —
    ///    nothing installed, nothing to uninstall, no drivers).
    /// The capture files land in <paramref name="dir"/>.</summary>
    private static bool RunUsbCapture(string dir, int seconds, string hubSpec)
    {
        try
        {
            Directory.CreateDirectory(dir);
            foreach (var f in Directory.GetFiles(dir)) { try { File.Delete(f); } catch { } }

            bool deviceSelected = !string.Equals(hubSpec, "all", StringComparison.OrdinalIgnoreCase);
            var usbpcap = FindUsbpcapCmd();

            if (deviceSelected && usbpcap != null)
            {
                WriteStatus(dir, "Capturing per-hub pcap files (usbpcap) — your device's traffic is isolated during analysis");
                return RunUsbpcapCapture(dir, seconds, "all");
            }

            if (deviceSelected)
                WriteStatus(dir, "Device selection needs usbpcap (not installed) — capturing ALL USB traffic instead");

            WriteStatus(dir, "Starting Windows USB tracing (built-in — no driver installation needed)...");
            if (RunEtlCapture(dir, seconds))
                return true;

            if (usbpcap != null)
            {
                WriteStatus(dir, "Windows tracing failed — falling back to usbpcap...");
                return RunUsbpcapCapture(dir, seconds, "all");
            }

            WriteStatus(dir, "Windows USB tracing failed and usbpcap is not installed — no capture was made");
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Captures USB traffic using Windows' built-in ETW providers
    /// (Microsoft-Windows-USB-USBPORT + UCX) via logman — part of Windows,
    /// nothing is installed. The trace is just a log file: it records traffic,
    /// it cannot change anything on the PC.</summary>
    private static bool RunEtlCapture(string dir, int seconds)
    {
        try
        {
            var etl = Path.Combine(dir, "usb.etl");

            // Remove any leftover trace from a crashed run (ignore errors).
            RunProcess("logman.exe", "delete usbtrace -ets", 5000);

            // logman allows only ONE -p per create — add the second provider
            // via an update on the same trace.
            bool created = RunProcess("logman.exe",
                $"create trace usbtrace -p \"Microsoft-Windows-USB-USBPORT\" 0xffffffffffffffff 0xff " +
                $"-o \"{etl}\" -ets", 15000);
            if (created)
                RunProcess("logman.exe",
                    "update trace usbtrace -p \"Microsoft-Windows-USB-UCX\" 0xffffffffffffffff 0xff -ets", 15000);

            if (!created)
            {
                WriteStatus(dir, "Could not start Windows USB tracing (logman failed)");
                return false;
            }

            for (int s = 1; s <= seconds; s++)
            {
                Thread.Sleep(1000);
                WriteStatus(dir, $"Capturing USB traffic (Windows tracing) — {s}/{seconds} s — DRIVE THE GAME NOW");
            }

            RunProcess("logman.exe", "stop usbtrace -ets", 15000);

            if (!File.Exists(etl) || new FileInfo(etl).Length < 4096)
            {
                try { File.Delete(etl); } catch { }
                WriteStatus(dir, "Windows USB trace came back empty");
                return false;
            }

            WriteStatus(dir, "Capture done — Windows USB trace saved (nothing to uninstall)");
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Runs a command line, waits up to the timeout, returns success.</summary>
    private static bool RunProcess(string file, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(); } catch { }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>usbpcap capture (only used when it is already installed):
    /// per-hub pcap files covering all hubs, 512-byte snaplen. The selected
    /// device's traffic is isolated during analysis (each hub is its own
    /// small file). <paramref name="spec"/> is kept for the hub-number path
    /// but the standard flow uses "all".</summary>
    private static bool RunUsbpcapCapture(string dir, int seconds, string spec)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var usbpcap = FindUsbpcapCmd();
            if (usbpcap == null) return false;

            // Enumerate capture devices. NOTE: this usbpcapcmd build prints only
            // help text (no live device list — and its help contains the
            // "\\.\USBPcap1" example, which would wrongly stop the list at hub 1),
            // so always probe USBPcap1..6 and let failed opens be ignored.
            var devices = new List<string>();
            for (int i = 1; i <= 6; i++) devices.Add($@"\\.\USBPcap{i}");

            // Optional hub-number selection.
            if (!string.Equals(spec, "all", StringComparison.OrdinalIgnoreCase))
            {
                var wanted = spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                devices = devices.Where(d =>
                {
                    var m = System.Text.RegularExpressions.Regex.Match(d, @"USBPcap(\d+)");
                    return m.Success && wanted.Contains(m.Groups[1].Value);
                }).ToList();
            }

            WriteStatus(dir, $"Starting capture on {devices.Count} USB hub(s)...");

            var procs = new List<Process>();
            var pcapPaths = new List<string>();
            int idx = 1;
            foreach (var dev in devices)
            {
                var outPath = Path.Combine(dir, $"usbcap{idx++}.pcap");
                pcapPaths.Add(outPath);
                try
                {
                    // All devices on the root hub, 512-byte snaplen.
                    var psi = new ProcessStartInfo(usbpcap,
                        $"-d {dev} -o \"{outPath}\" -A -s 512")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    procs.Add(Process.Start(psi)!);
                }
                catch { }
            }

            int alive = procs.Count(p => !p.HasExited);
            WriteStatus(dir, alive > 0
                ? $"Capturing on {alive} USB hub(s) — DRIVE THE GAME NOW"
                : "Capture could not start — no USB hub opened");

            for (int s = 1; s <= seconds; s++)
            {
                Thread.Sleep(1000);
                int aliveNow = procs.Count(p => !p.HasExited);
                WriteStatus(dir, aliveNow > 0
                    ? $"Capturing on {aliveNow} USB hub(s) — {s}/{seconds} s — DRIVE THE GAME NOW"
                    : $"Capture stalled ({s}/{seconds} s) — no USB hub is being captured");
            }

            WriteStatus(dir, "Stopping capture and saving...");

            foreach (var p in procs)
            {
                try { if (!p.HasExited) p.Kill(); } catch { }
            }
            foreach (var p in procs) { try { p.WaitForExit(3000); p.Dispose(); } catch { } }

            // Drop empty/failed pcaps.
            foreach (var f in pcapPaths)
            {
                try { if (!File.Exists(f) || new FileInfo(f).Length == 0) File.Delete(f); } catch { }
            }
            int saved = Directory.GetFiles(dir, "*.pcap").Length;
            WriteStatus(dir, saved > 0 ? $"Capture done — {saved} pcap file(s) saved" : "Capture done — no data captured");
            return saved > 0;
        }
        catch
        {
            WriteStatus(dir, "Capture failed");
            return false;
        }
    }

    /// <summary>Live progress file the elevated capture helper writes and the
    /// main instance reads to show the user what is happening.</summary>
    private static void WriteStatus(string dir, string text)
    {
        try { File.WriteAllText(Path.Combine(dir, "status.txt"), text); } catch { }
    }

    internal static string? FindUsbpcapCmd()
    {
        foreach (var p in new[]
                 {
                     @"C:\Program Files\USBPcap\usbpcapcmd.exe",
                     @"C:\Program Files (x86)\USBPcap\usbpcapcmd.exe"
                 })
        {
            if (File.Exists(p)) return p;
        }
        try
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                if (dir.Length == 0) continue;
                var cand = Path.Combine(dir.Trim('"'), "usbpcapcmd.exe");
                if (File.Exists(cand)) return cand;
            }
        }
        catch { }
        return null;
    }
}

internal sealed class CollectorForm : Form
{
    private readonly CheckedListBox _manufacturers;
    private readonly Button _collectButton;
    private readonly ListBox _log;
    private readonly Label _status;
    private CheckBox? _usbCaptureCheck;
    private ComboBox? _usbHubCombo;

    private const int CaptureSeconds = 60;
    private static readonly string CaptureDir =
        Path.Combine(Path.GetTempPath(), "wheelcollector_usbcapture");

    /// <summary>True in --auto mode: no dialogs, no explorer, headless.</summary>
    public bool Headless { get; set; }

    /// <summary>True when the last collection completed and verified its zip.</summary>
    public bool Success { get; private set; }

    /// <summary>All manufacturer names, in display order.</summary>
    public static List<string> AllManufacturerNames => Manufacturers.Select(m => m.Name).ToList();

    // Manufacturer → search hints. Folders/registry/device tree are matched
    // case-insensitively by these keywords; device tree also by VID.
    private static readonly (string Name, string[] Keywords, string[] Vids)[] Manufacturers =
    [
        ("Logitech", ["lghub", "logishrd", "logitech", "wheel_sdk"], ["046D"]),
        ("Fanatec", ["fanatec", "fanalab"], ["0EB7"]),
        ("Moza", ["moza", "pithouse"], ["346E"]),
        ("Simagic", ["simagic", "simpro", "sim pro"], ["0F0D", "26B4"]),
        ("Simucube", ["simucube", "truedrive", "granite"], ["21D1", "1915"]),
        ("Thrustmaster", ["thrustmaster", "tmphub", "tm control"], ["044F", "06F8"]),
        ("Asetek", ["asetek"], ["231D"]),
        ("Other / Generic", ["simhub", "dimmer", "sim racing", "simucube"], []),
    ];

    public CollectorForm()
    {
        Text = "Wheelbase Data Collector - AC Evo FFB Tuner";
        ClientSize = new Size(640, 580);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(600, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);

        // App logo header + title-bar icon, so it's clearly part of the
        // AC Evo FFB Tuner and not some random tool.
        try
        {
            using var stream = System.Reflection.Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("WheelSetupCollector.Resources.app_icon.png");
            if (stream != null)
            {
                var logo = new PictureBox
                {
                    Image = Image.FromStream(stream),
                    Location = new Point(12, 14),
                    Size = new Size(64, 64),
                    SizeMode = PictureBoxSizeMode.Zoom
                };
                Controls.Add(logo);
            }
        }
        catch { }
        try
        {
            var icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "");
            if (icon != null) Icon = icon;
        }
        catch { }

        var intro = new Label
        {
            Text = "Select your wheelbase manufacturer(s), then click Collect.\n" +
                   "The tool gathers the setup data the developer needs to implement\n" +
                   "your wheelbase properly - one zip file, no hunting for files.",
            Location = new Point(92, 12),
            AutoSize = true
        };
        Controls.Add(intro);

        var manLabel = new Label { Text = "Manufacturers:", Location = new Point(92, 80), AutoSize = true };
        Controls.Add(manLabel);

        _manufacturers = new CheckedListBox
        {
            Location = new Point(92, 102),
            Size = new Size(300, 180),
            CheckOnClick = true,
            IntegralHeight = false,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        foreach (var m in Manufacturers)
            _manufacturers.Items.Add(m.Name, true);
        Controls.Add(_manufacturers);

        _collectButton = new Button
        {
            Text = "Collect data && create ZIP",
            Location = new Point(92, 297),
            Size = new Size(300, 40),
            Font = new Font("Segoe UI", 10f, FontStyle.Bold)
        };
        _collectButton.Click += (_, _) => StartCollection();
        Controls.Add(_collectButton);

        _usbCaptureCheck = new CheckBox
        {
            Text = "Capture USB traffic (60 s) — shows what the game sends to the wheel",
            Location = new Point(92, 345),
            AutoSize = true,
            Checked = false,
            Font = new Font("Segoe UI", 9f)
        };
        _usbCaptureCheck.CheckedChanged += (_, _) => UpdateCollectButtonText();
        Controls.Add(_usbCaptureCheck);
        var captureTip = new ToolTip();
        captureTip.SetToolTip(_usbCaptureCheck,
            "Uses Windows' built-in USB tracing — nothing is installed, nothing to uninstall, no drivers, no BIOS/Secure Boot changes, no reboot. While capturing, drive in the game so the trace shows the wheel's USB traffic. One admin confirmation.");

        var hubLabel = new Label
        {
            Text = "Capture:",
            Location = new Point(92, 373),
            AutoSize = true,
            Font = new Font("Segoe UI", 9f)
        };
        Controls.Add(hubLabel);

        _usbHubCombo = new ComboBox
        {
            Location = new Point(150, 369),
            Width = 360,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = new Font("Segoe UI", 9f),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        _usbHubCombo.Items.Add("All USB hubs");
        foreach (var (hubNumber, devices, _) in BuildHubDeviceList())
            _usbHubCombo.Items.Add($"USB {hubNumber}: {devices}");
        _usbHubCombo.SelectedIndex = 0;

        // The dropdown must show the full device names — size it to the widest
        // item instead of clipping at the control width.
        try
        {
            int maxW = 0;
            foreach (var item in _usbHubCombo.Items)
                maxW = Math.Max(maxW, TextRenderer.MeasureText(item.ToString() ?? "", _usbHubCombo.Font).Width);
            _usbHubCombo.DropDownWidth = Math.Max(_usbHubCombo.Width, maxW + 40);
        }
        catch { }

        Controls.Add(_usbHubCombo);
        var hubTip = new ToolTip();
        hubTip.SetToolTip(_usbHubCombo,
            "What to capture. Selecting your wheel (e.g. 'Logitech G HUB RS50') uses per-hub pcap files (compact, easy to analyse — needs usbpcap installed); 'All USB hubs' captures everything with Windows' built-in tracing (no install). Without usbpcap, a device selection falls back to the full trace.");

        _status = new Label
        {
            Text = "",
            Location = new Point(12, 398),
            Size = new Size(576, 20),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        Controls.Add(_status);

        _log = new ListBox
        {
            Location = new Point(12, 423),
            Size = new Size(576, 130),
            IntegralHeight = false,
            Font = new Font("Consolas", 9f),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
        };
        Controls.Add(_log);
        UpdateCollectButtonText();
    }

    private void Log(string msg)
    {
        if (IsDisposed) return;
        try
        {
            if (InvokeRequired)
            {
                BeginInvoke(() => { _log.Items.Add(msg); _log.TopIndex = _log.Items.Count - 1; });
            }
            else
            {
                _log.Items.Add(msg);
                _log.TopIndex = _log.Items.Count - 1;
            }
        }
        catch { }
    }

    private void SetStatus(string msg)
    {
        if (IsDisposed) return;
        try
        {
            if (InvokeRequired) BeginInvoke(() => _status.Text = msg);
            else _status.Text = msg;
        }
        catch { }
    }

    /// <summary>The Collect button reflects what it will actually do — the
    /// USB capture is part of the collection when its checkbox is ticked.</summary>
    private void UpdateCollectButtonText()
    {
        _collectButton.Text = _usbCaptureCheck?.Checked == true
            ? "Collect data, capture USB (60 s) && create ZIP"
            : "Collect data && create ZIP";
    }

    private void StartCollection()
    {
        var selected = new List<string>();
        for (int i = 0; i < _manufacturers.Items.Count; i++)
            if (_manufacturers.GetItemChecked(i))
                selected.Add((string)_manufacturers.Items[i]!);

        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Select at least one manufacturer.", "Wheelbase Data Collector",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _collectButton.Enabled = false;
        _log.Items.Clear();
        Task.Run(() => RunCollection(selected));
    }

    /// <summary>Runs the collection for the given manufacturers (called from
    /// the Collect button and from --auto headless mode).</summary>
    public void RunCollection(List<string> selected)
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var zipName = $"WheelbaseData_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
            var zipPath = Path.Combine(desktop, zipName);
            var found = new List<string>();

            Log("Collecting wheelbase data...");
            {
                using var fs = new FileStream(zipPath, FileMode.Create);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

                WriteEntry(zip, "README.txt", BuildReadme());

                foreach (var m in Manufacturers)
                {
                    if (!selected.Contains(m.Name)) continue;
                    Log($"--- {m.Name} ---");
                    CollectManufacturer(zip, m.Name, m.Keywords, m.Vids, found, Log);
                }

                Log("--- Generic info (always collected) ---");
                CollectGeneric(zip, found);

                if (!Headless && _usbCaptureCheck?.Checked == true)
                {
                    Log("--- USB capture phase (DRIVE THE GAME NOW) ---");
                    Log("Using Windows built-in USB tracing — nothing is installed, nothing to uninstall");
                    var helper = LaunchCaptureHelper();
                    if (helper != null)
                    {
                        Log("USB capture running — see the countdown window, DRIVE THE GAME NOW");
                        try
                        {
                            Invoke((Action)(() => ShowCaptureDialog(helper)));
                        }
                        catch { }
                        try { helper.WaitForExit(25000); } catch { }
                        Log("USB capture finished — bundling the captured traffic into the zip...");
                    }
                    else
                    {
                        Log("USB capture could not start (elevation declined?) — skipping");
                    }
                    AddCaptureFilesToZip(zip, found);
                }
            }

            // Self-verification: the zip must be complete on disk.
            try
            {
                using var check = ZipFile.OpenRead(zipPath);
                if (check.Entries.Count == 0)
                    throw new InvalidOperationException("zip contains no entries");
            }
            catch (Exception verifyEx)
            {
                var fallback = Path.Combine(Path.GetTempPath(), zipName);
                File.Copy(zipPath, fallback, true);
                try { File.Delete(zipPath); } catch { }
                zipPath = fallback;
                using var verify = ZipFile.OpenRead(zipPath);
                if (verify.Entries.Count == 0)
                    throw new InvalidOperationException("zip verify failed: " + verifyEx.Message);
                Log("NOTE: Desktop write was incomplete - used fallback copy.");
            }

            Success = true;
            Log($"Done - {found.Count} item(s) collected.");
            SetStatus($"Zip ready: {zipPath}");
            if (Headless) return;

            var result = MessageBox.Show(this,
                $"The wheelbase data zip is ready:\n\n{zipPath}\n\n" +
                "Send this file to the developer (Discord / GitHub).\n\n" +
                "Open the folder?",
                "Wheelbase Data Collector - done",
                MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (result == DialogResult.Yes)
                Process.Start("explorer.exe", $"/select,\"{zipPath}\"");
        }
        catch (Exception ex)
        {
            Log($"FAILED: {ex.Message}");
            SetStatus("Failed");
            if (!Headless)
                MessageBox.Show(this,
                    $"The collector failed:\n\n{ex.Message}\n\n" +
                    "Please tell the developer exactly what this says.",
                    "Wheelbase Data Collector - error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (!IsDisposed && !Headless)
                BeginInvoke(() => _collectButton.Enabled = true);
        }
    }

    private static string BuildReadme()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Wheelbase Data Collector - collected data");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine("Folders in this zip:");
        sb.AppendLine("  <Manufacturer>/    that manufacturer's software config and info");
        sb.AppendLine("  Generic/           Windows device tree, registry, processes, system");
        sb.AppendLine("  GameConfigs/       sim game FFB/settings files (AC EVO, ACC, R3E, LMU, rF2)");
        sb.AppendLine("  Tuner/             AC Evo FFB Tuner logs and profiles (if installed)");
        sb.AppendLine("  crashdumps/        Windows crash dumps for the tuner (if any)");
        sb.AppendLine();
        sb.AppendLine("Send this zip to the developer as-is.");
        return sb.ToString();
    }

    // ─────────────────────── USB capture (usbpcap) ─────────────────────────

    /// <summary>VID prefix → vendor name, so wheels that enumerate with generic
    /// names ("USB Serial Device (COM4)") are still identifiable.</summary>
    private static readonly Dictionary<string, string> KnownVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["046D"] = "Logitech", ["0EB7"] = "Fanatec", ["346E"] = "Moza",
        ["044F"] = "Thrustmaster", ["0F0D"] = "Simagic", ["26B4"] = "Simagic",
        ["21D1"] = "Simucube", ["1915"] = "Simucube", ["231D"] = "Asetek",
    };

    /// <summary>Builds the capture-point list for the UI: each entry is one
    /// USB capture device (~USBPcapN, in bus order) labelled with the devices
    /// attached to it — grouped per composite device (all its interfaces share
    /// the base key) and named by friendly name or vendor+VID, so the user
    /// picks the entry that shows their wheel. Also returns the VID base keys
    /// of the entry's devices (used for address-filtered capture).</summary>
    internal static List<(int HubNumber, string Devices, List<string> VidKeys)> BuildHubDeviceList()
    {
        LastBuildError = null;
        var result = new List<(int, string, List<string>)>();
        try
        {
            using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usb == null) return result;

            // Group by the COMPOSITE base key (strip the &MI_xx interface
            // suffix) — all interfaces of a device belong to one entry.
            var groups = new Dictionary<string, (string VidKey, List<(string Name, string? Parent)> Members)>(StringComparer.OrdinalIgnoreCase);
            foreach (var sub in usb.GetSubKeyNames())
            {
                string baseKey = sub;
                int mi = sub.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase);
                if (mi > 0) baseKey = sub.Substring(0, mi);

                using var key = usb.OpenSubKey(sub);
                if (key == null) continue;
                foreach (var inst in key.GetSubKeyNames())
                {
                    using var ik = key.OpenSubKey(inst);
                    if (ik == null) continue;
                    string? parent = ik.GetValue("ParentIdPrefix") as string;
                    string? friendly = ik.GetValue("FriendlyName") as string;
                    string? desc = ik.GetValue("DeviceDesc") as string;
                    string name = friendly ?? CleanDeviceDesc(desc) ?? sub;
                    if (!groups.TryGetValue(baseKey, out var g))
                    {
                        g = (sub, new List<(string, string?)>());
                        groups[baseKey] = g;
                    }
                    g.Members.Add((name, parent));
                }
            }

            var hubParents = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var compositeLabel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var compositeParent = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            foreach (var (inst, g) in groups)
            {
                bool isHub = g.Members.Any(m => m.Name.Contains("Hub", StringComparison.OrdinalIgnoreCase))
                             || g.VidKey.StartsWith("ROOT_HUB", StringComparison.OrdinalIgnoreCase);
                var parent = g.Members.Select(m => m.Parent).FirstOrDefault(p => !string.IsNullOrEmpty(p));
                if (isHub)
                {
                    hubParents[inst] = parent;
                    continue;
                }

                // Label: prefer a friendly name; else vendor + VID + member names.
                var friendly = g.Members
                    .Select(m => m.Name)
                    .FirstOrDefault(n => !string.IsNullOrEmpty(n)
                        && !n.Contains("Input Device", StringComparison.OrdinalIgnoreCase)
                        && !n.Contains("Composite Device", StringComparison.OrdinalIgnoreCase)
                        && !n.Contains("Serial Device", StringComparison.OrdinalIgnoreCase)
                        && !n.Contains("Hub", StringComparison.OrdinalIgnoreCase));
                string label;
                if (!string.IsNullOrEmpty(friendly))
                {
                    label = friendly;
                }
                else
                {
                    string vid = g.VidKey.StartsWith("VID_", StringComparison.OrdinalIgnoreCase) && g.VidKey.Length >= 8
                        ? g.VidKey.Substring(4, 4)
                        : "";
                    string vendor = vid.Length > 0 && KnownVendors.TryGetValue(vid, out var v) ? v + " " : "";
                    var descs = g.Members.Select(m => m.Name).Distinct().ToList();
                    label = $"{vendor}{g.VidKey}: {string.Join(", ", descs)}";
                }
                compositeLabel[inst] = label;
                compositeParent[inst] = parent;
            }

            // Walk each composite's parent chain to its top-level ancestor.
            string TopAncestor(string? parent)
            {
                var cur = parent;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (!string.IsNullOrEmpty(cur) && seen.Add(cur))
                {
                    string? hubInst = null;
                    foreach (var id in hubParents.Keys)
                    {
                        if (string.Equals(id, cur, StringComparison.OrdinalIgnoreCase)
                            || id.StartsWith(cur + "&", StringComparison.OrdinalIgnoreCase))
                        {
                            hubInst = id;
                            break;
                        }
                    }
                    if (hubInst == null || !hubParents.TryGetValue(hubInst, out var up)) break;
                    cur = up;
                }
                return cur ?? "";
            }

            var byTop = new Dictionary<string, (List<string> Labels, List<string> Vids)>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            foreach (var (inst, label) in compositeLabel)
            {
                string top = TopAncestor(compositeParent[inst]);
                if (!byTop.TryGetValue(top, out var entry))
                {
                    entry = (new List<string>(), new List<string>());
                    byTop[top] = entry;
                    order.Add(top);
                }
                entry.Labels.Add(label);
                if (inst.StartsWith("VID_", StringComparison.OrdinalIgnoreCase))
                    entry.Vids.Add(inst);
            }

            int n = 1;
            foreach (var top in order)
            {
                var names = byTop[top].Labels.Distinct().ToList();
                string label = names.Count > 0
                    ? string.Join(", ", names.Take(3)) + (names.Count > 3 ? $" +{names.Count - 3} more" : "")
                    : "(nothing attached)";
                result.Add((n, label, byTop[top].Vids.Distinct().ToList()));
                n++;
            }
        }
        catch (Exception ex)
        {
            LastBuildError = ex.ToString();
        }
        return result;
    }

    /// <summary>Last BuildHubDeviceList exception (debug/diagnostic use).</summary>
    internal static string? LastBuildError;

    /// <summary>Strips the '@oemXX.inf,%key%;' prefix Windows puts on DeviceDesc
    /// values, leaving the readable device name.</summary>
    private static string? CleanDeviceDesc(string? desc)
    {
        if (string.IsNullOrEmpty(desc)) return null;
        int semi = desc.LastIndexOf(';');
        if (semi >= 0 && desc.Contains('%')) return desc[(semi + 1)..].Trim();
        return desc;
    }

    /// <summary>Launches the elevated capture helper (one UAC): this exe with
    /// --capture-only, which runs the Windows USB tracing (ETW) — or the
    /// usbpcap fallback if it is already installed — for the given duration.</summary>
    private static Process? LaunchCaptureHelper()
    {
        try
        {
            Directory.CreateDirectory(CaptureDir);
            foreach (var f in Directory.GetFiles(CaptureDir)) { try { File.Delete(f); } catch { } }
            var exe = Environment.ProcessPath ?? Application.ExecutablePath;

            // Capture selection: combo index 0 = all (Windows tracing);
            // any device entry = per-hub usbpcap pcaps when available.
            string hubSpec = "all";
            if (Form.ActiveForm is CollectorForm cf && cf._usbHubCombo is { } combo && combo.SelectedIndex > 0)
                hubSpec = "device";

            var psi = new ProcessStartInfo(exe, $"--capture-only \"{CaptureDir}\" {CaptureSeconds} {hubSpec}")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            return Process.Start(psi);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Modal countdown dialog shown while the USB capture runs —
    /// the user drives the game during this window. Shows the live status
    /// the elevated helper reports via status.txt.</summary>
    private void ShowCaptureDialog(Process helper)
    {
        var statusPath = Path.Combine(CaptureDir, "status.txt");
        using var dlg = new Form
        {
            Text = "USB Capture",
            ClientSize = new Size(440, 210),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MaximizeBox = false,
            MinimizeBox = false,
            Font = new Font("Segoe UI", 9.5f)
        };
        var label = new Label
        {
            Text = "DRIVE NOW — capturing USB traffic.\n" +
                   "Drive in the game so the capture shows what the game\n" +
                   "sends to the wheel. Do not close this window.",
            Location = new Point(16, 14),
            AutoSize = true,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold)
        };
        var phase = new Label
        {
            Location = new Point(16, 96),
            Size = new Size(400, 40),
            ForeColor = Color.Firebrick,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold)
        };
        var countdown = new Label { Location = new Point(16, 138), AutoSize = true };
        var bar = new ProgressBar { Location = new Point(16, 164), Width = 400, Maximum = CaptureSeconds * 10 };
        dlg.Controls.Add(label);
        dlg.Controls.Add(phase);
        dlg.Controls.Add(countdown);
        dlg.Controls.Add(bar);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += (_, _) =>
        {
            int remain = CaptureSeconds - (int)sw.Elapsed.TotalSeconds;
            countdown.Text = $"Remaining: {remain} s";
            bar.Value = Math.Min(bar.Maximum, (int)(sw.Elapsed.TotalSeconds * 10));

            try
            {
                if (File.Exists(statusPath))
                {
                    var status = File.ReadAllText(statusPath);
                    if (!string.IsNullOrWhiteSpace(status)) phase.Text = status;
                }
            }
            catch { }

            if (helper.HasExited || remain <= 0)
            {
                timer.Stop();
                dlg.Close();
            }
        };
        timer.Start();
        dlg.ShowDialog(this);
        timer.Stop();
    }

    /// <summary>Adds the captured traffic files (usb.etl from Windows tracing,
    /// or pcaps from the usbpcap fallback) to the zip, compressed.</summary>
    private static void AddCaptureFilesToZip(ZipArchive zip, List<string> found)
    {
        foreach (var f in Directory.GetFiles(CaptureDir))
        {
            if (!f.EndsWith(".etl", StringComparison.OrdinalIgnoreCase)
                && !f.EndsWith(".pcap", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var fi = new FileInfo(f);
                if (fi.Length == 0) continue;
                WriteBytesEntry(zip, $"usbcapture/{fi.Name}", File.ReadAllBytes(f));
                found.Add($"usbcapture/{fi.Name} ({fi.Length / 1024} KB)");
            }
            catch { }
        }
    }

    // ─────────────────────────── collection core ───────────────────────────

    private static void CollectManufacturer(ZipArchive zip, string name, string[] keywords, string[] vids,
        List<string> found, Action<string> log)
    {
        // 1) Software data folders (G HUB, FanaLab, Pithouse, SimPro, TrueDrive, ...)
        foreach (var root in new[]
                 {
                     @"%ProgramData%", @"%LOCALAPPDATA%", @"%APPDATA%",
                     @"%ProgramFiles%", @"%ProgramFiles(x86)%"
                 })
        {
            var path = Environment.ExpandEnvironmentVariables(root);
            if (!Directory.Exists(path)) continue;
            foreach (var dir in Directory.GetDirectories(path))
            {
                var dn = Path.GetFileName(dir);
                if (dn.Length == 0 || !MatchesKeywords(dn, keywords)) continue;
                var count = AddFolderBounded(zip, dir, $"{name}/{dn}", found, log: s => { });
                if (count > 0)
                    found.Add($"{name}/{dn}: {count} file(s)");
            }
        }

        // 2) Registry: software keys + uninstall entries
        var regSb = new StringBuilder();
        regSb.AppendLine($"=== {name} registry state ===");
        regSb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        DumpRegistryMatches(regSb, keywords, name);
        WriteEntry(zip, $"{name}/registry.txt", regSb.ToString());
        found.Add($"{name}/registry.txt");

        // 3) Device tree (by VID or device name)
        var devSb = new StringBuilder();
        devSb.AppendLine($"=== {name} devices from the Windows device tree ===");
        DumpDeviceMatches(devSb, keywords, vids);
        WriteEntry(zip, $"{name}/devices.txt", devSb.ToString());
        found.Add($"{name}/devices.txt");

        log($"{name}: software folders, registry, device tree collected.");
    }

    private static void CollectGeneric(ZipArchive zip, List<string> found)
    {
        WriteEntry(zip, "Generic/system.txt", BuildSystemInfo());
        WriteEntry(zip, "Generic/processes.txt", BuildProcessInfo());
        found.Add("Generic/system.txt");
        found.Add("Generic/processes.txt");

        // Full Logitech device tree is useful as a reference even for other wheels
        var allDev = new StringBuilder();
        allDev.AppendLine("=== All Logitech + sim-wheel devices (VID match) ===");
        DumpDeviceMatches(allDev, [], new[] { "046D", "0EB7", "346E", "044F", "21D1", "231D", "0F0D", "26B4" });
        WriteEntry(zip, "Generic/sim_devices.txt", allDev.ToString());
        found.Add("Generic/sim_devices.txt");

        // Game FFB configs
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (Directory.Exists(docs))
        {
            foreach (var dir in Directory.GetDirectories(docs))
            {
                var dn = Path.GetFileName(dir);
                if (!MatchesKeywords(dn, new[]
                        { "assetto", "race", "le mans", "r factor", "rFactor", "r3e", "raceroom", "lmu" }))
                    continue;
                var count = AddFolderBounded(zip, dir, $"GameConfigs/{dn}", found,
                    fileFilter: f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                                     || f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                                     || f.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)
                                     || f.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase)
                                     || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase),
                    log: s => { });
                if (count > 0)
                    found.Add($"GameConfigs/{dn}: {count} file(s)");
            }
        }

        // Tuner app data (logs + profiles)
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AcEvoFfbTuner");
        if (Directory.Exists(appData))
        {
            var count = AddFolderBounded(zip, appData, "Tuner", found,
                includeSubdirs: new[] { "Profiles" }, log: s => { });
            if (count > 0)
                found.Add($"Tuner: {count} file(s)");
        }

        // WER crash dumps for the tuner
        var crashDumps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CrashDumps");
        if (Directory.Exists(crashDumps))
        {
            foreach (var d in Directory.GetFiles(crashDumps, "AcEvoFfbTuner.exe*.dmp")
                         .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                         .Take(5))
            {
                try
                {
                    var fi = new FileInfo(d);
                    if (fi.Length > 30L * 1024 * 1024) continue;
                    WriteBytesEntry(zip, $"crashdumps/{fi.Name}", File.ReadAllBytes(d));
                    found.Add($"crashdumps/{fi.Name}");
                }
                catch { }
            }
        }
    }

    // ─────────────────────────── helpers ───────────────────────────────────

    private static bool MatchesKeywords(string text, string[] keywords)
    {
        var t = text.ToLowerInvariant();
        foreach (var k in keywords)
            if (t.Contains(k.ToLowerInvariant()))
                return true;
        return false;
    }

    /// <summary>Bounded copy of a folder: newest files first, ~2 MB per file,
    /// ~8 MB per folder, optional subdir inclusion and file filter.</summary>
    private static int AddFolderBounded(ZipArchive zip, string folder, string entryPrefix, List<string> found,
        string[]? includeSubdirs = null, Func<string, bool>? fileFilter = null, Action<string>? log = null)
    {
        int count = 0;
        long budget = 8L * 1024 * 1024;
        try
        {
            void AddFile(string file)
            {
                try
                {
                    if (budget <= 0) return;
                    var fi = new FileInfo(file);
                    if (fi.Length > 2L * 1024 * 1024) return;
                    if (fileFilter != null && !fileFilter(file)) return;
                    WriteBytesEntry(zip, $"{entryPrefix}/{fi.Name}", File.ReadAllBytes(file));
                    budget -= fi.Length;
                    count++;
                }
                catch { }
            }

            foreach (var f in Directory.GetFiles(folder)
                         .OrderByDescending(f => new FileInfo(f).LastWriteTime))
                AddFile(f);

            if (includeSubdirs != null)
            {
                foreach (var sub in includeSubdirs)
                {
                    var subPath = Path.Combine(folder, sub);
                    if (!Directory.Exists(subPath)) continue;
                    foreach (var f in Directory.GetFiles(subPath))
                        AddFile(f);
                }
            }
        }
        catch { }
        return count;
    }

    private static void DumpRegistryMatches(StringBuilder sb, string[] keywords, string label)
    {
        try
        {
            foreach (var root in new[]
                     {
                         @"SOFTWARE", @"SOFTWARE\WOW6432Node"
                     })
            {
                using var baseKey = Registry.LocalMachine.OpenSubKey(root);
                if (baseKey == null) continue;
                foreach (var sub in baseKey.GetSubKeyNames())
                {
                    if (!MatchesKeywords(sub, keywords)) continue;
                    using var key = baseKey.OpenSubKey(sub);
                    sb.AppendLine($"HKLM\\{root}\\{sub}");
                    if (key != null)
                    {
                        foreach (var val in key.GetValueNames())
                        {
                            if (val.Equals("InstallPath", StringComparison.OrdinalIgnoreCase)
                                || val.Equals("InstallDir", StringComparison.OrdinalIgnoreCase)
                                || val.Equals("Path", StringComparison.OrdinalIgnoreCase)
                                || val.Equals("Version", StringComparison.OrdinalIgnoreCase)
                                || val.Equals("DisplayVersion", StringComparison.OrdinalIgnoreCase))
                                sb.AppendLine($"    {val} = {key.GetValue(val)}");
                        }
                    }
                }
            }

            foreach (var root in new[]
                     {
                         @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                         @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                     })
            {
                using var uninstall = Registry.LocalMachine.OpenSubKey(root);
                if (uninstall == null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var app = uninstall.OpenSubKey(name);
                    string? disp = app?.GetValue("DisplayName") as string;
                    if (string.IsNullOrEmpty(disp) || !MatchesKeywords(disp, keywords)) continue;
                    sb.AppendLine($"Installed: {disp} - {app?.GetValue("DisplayVersion")} ({app?.GetValue("Publisher")})");
                }
            }

            if (sb.Length == 0)
                sb.AppendLine($"{label}: no registry entries found (software not installed?)");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"ERROR: {ex.Message}");
        }
    }

    private static void DumpDeviceMatches(StringBuilder sb, string[] keywords, string[] vids)
    {
        int foundCount = 0;
        try
        {
            foreach (var hive in new[] { "USB", "HID" })
            {
                using var root = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{hive}");
                if (root == null) continue;
                foreach (var sub in root.GetSubKeyNames())
                {
                    bool vidMatch = vids.Length > 0 &&
                                    vids.Any(v => sub.StartsWith($"VID_{v}", StringComparison.OrdinalIgnoreCase));
                    bool nameMatch = keywords.Length > 0 && MatchesKeywords(sub, keywords);
                    if (!vidMatch && !nameMatch) continue;
                    using var devKey = root.OpenSubKey(sub);
                    if (devKey == null) continue;
                    string? desc = devKey.GetValue("DeviceDesc") as string;
                    if (!vidMatch && desc != null && !MatchesKeywords(desc, keywords)) continue;
                    foundCount++;
                    sb.AppendLine();
                    sb.AppendLine($"[{hive}\\{sub}]");
                    sb.AppendLine($"  DeviceDesc:   {desc}");
                    sb.AppendLine($"  FriendlyName: {devKey.GetValue("FriendlyName") as string}");
                    sb.AppendLine($"  Mfg:          {devKey.GetValue("Mfg") as string}");
                    sb.AppendLine($"  Service:      {devKey.GetValue("Service") as string}");
                    try
                    {
                        if (devKey.GetValue("HardwareID") is string[] hw)
                            sb.AppendLine($"  HardwareIDs:  {string.Join("; ", hw)}");
                    }
                    catch { }
                    foreach (var child in devKey.GetSubKeyNames())
                    {
                        using var childKey = devKey.OpenSubKey(child);
                        if (childKey == null) continue;
                        sb.AppendLine($"  [{child}]");
                        sb.AppendLine($"    DeviceDesc:   {childKey.GetValue("DeviceDesc") as string}");
                        sb.AppendLine($"    FriendlyName: {childKey.GetValue("FriendlyName") as string}");
                        sb.AppendLine($"    Service:      {childKey.GetValue("Service") as string}");
                    }
                }
            }
            if (foundCount == 0)
                sb.AppendLine("(no matching devices found)");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"ERROR: {ex.Message}");
        }
    }

    private static string BuildSystemInfo()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== System ===");
        try
        {
            using var os = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            sb.AppendLine($"Windows: {os?.GetValue("ProductName")} {os?.GetValue("DisplayVersion")} (build {os?.GetValue("CurrentBuildNumber")})");
        }
        catch { }
        sb.AppendLine($"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"User: {Environment.UserName}");
        sb.AppendLine($"Machine: {Environment.MachineName}");
        sb.AppendLine();
        sb.AppendLine("=== Logitech wheel SDK bridge (reference) ===");
        try
        {
            const string clsid = @"CLSID\{63BD165D-1584-4E75-AB56-08330350545F}";
            using var clsidKey = Registry.ClassesRoot.OpenSubKey(clsid);
            sb.AppendLine(clsidKey != null
                ? $"CLSID: {clsidKey.GetValue(null) as string}"
                : "CLSID: not registered (no Logitech software installed)");
            using var bin = Registry.ClassesRoot.OpenSubKey(clsid + @"\ServerBinary");
            if (bin?.GetValue(null) is string dllPath && File.Exists(dllPath))
            {
                var vi = FileVersionInfo.GetVersionInfo(dllPath);
                sb.AppendLine($"Bridge DLL: {dllPath}");
                sb.AppendLine($"Bridge version: {vi.FileVersion} ({vi.ProductVersion})");
            }
            else
            {
                sb.AppendLine("Bridge DLL: (not found)");
            }
        }
        catch (Exception ex) { sb.AppendLine($"ERROR: {ex.Message}"); }
        return sb.ToString();
    }

    private static string BuildProcessInfo()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Running wheel/software related processes ===");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        try
        {
            foreach (var p in Process.GetProcesses().OrderBy(p => p.ProcessName))
            {
                var n = p.ProcessName.ToLowerInvariant();
                if (n.Contains("lghub") || n.Contains("logi") || n.Contains("fanatec")
                    || n.Contains("moza") || n.Contains("pithouse") || n.Contains("simagic")
                    || n.Contains("simpro") || n.Contains("simucube") || n.Contains("truedrive")
                    || n.Contains("granite") || n.Contains("thrustmaster") || n.Contains("asetek")
                    || n.Contains("simhub") || n.Contains("ghub") || n.Contains("steering")
                    || n.Contains("trueforce") || n.Contains("wheel"))
                {
                    sb.AppendLine($"PROCESS: {p.ProcessName} (PID {p.Id})");
                }
            }
        }
        catch (Exception ex) { sb.AppendLine($"ERROR: {ex.Message}"); }
        return sb.ToString();
    }

    private static void WriteEntry(ZipArchive zip, string entryName, string content)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var dst = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        dst.Write(bytes, 0, bytes.Length);
    }

    private static void WriteBytesEntry(ZipArchive zip, string entryName, byte[] content)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var dst = entry.Open();
        dst.Write(content, 0, content.Length);
    }
}
