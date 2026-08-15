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
        if (Environment.GetCommandLineArgs().Contains("--auto"))
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
}

internal sealed class CollectorForm : Form
{
    private readonly CheckedListBox _manufacturers;
    private readonly Button _collectButton;
    private readonly ListBox _log;
    private readonly Label _status;

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
        ClientSize = new Size(600, 560);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
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
            IntegralHeight = false
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

        _status = new Label
        {
            Text = "",
            Location = new Point(12, 347),
            Size = new Size(576, 20)
        };
        Controls.Add(_status);

        _log = new ListBox
        {
            Location = new Point(12, 372),
            Size = new Size(576, 176),
            IntegralHeight = false,
            Font = new Font("Consolas", 9f)
        };
        Controls.Add(_log);
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
