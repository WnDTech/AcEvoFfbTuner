using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using AcEvoFfbTuner.Services;
using AcEvoFfbTuner.ViewModels;

namespace AcEvoFfbTuner.Views.Pages;

public partial class SettingsPage : UserControl
{
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner");

    public event EventHandler? TestingGuideRequested;
    public event EventHandler? CalibrationWizardRequested;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.SystemLogEntries.CollectionChanged -= OnLogChanged;
            vm.SystemLogEntries.CollectionChanged += OnLogChanged;
        }
        DataContextChanged += (s, args) =>
        {
            if (args.OldValue is MainViewModel oldVm)
                oldVm.SystemLogEntries.CollectionChanged -= OnLogChanged;
            if (args.NewValue is MainViewModel newVm)
                newVm.SystemLogEntries.CollectionChanged += OnLogChanged;
        };
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (SystemLogList.Items.Count > 0)
                SystemLogList.ScrollIntoView(SystemLogList.Items[SystemLogList.Items.Count - 1]);
        });
    }

    private void OnSystemLogSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox lb)
            lb.UnselectAll();
    }

    private void OnCopyDebugToClipboard(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            Clipboard.SetText(vm.DebugSnapshot);
            vm.StatusText = "Debug info copied to clipboard";
        }
    }

    private void OnOpenTestingGuide(object sender, RoutedEventArgs e)
    {
        TestingGuideRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnOpenCalibrationWizard(object sender, RoutedEventArgs e)
    {
        CalibrationWizardRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnOpenLogFolder(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(AppDataDir))
        {
            Directory.CreateDirectory(AppDataDir);
        }
        Process.Start(new ProcessStartInfo(AppDataDir) { UseShellExecute = true });
    }

    private void OnZipAllLogs(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        try
        {
            vm.ZipAllLogsStatus = "Creating ZIP...";

            // Include the crash event-log export (and crash.dmp below via *.txt/*.log
            // collection — the dump is picked up explicitly).
            DiagnosticPackService.WriteCrashEventLogExport();

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var zipPath = Path.Combine(Path.GetTempPath(), $"AcEvoFfbTuner_Logs_{timestamp}.zip");

            using (var fs = new FileStream(zipPath, FileMode.Create))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                AddDirectoryToZip(zip, AppDataDir, "", "*.log");
                AddDirectoryToZip(zip, AppDataDir, "", "*.txt");
                AddDirectoryToZip(zip, AppDataDir, "Profiles", "*.json");
                AddDirectoryToZip(zip, AppDataDir, "snapshots", "*.csv");
                AddDirectoryToZip(zip, AppDataDir, "snapshots", "*.html");
                AddDirectoryToZip(zip, AppDataDir, "snapshots", "*.txt");
                AddDirectoryToZip(zip, AppDataDir, "TrackMaps", "*.json");
                AddCrashDumpToZip(zip);
            }

            var zipInfo = new FileInfo(zipPath);
            var sizeMb = zipInfo.Length / (1024.0 * 1024.0);
            vm.ZipAllLogsStatus = $"ZIP created ({sizeMb:F1} MB): {zipPath}";

            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{zipPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            vm.ZipAllLogsStatus = $"ZIP failed: {ex.Message}";
        }
    }

    private static void AddDirectoryToZip(ZipArchive zip, string baseDir, string subDir, string searchPattern)
    {
        var dir = Path.Combine(baseDir, subDir);
        if (!Directory.Exists(dir)) return;

        foreach (var file in Directory.GetFiles(dir, searchPattern, SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(baseDir, file);
            zip.CreateEntryFromFile(file, relativePath, CompressionLevel.Optimal);
        }
    }

    private static void AddCrashDumpToZip(ZipArchive zip)
    {
        try
        {
            var dumpPath = Path.Combine(AppDataDir, "crash.dmp");
            if (File.Exists(dumpPath))
                zip.CreateEntryFromFile(dumpPath, "crash.dmp", CompressionLevel.Optimal);
        }
        catch { }
    }
}
