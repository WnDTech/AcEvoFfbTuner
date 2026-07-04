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
    partial void OnSnapshotButtonComboIndexChanged(int value)
    {
        SnapshotButtonIndex = value <= 0 ? -1 : value - 1;
        _appSettings.SnapshotButtonComboIndex = value;
        if (!_suppressSettingsSave)
            _appSettings.Save();
    }

    partial void OnPanicButtonComboIndexChanged(int value)
    {
        _appSettings.PanicButtonComboIndex = value;
        _appSettings.Save();
    }

    private void PollSnapshotButton()
    {
        if (!IsDeviceConnected && !IsAssigningSnapshotButton) return;

        var buttons = _deviceManager.PollButtons();
        if (buttons == null)
        {
            ButtonDetectionText = "";
        }
        else
        {
            var pressed = new System.Text.StringBuilder();
            for (int i = 0; i < buttons.Length; i++)
            {
                if (buttons[i])
                {
                    if (pressed.Length > 0) pressed.Append(", ");
                    string btnName = i < SnapshotButtonNames.Count - 1
                        ? SnapshotButtonNames[i + 1]
                        : $"Btn{i + 1}";
                    pressed.Append(btnName);
                }
            }

            ButtonDetectionText = pressed.Length > 0 ? $"Pressed: {pressed}" : "";

            if (IsAssigningSnapshotButton)
            {
                for (int i = 0; i < buttons.Length; i++)
                {
                    bool prev = _prevSnapshotButtons != null && i < _prevSnapshotButtons.Length && _prevSnapshotButtons[i];
                    if (buttons[i] && !prev)
                    {
                        SnapshotButtonComboIndex = i + 1;
                        IsAssigningSnapshotButton = false;
                        string name = i < SnapshotButtonNames.Count - 1 ? SnapshotButtonNames[i + 1] : $"Button {i + 1}";
                        SnapshotAssignStatus = $"Assigned: {name}";
                        break;
                    }
                }
                _prevSnapshotButtons = (bool[])buttons.Clone();
            }
            else if (SnapshotButtonIndex >= 0 && SnapshotButtonIndex < buttons.Length)
            {
                bool pressed2 = buttons[SnapshotButtonIndex];
                bool wasPressed = _prevSnapshotButtons != null && SnapshotButtonIndex < _prevSnapshotButtons.Length && _prevSnapshotButtons[SnapshotButtonIndex];
                _prevSnapshotButtons = (bool[])buttons.Clone();

                if (pressed2 && !wasPressed)
                {
                    if (Application.Current?.MainWindow is MainWindow mw3)
                    {
                        string path = mw3.AutoSaveSnapshot();
                        StatusText = $"Wheel snapshot saved: {Path.GetFileName(path)}";
                        _telemetryLoop.LiveServer.TriggerSnapshot();
                        _voiceService.Speak("Snapshot saved");
                    }
                }
            }
        }

        PollPanicButton();
    }

    private void PollPanicButton()
    {
        if (PanicButtonIndex < 0 && !IsAssigningPanicButton) return;

        var buttons = _deviceManager.PollSecondaryButtons();
        if (buttons == null) return;

        if (IsAssigningPanicButton)
        {
            for (int i = 0; i < buttons.Length; i++)
            {
                bool prev = _prevPanicButtons != null && i < _prevPanicButtons.Length && _prevPanicButtons[i];
                if (buttons[i] && !prev)
                {
                    PanicButtonComboIndex = i + 1;
                    IsAssigningPanicButton = false;
                    string name = i < PanicButtonNames.Count - 1 ? PanicButtonNames[i + 1] : $"Button {i + 1}";
                    PanicAssignStatus = $"Assigned: {name}";
                    break;
                }
            }
            _prevPanicButtons = (bool[])buttons.Clone();
            return;
        }

        if (PanicButtonIndex < 0 || PanicButtonIndex >= buttons.Length) return;

        bool pressed = buttons[PanicButtonIndex];
        bool wasPressed = _prevPanicButtons != null && PanicButtonIndex < _prevPanicButtons.Length && _prevPanicButtons[PanicButtonIndex];
        _prevPanicButtons = (bool[])buttons.Clone();

        if (pressed && !wasPressed)
            PanicStop();
    }

    private void RefreshSnapshotButtonNames()
    {
        int savedIndex = SnapshotButtonComboIndex;
        _suppressSettingsSave = true;
        try
        {
            var names = _deviceManager.GetButtonNames();
            SnapshotButtonNames.Clear();
            SnapshotButtonNames.Add("Disabled");
            foreach (var name in names)
                SnapshotButtonNames.Add(name);
        }
        finally
        {
            _suppressSettingsSave = false;
            SnapshotButtonComboIndex = savedIndex;
        }
    }

    private void RefreshPanicButtonNames()
    {
        var names = _deviceManager.GetSecondaryButtonNames();
        PanicButtonNames.Clear();
        PanicButtonNames.Add("Disabled");
        foreach (var name in names)
            PanicButtonNames.Add(name);
    }

    private static void PopulateFallbackButtonNames(ObservableCollection<string> collection, int count)
    {
        collection.Clear();
        collection.Add("Disabled");
        for (int i = 1; i <= count; i++)
            collection.Add($"Button {i}");
    }

    [RelayCommand]
    private void AssignSnapshotButton()
    {
        if (!IsDeviceConnected)
        {
            StatusText = "Connect a wheel device first";
            return;
        }
        IsAssigningSnapshotButton = true;
        SnapshotAssignStatus = "Listening... press a button on your wheel";
    }

    [RelayCommand]
    private void AssignPanicButton()
    {
        if (SelectedPanicDevice == null || SelectedPanicDevice.ProductName == "None")
        {
            StatusText = "Select a panic device first";
            return;
        }
        IsAssigningPanicButton = true;
        PanicAssignStatus = "Listening... press a button on the panic device";
    }

    partial void OnSelectedPanicDeviceChanged(FfbDeviceInfo? value)
    {
        _deviceManager.DisconnectSecondaryDevice();

        if (value == null || value.ProductName == "None")
        {
            OnPropertyChanged(nameof(PanicDeviceButtonCount));
            _appSettings.PanicDeviceInstanceId = null;
            _appSettings.Save();
            RefreshPanicButtonNames();
            return;
        }

        var window = Application.Current?.MainWindow;
        if (window != null)
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(window);
            _deviceManager.SetWindowHandle(helper.Handle);
        }

        if (_deviceManager.TryConnectSecondaryDevice(value))
        {
            StatusText = $"Panic button device: {value.ProductName} ({_deviceManager.SecondaryButtonCount} buttons)";
            _appSettings.PanicDeviceInstanceId = value.DeviceInstance.InstanceGuid.ToString();
            _appSettings.Save();
            if (!_uiUpdateTimer.IsEnabled)
                _uiUpdateTimer.Start();
        }
        else
        {
            StatusText = "Failed to connect panic button device";
            _appSettings.PanicDeviceInstanceId = null;
            _appSettings.Save();
        }

        OnPropertyChanged(nameof(PanicDeviceButtonCount));
        RefreshPanicButtonNames();
        RestoreButtonSettings();
    }

    [RelayCommand]
    private void ToggleRaceInfoOverlay()
    {
        if (_raceInfoOverlay != null)
        {
            _raceInfoOverlay.Close();
            _raceInfoOverlay = null;
        }
        else
        {
            if (!_uiUpdateTimer.IsEnabled) _uiUpdateTimer.Start();
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                _raceInfoOverlay = new RaceInfoOverlay();
                _raceInfoOverlay.Closed += (_, _) => _raceInfoOverlay = null;
                _raceInfoOverlay.Show();
            });
        }
    }

    private void ShowSnapshotPicker()
    {
        var files = SnapshotFileLoader.LoadSnapshotFiles();
        if (files.Count == 0)
        {
            CoachMessages.Add(new CoachMessage { Text = "No saved snapshots found. Take a snapshot first (wheel button or Telemetry page), or use live data.", Icon = "📭" });
            return;
        }

        CoachSessionState = CoachSessionState.SelectingSource;
        List<CoachAnswer> answers = [];
        foreach (var f in files.Take(20))
        {
            answers.Add(new CoachAnswer
            {
                Id = "snap_" + f.FilePath,
                Label = f.Timestamp.ToString("MMM dd, HH:mm:ss"),
                Description = f.ProfileName
            });
        }
        answers.Add(new CoachAnswer { Id = "go_back", Label = "← Back to options" });

        CoachMessages.Add(new CoachMessage
        {
            Text = $"Found {files.Count} snapshot{(files.Count > 1 ? "s" : "")}. Select one to analyze:",
            Answers = answers
        });
    }
}
