// WheelSetupCollector — a tiny standalone tool for Logitech wheel users.
// It collects the wheel-related setup data (G HUB / LGS files, Logitech
// registry and device-tree info, the FFB tuner's own logs and profiles,
// Windows crash dumps) into a single zip on the Desktop, which the user
// can then send back. No installation, no admin rights, nothing else.

using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using Microsoft.Win32;

Console.WriteLine("Wheel Setup Collector");
Console.WriteLine("=====================");
Console.WriteLine();

var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
var zipName = $"WheelSetup_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
var zipPath = Path.Combine(desktop, zipName);

var found = new List<string>();

try
{
    using var fs = new FileStream(zipPath, FileMode.Create);
    using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

    // ── README ──────────────────────────────────────────────────────────────
    var readme = new StringBuilder();
    readme.AppendLine("Wheel Setup Collector — collected data");
    readme.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    readme.AppendLine();
    readme.AppendLine("Folders in this zip:");
    readme.AppendLine("  ghub/            G HUB / Logitech Gaming Software data (if installed)");
    readme.AppendLine("  tuner/           AC Evo FFB Tuner logs and profiles (if installed)");
    readme.AppendLine("  crashdumps/      Windows crash dumps for the tuner (if any)");
    readme.AppendLine("  info/            Logitech devices, software and system info");
    readme.AppendLine();
    readme.AppendLine("This zip can be sent to the developer as-is.");
    WriteEntry(zip, "README.txt", readme.ToString());

    // ── G HUB / LGS data (bounded: newest files, max 2 MB each, 6 MB per folder) ──
    AddFolder(zip, "%ProgramData%\\LGHUB", "ghub/ProgramData_LGHUB", found);
    AddFolder(zip, "%LOCALAPPDATA%\\LGHUB", "ghub/LocalAppData_LGHUB", found);
    AddFolder(zip, "%ProgramData%\\LogiShrd", "ghub/ProgramData_LogiShrd", found);

    // ── AC Evo FFB Tuner data ───────────────────────────────────────────────
    var appData = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner");
    if (Directory.Exists(appData))
    {
        AddFolder(zip, appData, "tuner/AppData", found, includeSubdirs: new[] { "Profiles" });
    }

    // ── WER crash dumps for the tuner ───────────────────────────────────────
    var crashDumps = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CrashDumps");
    if (Directory.Exists(crashDumps))
    {
        var dumps = Directory.GetFiles(crashDumps, "AcEvoFfbTuner.exe*.dmp")
            .OrderByDescending(f => new FileInfo(f).LastWriteTime)
            .Take(5)
            .ToList();
        foreach (var d in dumps)
        {
            try
            {
                var fi = new FileInfo(d);
                if (fi.Length > 30L * 1024 * 1024) continue;
                WriteBytesEntry(zip, $"crashdumps/{fi.Name}", File.ReadAllBytes(d));
                found.Add($"WER crash dump: {fi.Name} ({fi.Length / 1024} KB)");
            }
            catch { }
        }
    }

    // ── System / Logitech info ──────────────────────────────────────────────
    WriteEntry(zip, "info/system.txt", BuildSystemInfo());
    WriteEntry(zip, "info/logitech_devices.txt", BuildDeviceTreeInfo());
    WriteEntry(zip, "info/logitech_registry.txt", BuildRegistryInfo());

    Console.WriteLine($"Done. {found.Count} item(s) collected.");
    Console.WriteLine();
    Console.WriteLine($"Zip saved to:");
    Console.WriteLine(zipPath);
    Console.WriteLine();
    Console.WriteLine("Send this file to the developer. Press Enter to open the folder...");
    Console.ReadLine();
    try
    {
        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{zipPath}\"");
    }
    catch { }
}
catch (Exception ex)
{
    Console.WriteLine();
    Console.WriteLine($"FAILED: {ex.Message}");
    Console.WriteLine("Press Enter to close...");
    Console.ReadLine();
}

// ─────────────────────────────────────────────────────────────────────────────

static void AddFolder(ZipArchive zip, string folder, string entryPrefix, List<string> found, string[]? includeSubdirs = null)
{
    try
    {
        var path = Environment.ExpandEnvironmentVariables(folder);
        if (!Directory.Exists(path))
        {
            Console.WriteLine($"skip  (not present): {path}");
            return;
        }

        long budget = 6L * 1024 * 1024;
        int count = 0;

        void AddFile(string file)
        {
            try
            {
                var fi = new FileInfo(file);
                if (fi.Length > 2L * 1024 * 1024) return;
                if (budget <= 0) return;
                WriteBytesEntry(zip, $"{entryPrefix}/{fi.Name}", File.ReadAllBytes(file));
                budget -= fi.Length;
                count++;
            }
            catch { }
        }

        foreach (var f in Directory.GetFiles(path).OrderByDescending(f => new FileInfo(f).LastWriteTime))
            AddFile(f);

        if (includeSubdirs != null)
        {
            foreach (var sub in includeSubdirs)
            {
                var subPath = Path.Combine(path, sub);
                if (!Directory.Exists(subPath)) continue;
                foreach (var f in Directory.GetFiles(subPath))
                    AddFile(f);
            }
        }

        if (count > 0)
        {
            found.Add($"{entryPrefix}: {count} file(s)");
            Console.WriteLine($"ok    {entryPrefix}: {count} file(s)");
        }
        else
        {
            Console.WriteLine($"empty {entryPrefix} (no suitable files)");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"warn  {entryPrefix}: {ex.Message}");
    }
}

static void WriteEntry(ZipArchive zip, string entryName, string content)
{
    var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
    using var dst = entry.Open();
    var bytes = Encoding.UTF8.GetBytes(content);
    dst.Write(bytes, 0, bytes.Length);
}

static void WriteBytesEntry(ZipArchive zip, string entryName, byte[] content)
{
    var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
    using var dst = entry.Open();
    dst.Write(content, 0, content.Length);
}

static string BuildSystemInfo()
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
    sb.AppendLine($"Framework: {Environment.Version}");
    sb.AppendLine();
    sb.AppendLine("=== Logitech wheel SDK bridge ===");
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

static string BuildDeviceTreeInfo()
{
    var sb = new StringBuilder();
    sb.AppendLine("=== Logitech USB devices (VID_046D) from the Windows device tree ===");
    try
    {
        foreach (var hive in new[] { "USB", "HID" })
        {
            using var root = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{hive}");
            if (root == null) continue;
            foreach (var sub in root.GetSubKeyNames()
                         .Where(n => n.StartsWith("VID_046D", StringComparison.OrdinalIgnoreCase)))
            {
                using var devKey = root.OpenSubKey(sub);
                if (devKey == null) continue;
                sb.AppendLine();
                sb.AppendLine($"[{hive}\\{sub}]");
                sb.AppendLine($"  DeviceDesc:   {devKey.GetValue("DeviceDesc") as string}");
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
    }
    catch (Exception ex) { sb.AppendLine($"ERROR: {ex.Message}"); }
    return sb.ToString();
}

static string BuildRegistryInfo()
{
    var sb = new StringBuilder();
    sb.AppendLine("=== Logitech software / G HUB install state ===");
    try
    {
        using var logi = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Logitech");
        if (logi != null)
            foreach (var name in logi.GetSubKeyNames())
                sb.AppendLine($"HKLM\\SOFTWARE\\Logitech\\{name}");
        else
            sb.AppendLine("HKLM\\SOFTWARE\\Logitech: not present (Logitech software not installed)");
    }
    catch (Exception ex) { sb.AppendLine($"ERROR: {ex.Message}"); }
    try
    {
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
                if (string.IsNullOrEmpty(disp) || !disp.Contains("ogitech", StringComparison.OrdinalIgnoreCase)) continue;
                sb.AppendLine($"Installed: {disp} — {app?.GetValue("DisplayVersion")} ({app?.GetValue("Publisher")})");
            }
        }
    }
    catch (Exception ex) { sb.AppendLine($"ERROR: {ex.Message}"); }
    return sb.ToString();
}
