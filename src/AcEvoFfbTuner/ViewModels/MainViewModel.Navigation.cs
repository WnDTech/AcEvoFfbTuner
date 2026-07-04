using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AcEvoFfbTuner.Core;
using AcEvoFfbTuner.Core.DirectInput;
using AcEvoFfbTuner.Core.FfbProcessing;
using AcEvoFfbTuner.Core.FfbProcessing.Models;
using AcEvoFfbTuner.Core.FfbProviders;
using AcEvoFfbTuner.Core.Profiles;
using AcEvoFfbTuner.Core.SharedMemory;
using AcEvoFfbTuner.Core.TrackMapping;
using AcEvoFfbTuner.Services;
using AcEvoFfbTuner.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AcEvoFfbTuner.ViewModels;

public sealed partial class MainViewModel
{
    [RelayCommand]
    private void DismissGameFfbWarning()
    {
        ShowGameFfbWarning = false;
        _ffbWarningDismissed = true;
    }

    [RelayCommand]
    private void DismissConflictingAppsWarning()
    {
        ShowConflictingAppsWarning = false;
        _conflictingAppsWarningDismissed = true;
    }

    private void CheckConflictingApps()
    {
        _conflictingAppsCheckCounter++;
        if (_conflictingAppsCheckCounter % 30 != 0 && !ShowConflictingAppsWarning) return;

        var result = Services.ConflictingAppDetector.Detect();
        if (result.HasConflicts)
        {
            ConflictingAppsNames = string.Join(", ", result.DetectedApps.Select(a => a.DisplayName));
            var sb = new System.Text.StringBuilder();
            foreach (var app in result.DetectedApps)
                sb.AppendLine($"  • {app.DisplayName} — {app.Reason}");
            ConflictingAppsDetail = sb.ToString();

            if (!_conflictingAppsWarningDismissed)
            {
                ShowConflictingAppsWarning = true;
                AddSystemLog($"Conflicting FFB apps detected: {ConflictingAppsNames}");
            }
        }
        else
        {
            ShowConflictingAppsWarning = false;
            ConflictingAppsNames = "";
            ConflictingAppsDetail = "";
            _conflictingAppsWarningDismissed = false;
        }
    }
}
