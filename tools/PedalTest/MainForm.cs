using System.Net.Http;
using System.Text.Json;
using AcEvoFfbTuner.Core.Config;
using AcEvoFfbTuner.Core.PedalInput.Sources;

namespace PedalTest;

public sealed class MainForm : Form
{
    // ── Connected to the running FFB app via HTTP ──
    private readonly HttpClient _http = new();
    private readonly System.Windows.Forms.Timer _pollTimer = new();
    private string _baseUrl = "http://localhost:8321";

    // ── Controls: Connection ──
    private readonly TextBox _txtUrl = new();
    private readonly Button _btnConnect = new();
    private readonly Label _lblConnStatus = new();

    // ── Controls: Input ──
    private readonly Label _lblSource = new();
    private readonly ProgressBar _pbGas = new(), _pbBrake = new(), _pbClutch = new();
    private readonly Label _lblGasVal = new(), _lblBrakeVal = new(), _lblClutchVal = new();
    private readonly Label _lblGasRaw = new(), _lblBrakeRaw = new(), _lblClutchRaw = new();

    // ── Controls: Device selector ──
    private readonly ComboBox _cmbDevice = new();
    private readonly Label _lblDeviceStatus = new();
    private int _selectedDeviceIndex; // 0 = wheelbase, 1+ = DI device

    // ── Controls: Axis details ──
    private readonly Label _lblAxX = new(), _lblAxY = new(), _lblAxZ = new();
    private readonly Label _lblAxRx = new(), _lblAxRy = new(), _lblAxRz = new();
    private readonly Label _lblAxSl0 = new(), _lblAxSl1 = new();

    // ── Controls: Axis mapping ──
    private ComboBox _cmbGasAxis = null!, _cmbBrakeAxis = null!, _cmbClutchAxis = null!;
    private CheckBox _chkGasInv = null!, _chkBrakeInv = null!, _chkClutchInv = null!;

    // ── Controls: Calibration ──
    private readonly NumericUpDown _numDeadzone = new();
    private readonly CheckBox _chkInvert = new();
    private readonly Button _btnApply = new();
    private readonly CheckBox _chkEnablePedals = new();

    // ── Controls: Simulated pedal haptics ──
    private readonly ProgressBar _pbHapticAbs = new(), _pbHapticTc = new(), _pbHapticCurb = new();
    private readonly ProgressBar _pbHapticRoad = new(), _pbHapticScrub = new(), _pbHapticBrakePressure = new();
    private readonly Label _lblHapticAbs = new(), _lblHapticTc = new(), _lblHapticCurb = new();
    private readonly Label _lblHapticRoad = new(), _lblHapticScrub = new(), _lblHapticBrakePressure = new();

    // ── Controls: Haptic gain sliders ──
    private readonly NumericUpDown _numAbsGain = new(), _numTcGain = new(), _numCurbGain = new();
    private readonly NumericUpDown _numRoadGain = new(), _numScrubGain = new(), _numBrakePressureGain = new();
    private readonly NumericUpDown _numBrakeMasterGain = new(), _numGasMasterGain = new();
    private readonly Label _lblAbsGainVal = new(), _lblTcGainVal = new(), _lblCurbGainVal = new();
    private readonly Label _lblRoadGainVal = new(), _lblScrubGainVal = new(), _lblBrakePressureGainVal = new();

    // ── Cached raw haptic signals (before gain) ──
    private float _rawAbs, _rawTc, _rawCurb, _rawRoad, _rawScrub, _rawBrakePress;

    // ── Logging ──
    private readonly ListBox _lstLog = new();
    private int _logSeq;

    // ── Cached API data ──
    private JsonDocument? _lastData;
    private bool _connected;
    private volatile bool _polling;
    private int _consecutiveFailures;

    public MainForm()
    {
        _http.Timeout = TimeSpan.FromSeconds(3);

        Text = "Pedal Test — diagnostics";
        Size = new Size(900, 780);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Consolas", 10);
        BackColor = Color.FromArgb(30, 30, 30);
        ForeColor = Color.FromArgb(220, 220, 220);

        BuildUI();
        WireEvents();

        _pollTimer.Interval = 250;
        _pollTimer.Tick += PollTick;
        _pollTimer.Start();
    }

    private void BuildUI()
    {
        // ── Title ──
        Controls.Add(new Label
        {
            Text = "Pedal Test — connected to running FFB App",
            Font = new Font("Consolas", 14, FontStyle.Bold),
            ForeColor = Color.FromArgb(79, 195, 247),
            Location = new Point(16, 10), Size = new Size(600, 28)
        });

        // ── Row 1: Connection ──
        var lblUrl = new Label { Text = "FFB App URL:", Location = new Point(16, 46), Size = new Size(100, 22), ForeColor = Color.FromArgb(200, 200, 200) };
        _txtUrl.Text = _baseUrl;
        _txtUrl.Location = new Point(120, 44); _txtUrl.Size = new Size(200, 22);
        _txtUrl.BackColor = Color.FromArgb(40, 40, 50); _txtUrl.ForeColor = Color.FromArgb(220, 220, 220);
        _txtUrl.BorderStyle = BorderStyle.FixedSingle;

        _btnConnect.Text = "Connect"; _btnConnect.Location = new Point(330, 43); _btnConnect.Size = new Size(80, 24);
        _btnConnect.FlatStyle = FlatStyle.Flat; _btnConnect.BackColor = Color.FromArgb(50, 50, 50);
        _btnConnect.ForeColor = Color.FromArgb(200, 200, 200);
        _btnConnect.FlatAppearance.BorderColor = Color.FromArgb(100, 100, 100);

        _lblConnStatus.Location = new Point(420, 46); _lblConnStatus.Size = new Size(400, 20);
        _lblConnStatus.ForeColor = Color.FromArgb(150, 150, 150);
        _lblConnStatus.Text = "Click Connect to fetch pedal data from FFB app";

        Controls.AddRange([lblUrl, _txtUrl, _btnConnect, _lblConnStatus]);

        // ── Row 2: Pedal bars + Device selector ──
        _lblSource.Location = new Point(16, 76); _lblSource.Size = new Size(400, 20); _lblSource.Text = "Source: —";
        Controls.Add(_lblSource);

        int topRowY = 100;
        var grpInput = new GroupBox { Text = "Calibrated Pedal Values", Location = new Point(16, topRowY), Size = new Size(400, 130), ForeColor = Color.FromArgb(79, 195, 247) };
        int y = 20;
        AddPedalBar(grpInput, ref y, "Gas",   _pbGas,   _lblGasRaw,   _lblGasVal);
        AddPedalBar(grpInput, ref y, "Brake", _pbBrake, _lblBrakeRaw, _lblBrakeVal);
        AddPedalBar(grpInput, ref y, "Clutch", _pbClutch, _lblClutchRaw, _lblClutchVal);
        Controls.Add(grpInput);

        // Device selector
        var grpDev = new GroupBox { Text = "Device", Location = new Point(430, topRowY), Size = new Size(450, 130), ForeColor = Color.FromArgb(100, 180, 255) };
        var lblDev = new Label { Text = "Available devices:", Location = new Point(12, 22), Size = new Size(120, 22), ForeColor = Color.FromArgb(200, 200, 200) };
        _cmbDevice.Location = new Point(136, 20); _cmbDevice.Size = new Size(290, 22);
        _cmbDevice.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbDevice.BackColor = Color.FromArgb(40, 40, 50); _cmbDevice.ForeColor = Color.FromArgb(220, 220, 220);

        _lblDeviceStatus.Location = new Point(12, 48); _lblDeviceStatus.Size = new Size(420, 18);
        _lblDeviceStatus.ForeColor = Color.FromArgb(150, 150, 150); _lblDeviceStatus.Text = "";

        string[] axes = DirectInputPedalSource.AxisMap.AvailableAxes;
        var mapTitle = new Label { Text = "AXIS MAPPING", Location = new Point(12, 70), Size = new Size(420, 16), ForeColor = Color.FromArgb(100, 180, 255), BackColor = Color.Transparent, Font = new Font("Consolas", 9, FontStyle.Bold) };

        _cmbGasAxis = MakeMappingCombo(12, 88, axes, 0); _cmbGasAxis.SelectedIndex = Array.IndexOf(axes, "Y");
        _cmbBrakeAxis = MakeMappingCombo(158, 88, axes, 0); _cmbBrakeAxis.SelectedIndex = Array.IndexOf(axes, "Z");
        _cmbClutchAxis = MakeMappingCombo(304, 88, axes, 0); _cmbClutchAxis.SelectedIndex = Array.IndexOf(axes, "Rx");

        var labGas = new Label { Text = "→ Gas", Location = new Point(12+60, 88), Size = new Size(38, 22), ForeColor = Color.FromArgb(76, 175, 80), BackColor = Color.Transparent };
        var labBrake = new Label { Text = "→ Brake", Location = new Point(158+60, 88), Size = new Size(44, 22), ForeColor = Color.FromArgb(229, 57, 53), BackColor = Color.Transparent };
        var labClutch = new Label { Text = "→ Clutch", Location = new Point(304+60, 88), Size = new Size(44, 22), ForeColor = Color.FromArgb(255, 193, 7), BackColor = Color.Transparent };

        _chkGasInv = new CheckBox { Text = "Gas Inv", Location = new Point(12, 112), Size = new Size(68, 18), ForeColor = Color.FromArgb(180, 180, 180), BackColor = Color.Transparent };
        _chkBrakeInv = new CheckBox { Text = "Brake Inv", Location = new Point(158, 112), Size = new Size(74, 18), ForeColor = Color.FromArgb(180, 180, 180), BackColor = Color.Transparent };
        _chkClutchInv = new CheckBox { Text = "Clutch Inv", Location = new Point(304, 112), Size = new Size(74, 18), ForeColor = Color.FromArgb(180, 180, 180), BackColor = Color.Transparent };

        grpDev.Controls.AddRange([lblDev, _cmbDevice, _lblDeviceStatus, mapTitle,
            _cmbGasAxis, labGas, _chkGasInv, _cmbBrakeAxis, labBrake, _chkBrakeInv, _cmbClutchAxis, labClutch, _chkClutchInv]);
        Controls.Add(grpDev);

        // ── Row 3: Raw axes + Calibration ──
        int midRowY = topRowY + 140;
        var grpAxes = new GroupBox { Text = "Raw DirectInput Axes", Location = new Point(16, midRowY), Size = new Size(400, 190), ForeColor = Color.FromArgb(100, 180, 255) };
        y = 18;
        AddAxisLabel(grpAxes, ref y, "X", _lblAxX, Color.FromArgb(200, 200, 200));
        AddAxisLabel(grpAxes, ref y, "Y", _lblAxY, Color.FromArgb(180, 180, 180));
        AddAxisLabel(grpAxes, ref y, "Z", _lblAxZ, Color.FromArgb(180, 180, 180));
        y += 2;
        AddAxisLabel(grpAxes, ref y, "Rx (rot)", _lblAxRx, Color.FromArgb(255, 193, 7));
        AddAxisLabel(grpAxes, ref y, "Ry (rot)", _lblAxRy, Color.FromArgb(150, 150, 150));
        AddAxisLabel(grpAxes, ref y, "Rz (rot)", _lblAxRz, Color.FromArgb(150, 150, 150));
        y += 2;
        AddAxisLabel(grpAxes, ref y, "Slider 0", _lblAxSl0, Color.FromArgb(150, 150, 150));
        AddAxisLabel(grpAxes, ref y, "Slider 1", _lblAxSl1, Color.FromArgb(150, 150, 150));
        Controls.Add(grpAxes);

        var grpCal = new GroupBox { Text = "Calibration", Location = new Point(430, midRowY), Size = new Size(450, 190), ForeColor = Color.FromArgb(255, 193, 7) };
        var lblDz = new Label { Text = "Brake Deadzone (%):", Location = new Point(12, 24), Size = new Size(150, 22), ForeColor = Color.FromArgb(200, 200, 200) };
        _numDeadzone.Location = new Point(168, 22); _numDeadzone.Size = new Size(80, 22);
        _numDeadzone.Minimum = 0; _numDeadzone.Maximum = 50; _numDeadzone.Value = 0;
        _numDeadzone.Increment = 1; _numDeadzone.DecimalPlaces = 0;

        _chkEnablePedals.Text = "Enable Physical Pedal Input";
        _chkEnablePedals.Location = new Point(12, 52); _chkEnablePedals.Size = new Size(220, 24);
        _chkEnablePedals.ForeColor = Color.FromArgb(76, 175, 80); _chkEnablePedals.BackColor = Color.Transparent;

        _chkInvert.Text = "Invert Gas";
        _chkInvert.Location = new Point(12, 78); _chkInvert.Size = new Size(140, 24);
        _chkInvert.ForeColor = Color.FromArgb(200, 200, 200); _chkInvert.BackColor = Color.Transparent;

        _btnApply.Text = "Save to pedal_config.json";
        _btnApply.Location = new Point(12, 110); _btnApply.Size = new Size(220, 26);
        _btnApply.FlatStyle = FlatStyle.Flat; _btnApply.BackColor = Color.FromArgb(50, 50, 50);
        _btnApply.ForeColor = Color.FromArgb(200, 200, 200);
        _btnApply.FlatAppearance.BorderColor = Color.FromArgb(100, 100, 100);

        var lblInfo = new Label
        {
            Text = "Changes take effect immediately in the FFB app.\nRestart not required.",
            Location = new Point(12, 142), Size = new Size(400, 40),
            ForeColor = Color.FromArgb(130, 130, 130), BackColor = Color.Transparent
        };
        grpCal.Controls.AddRange([lblDz, _numDeadzone, _chkEnablePedals, _chkInvert, _btnApply, lblInfo]);
        Controls.Add(grpCal);

        // ── Bottom: Simulated Active Pedal Haptics + gain controls ──
        int botY = midRowY + 200;
        var grpHaptics = new GroupBox { Text = "Simulated Active Pedal Haptics — drag sliders to adjust effect → pedal routing", Location = new Point(16, botY), Size = new Size(864, 210), ForeColor = Color.FromArgb(239, 83, 80) };

        // Row 1: Master gains
        var lblMaster = new Label { Text = "MASTER", Location = new Point(12, 18), Size = new Size(60, 16), ForeColor = Color.FromArgb(239, 83, 80), BackColor = Color.Transparent };
        var lblBrakeM = new Label { Text = "Brake →", Location = new Point(78, 18), Size = new Size(60, 16), ForeColor = Color.FromArgb(229, 57, 53), BackColor = Color.Transparent };
        _numBrakeMasterGain.Location = new Point(140, 16); _numBrakeMasterGain.Size = new Size(60, 20);
        _numBrakeMasterGain.Minimum = 0; _numBrakeMasterGain.Maximum = 200; _numBrakeMasterGain.Value = 100; _numBrakeMasterGain.Increment = 5;
        var lblGasM = new Label { Text = "Gas →", Location = new Point(210, 18), Size = new Size(50, 16), ForeColor = Color.FromArgb(76, 175, 80), BackColor = Color.Transparent };
        _numGasMasterGain.Location = new Point(258, 16); _numGasMasterGain.Size = new Size(60, 20);
        _numGasMasterGain.Minimum = 0; _numGasMasterGain.Maximum = 200; _numGasMasterGain.Value = 100; _numGasMasterGain.Increment = 5;
        var lblPct = new Label { Text = "(100% = 1x)", Location = new Point(324, 18), Size = new Size(100, 16), ForeColor = Color.FromArgb(150, 150, 150), BackColor = Color.Transparent };
        grpHaptics.Controls.AddRange([lblMaster, lblBrakeM, _numBrakeMasterGain, lblGasM, _numGasMasterGain, lblPct]);

        // Rows 2-3: Signal bars per-column
        AddHapticSignalColumn(grpHaptics, 12, 44, "ABS pulse",   _pbHapticAbs,   _lblHapticAbs,   _numAbsGain,   _lblAbsGainVal,   Color.FromArgb(229, 57, 53));
        AddHapticSignalColumn(grpHaptics, 156, 44, "TC slip",     _pbHapticTc,    _lblHapticTc,    _numTcGain,    _lblTcGainVal,    Color.FromArgb(255, 193, 7));
        AddHapticSignalColumn(grpHaptics, 300, 44, "Curb strike", _pbHapticCurb,  _lblHapticCurb,  _numCurbGain,  _lblCurbGainVal,  Color.FromArgb(156, 39, 176));
        AddHapticSignalColumn(grpHaptics, 444, 44, "Road texture",_pbHapticRoad,  _lblHapticRoad,  _numRoadGain,  _lblRoadGainVal,  Color.FromArgb(100, 180, 255));
        AddHapticSignalColumn(grpHaptics, 588, 44, "Tyre scrub",  _pbHapticScrub, _lblHapticScrub, _numScrubGain, _lblScrubGainVal, Color.FromArgb(76, 175, 80));
        AddHapticSignalColumn(grpHaptics, 732, 44, "Brake press", _pbHapticBrakePressure, _lblHapticBrakePressure, _numBrakePressureGain, _lblBrakePressureGainVal, Color.FromArgb(239, 83, 80));

        // All gains default to 100 (= 1x)
        foreach (var n in new[] { _numAbsGain, _numTcGain, _numCurbGain, _numRoadGain, _numScrubGain, _numBrakePressureGain })
        { n.Minimum = 0; n.Maximum = 200; n.Value = 100; n.Increment = 5; }

        Controls.Add(grpHaptics);

        int logY = botY + 128;
        var grpLog = new GroupBox { Text = "Diagnostic Log", Location = new Point(16, logY), Size = new Size(864, 160), ForeColor = Color.FromArgb(255, 193, 7) };
        var btnClearLog = new Button { Text = "Clear", Location = new Point(12, 14), Size = new Size(60, 22) };
        btnClearLog.FlatStyle = FlatStyle.Flat; btnClearLog.BackColor = Color.FromArgb(50, 50, 50);
        btnClearLog.ForeColor = Color.FromArgb(200, 200, 200);
        btnClearLog.FlatAppearance.BorderColor = Color.FromArgb(100, 100, 100);
        btnClearLog.Click += (_, _) => _lstLog.Items.Clear();
        _lstLog.Location = new Point(12, 42); _lstLog.Size = new Size(838, 110);
        _lstLog.BackColor = Color.FromArgb(20, 20, 30);
        _lstLog.ForeColor = Color.FromArgb(200, 200, 200);
        _lstLog.BorderStyle = BorderStyle.FixedSingle;
        _lstLog.Font = new Font("Consolas", 9);
        _lstLog.HorizontalScrollbar = true;
        grpLog.Controls.AddRange([btnClearLog, _lstLog]);
        Controls.Add(grpLog);

        // Autoconnect on load
        _ = FetchPedalData();
    }

    private static void AddPedalBar(Control parent, ref int y, string name, ProgressBar pb, Label raw, Label val)
    {
        var lbl = new Label { Text = $"{name}:", Location = new Point(10, y), Size = new Size(56, 20), ForeColor = Color.FromArgb(200, 200, 200), BackColor = Color.Transparent };
        pb.Location = new Point(68, y); pb.Size = new Size(200, 18);
        pb.Minimum = 0; pb.Maximum = 100; pb.Value = 0;
        pb.ForeColor = name == "Brake" ? Color.FromArgb(229, 57, 53) : Color.FromArgb(76, 175, 80);
        pb.Style = ProgressBarStyle.Continuous;
        raw.Location = new Point(276, y); raw.Size = new Size(46, 20);
        raw.ForeColor = Color.FromArgb(150, 150, 150); raw.BackColor = Color.Transparent; raw.Text = "r:0.00";
        val.Location = new Point(68, y); val.Size = new Size(200, 20);
        val.ForeColor = Color.FromArgb(180, 180, 180); val.BackColor = Color.Transparent;
        val.TextAlign = ContentAlignment.MiddleRight; val.Text = "0%";
        parent.Controls.AddRange([lbl, pb, raw, val]);
        y += 26;
    }

    private static void AddHapticSignalColumn(Control parent, int x, int y, string name, ProgressBar pb, Label valLbl, NumericUpDown gain, Label gainLbl, Color c)
    {
        // Label (signal name)
        var label = new Label { Text = name, Location = new Point(x, y), Size = new Size(130, 16), ForeColor = c, BackColor = Color.Transparent };
        // Bar (routed value = rawSignal * gain)
        pb.Location = new Point(x, y + 18); pb.Size = new Size(130, 16);
        pb.Minimum = 0; pb.Maximum = 100; pb.Value = 0;
        pb.ForeColor = c; pb.Style = ProgressBarStyle.Continuous;
        // Value label (shows routed value)
        valLbl.Location = new Point(x, y + 36); valLbl.Size = new Size(130, 16);
        valLbl.ForeColor = Color.FromArgb(220, 220, 220); valLbl.BackColor = Color.Transparent;
        valLbl.Text = "0.000"; valLbl.TextAlign = ContentAlignment.MiddleCenter;
        // Gain label ("Gain:")
        var gainText = new Label { Text = "Gain:", Location = new Point(x, y + 54), Size = new Size(36, 18), ForeColor = Color.FromArgb(150, 150, 150), BackColor = Color.Transparent };
        // Gain spinner (0-200%, 100 = 1x)
        gain.Location = new Point(x + 36, y + 52); gain.Size = new Size(58, 20);
        gain.TextAlign = HorizontalAlignment.Center;
        // Gain value label (shows "1.00x")
        gainLbl.Location = new Point(x + 98, y + 54); gainLbl.Size = new Size(32, 16);
        gainLbl.ForeColor = Color.FromArgb(180, 180, 180); gainLbl.BackColor = Color.Transparent;
        gainLbl.Text = "1.0x";

        parent.Controls.AddRange([label, pb, valLbl, gainText, gain, gainLbl]);
    }

    private static void AddAxisLabel(Control parent, ref int y, string name, Label lbl, Color c)
    {
        var label = new Label { Text = name, Location = new Point(12, y), Size = new Size(100, 18), ForeColor = c, BackColor = Color.Transparent };
        lbl.Location = new Point(118, y); lbl.Size = new Size(80, 18);
        lbl.ForeColor = Color.FromArgb(255, 255, 255); lbl.BackColor = Color.Transparent; lbl.Text = "0.000";
        parent.Controls.AddRange([label, lbl]);
        y += 18;
    }

    private static ComboBox MakeMappingCombo(int x, int y, string[] items, int defaultIdx)
    {
        var cb = new ComboBox();
        cb.Items.AddRange(items);
        cb.SelectedIndex = defaultIdx;
        cb.Location = new Point(x, y);
        cb.Size = new Size(60, 22);
        cb.DropDownStyle = ComboBoxStyle.DropDownList;
        cb.BackColor = Color.FromArgb(40, 40, 50);
        cb.ForeColor = Color.FromArgb(220, 220, 220);
        cb.FlatStyle = FlatStyle.Flat;
        return cb;
    }

    private void Log(string msg)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss");
        if (_lstLog.InvokeRequired)
            _lstLog.BeginInvoke(() => _lstLog.Items.Insert(0, $"{ts}  [{++_logSeq:D3}] {msg}"));
        else
            _lstLog.Items.Insert(0, $"{ts}  [{++_logSeq:D3}] {msg}");
        if (_lstLog.Items.Count > 500) _lstLog.Items.RemoveAt(_lstLog.Items.Count - 1);
    }

    private void WireEvents()
    {
        _btnConnect.Click += async (_, _) =>
        {
            _baseUrl = _txtUrl.Text.Trim().TrimEnd('/');
            Log($"Connecting to {_baseUrl}/api/pedal-status");
            await FetchPedalData();
        };

        void SendMapping() => _ = SendMappingAsync();
        async Task SendMappingAsync()
        {
            string gasAx = _cmbGasAxis.SelectedItem?.ToString() ?? "Y";
            string brakeAx = _cmbBrakeAxis.SelectedItem?.ToString() ?? "Z";
            string clutchAx = _cmbClutchAxis.SelectedItem?.ToString() ?? "Rx";
            bool gasInv = _chkGasInv.Checked;
            bool brakeInv = _chkBrakeInv.Checked;
            bool clutchInv = _chkClutchInv.Checked;
            try
            {
                var url = $"{_baseUrl}/api/pedal-mapping?" +
                    $"gasAxis={gasAx}&brakeAxis={brakeAx}&clutchAxis={clutchAx}" +
                    $"&gasInvert={gasInv}&brakeInvert={brakeInv}&clutchInvert={clutchInv}";
                var resp = await _http.GetAsync(url);
                resp.EnsureSuccessStatusCode();
                Log($"Mapping sent to FFB app: {gasAx}→Gas {brakeAx}→Brake {clutchAx}→Clutch inv=({gasInv},{brakeInv},{clutchInv})");
                _lblDeviceStatus.Text = $"Mapping: {gasAx}→Gas  {brakeAx}→Brake  {clutchAx}→Clutch";
            }
            catch (Exception ex) { Log($"Mapping send failed: {ex.Message}"); }
        }

        _cmbGasAxis.SelectedIndexChanged += (_, _) => SendMapping();
        _cmbBrakeAxis.SelectedIndexChanged += (_, _) => SendMapping();
        _cmbClutchAxis.SelectedIndexChanged += (_, _) => SendMapping();
        _chkGasInv.CheckedChanged += (_, _) => SendMapping();
        _chkBrakeInv.CheckedChanged += (_, _) => SendMapping();
        _chkClutchInv.CheckedChanged += (_, _) => SendMapping();

        async Task SendHapticGainsAsync()
        {
            try
            {
                float brakeGain = (float)_numBrakeMasterGain.Value / 100f;
                float gasGain = (float)_numGasMasterGain.Value / 100f;
                var url = $"{_baseUrl}/api/pedal-haptic?brakeGain={brakeGain:F2}&gasGain={gasGain:F2}";
                var resp = await _http.GetAsync(url);
                resp.EnsureSuccessStatusCode();
            }
            catch { /* gains still apply locally even if send fails */ }
        }

        _cmbDevice.SelectedIndexChanged += async (_, _) =>
        {
            _selectedDeviceIndex = _cmbDevice.SelectedIndex;
            Log($"Device selection changed to index {_selectedDeviceIndex}");
            try
            {
                var resp = await _http.GetAsync($"{_baseUrl}/api/pedal-select?index={_selectedDeviceIndex}");
                resp.EnsureSuccessStatusCode();
                Log("Device selection sent to FFB app OK");
            }
            catch (Exception ex)
            {
                Log($"Device select failed: {ex.Message}");
            }
        };

        _chkEnablePedals.CheckedChanged += (_, _) =>
        {
            var cfg = PedalConfigManager.Instance.Config;
            cfg.Enabled = _chkEnablePedals.Checked;
            PedalConfigManager.Instance.Save(cfg);
            Log($"Pedal input {(cfg.Enabled ? "enabled" : "disabled")}");
        };

        // Wire haptic gain spinners → update display + send to FFB app
        void HapticGainChanged() { _ = SendHapticGainsAsync(); }
        foreach (var n in new[] { _numAbsGain, _numTcGain, _numCurbGain, _numRoadGain, _numScrubGain, _numBrakePressureGain,
                                  _numBrakeMasterGain, _numGasMasterGain })
            n.ValueChanged += (_, _) => HapticGainChanged();

        _btnApply.Click += (_, _) =>
        {
            var cfg = PedalConfigManager.Instance.Config;
            cfg.Brake.Deadzone = (float)_numDeadzone.Value / 100f;
            cfg.Gas.Invert = _chkInvert.Checked;
            cfg.Enabled = _chkEnablePedals.Checked;
            PedalConfigManager.Instance.Save(cfg);
            var msg = $"Calibration saved: deadzone={(float)_numDeadzone.Value / 100f:F2} invert={_chkInvert.Checked}";
            _lblConnStatus.Text = msg;
            _lblConnStatus.ForeColor = Color.FromArgb(76, 175, 80);
            Log(msg);
        };
    }

    private async Task FetchPedalData()
    {
        try
        {
            var resp = await _http.GetAsync($"{_baseUrl}/api/pedal-status", HttpCompletionOption.ResponseContentRead);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();
            _lastData?.Dispose();
            _lastData = JsonDocument.Parse(json);
            _connected = true;
            _lblConnStatus.Text = "Connected ✓ — updating at 10 Hz";
            _lblConnStatus.ForeColor = Color.FromArgb(76, 175, 80);
            Log("Connected to FFB app API");
        }
        catch (HttpRequestException ex)
        {
            _connected = false;
            _lblConnStatus.Text = $"Disconnected — {ex.Message}";
            _lblConnStatus.ForeColor = Color.FromArgb(229, 57, 53);
            Log($"Connection failed: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            _connected = false;
            _lblConnStatus.Text = "Connection timed out — FFB app not running?";
            _lblConnStatus.ForeColor = Color.FromArgb(229, 57, 53);
            Log("Connection timed out (FFB app not running)");
        }
        catch (JsonException ex)
        {
            _connected = false;
            _lblConnStatus.Text = $"Bad JSON: {ex.Message}";
            _lblConnStatus.ForeColor = Color.FromArgb(229, 57, 53);
            Log($"JSON parse error: {ex.Message}");
        }
    }

    private async void PollTick(object? sender, EventArgs e)
    {
        if (_polling) return; // skip if previous poll still in-flight
        _polling = true;
        try
        {
            if (!_connected)
            {
                await FetchPedalData();
                return;
            }

            var resp = await _http.GetAsync($"{_baseUrl}/api/pedal-status", HttpCompletionOption.ResponseContentRead);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();
            _lastData?.Dispose();
            _lastData = JsonDocument.Parse(json);
            UpdateFromJson(_lastData.RootElement);
            _consecutiveFailures = 0;
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            _connected = false;
            _lblConnStatus.Text = $"Connection lost ({_consecutiveFailures}) — retrying...";
            _lblConnStatus.ForeColor = Color.FromArgb(229, 57, 53);
            Log($"Poll error: {ex.Message}");
        }
        finally
        {
            _polling = false;
        }
    }

    private int _jsonUpdateCount;

    private void UpdateFromJson(JsonElement root)
    {
        _jsonUpdateCount++;

        // ── Pedal input state (calibrated values from PedalInputManager) ──
        bool hasPedal = root.TryGetProperty("hasPedalInput", out var hp) && hp.GetBoolean();
        _lblSource.Text = hasPedal
            ? $"Source: {root.GetProperty("source").GetString()}"
            : "Source: — (no input)";
        _lblSource.ForeColor = hasPedal ? Color.FromArgb(79, 195, 247) : Color.FromArgb(150, 150, 150);

        // Read mapping — prefer wheelbaseMapping (FfbDevicePedalSource, always works)
        // over mapping (DirectInputPedalSource, may be empty for wheelbase).
        string mapGas = "Y", mapBrake = "Z", mapClutch = "Rx";
        bool mapGasInv = false, mapBrakeInv = false, mapClutchInv = false;
        if (root.TryGetProperty("wheelbaseMapping", out var wbMap))
        {
            mapGas = wbMap.GetProperty("gasAxis").GetString() ?? "Y";
            mapBrake = wbMap.GetProperty("brakeAxis").GetString() ?? "Z";
            mapClutch = wbMap.GetProperty("clutchAxis").GetString() ?? "Rx";
            if (wbMap.TryGetProperty("gasInvert", out var gi)) mapGasInv = gi.GetBoolean();
            if (wbMap.TryGetProperty("brakeInvert", out var bi)) mapBrakeInv = bi.GetBoolean();
            if (wbMap.TryGetProperty("clutchInvert", out var ci)) mapClutchInv = ci.GetBoolean();
        }
        else if (root.TryGetProperty("mapping", out var mapRoot))
        {
            mapGas = mapRoot.GetProperty("gasAxis").GetString() ?? "Y";
            mapBrake = mapRoot.GetProperty("brakeAxis").GetString() ?? "Z";
            mapClutch = mapRoot.GetProperty("clutchAxis").GetString() ?? "Rx";
            if (mapRoot.TryGetProperty("gasInvert", out var gi)) mapGasInv = gi.GetBoolean();
            if (mapRoot.TryGetProperty("brakeInvert", out var bi)) mapBrakeInv = bi.GetBoolean();
            if (mapRoot.TryGetProperty("clutchInvert", out var ci)) mapClutchInv = ci.GetBoolean();
        }

        bool firstAxisData = _jsonUpdateCount < 5 && _selectedDeviceIndex == 0 && root.TryGetProperty("wheelbaseAxes", out _);

        // Periodic log (every ~30 updates = ~3 seconds)
        if (_jsonUpdateCount % 30 == 1)
        {
            var hasPedalStr = hasPedal ? "yes" : "no";
            var srcStr = hasPedal ? root.GetProperty("source").GetString() : "—";
            var devCount = root.TryGetProperty("devices", out var dList) ? dList.GetArrayLength() : 0;
            var hasWb = root.TryGetProperty("wheelbaseAxes", out _) ? "yes" : "no";
            Log($"Poll #{_jsonUpdateCount}: hasPedal={hasPedalStr} src={srcStr} devices={devCount} wheelbase={hasWb}");
        }

        // Helper: read a raw axis value by name from a JSON element with x/y/z/rx/ry/rz/sl0/sl1
        static float ReadRawAxis(JsonElement e, string name)
        {
            return name switch
            {
                "X" => e.GetProperty("x").GetSingle(),
                "Y" => e.GetProperty("y").GetSingle(),
                "Z" => e.GetProperty("z").GetSingle(),
                "Rx" => e.GetProperty("rx").GetSingle(),
                "Ry" => e.TryGetProperty("ry", out var ry) ? ry.GetSingle() : 0,
                "Rz" => e.TryGetProperty("rz", out var rz) ? rz.GetSingle() : 0,
                "Slider0" => e.TryGetProperty("sl0", out var s0) && s0.ValueKind == JsonValueKind.Number ? s0.GetSingle() : 0,
                "Slider1" => e.TryGetProperty("sl1", out var s1) && s1.ValueKind == JsonValueKind.Number ? s1.GetSingle() : 0,
                _ => 0f
            };
        }

        float gas = 0, brake = 0, clutch = 0;
        if (hasPedal)
        {
            // Use calibrated values from FFB app (respects its axis mapping)
            gas = (float)root.GetProperty("gasInput").GetDouble();
            brake = (float)root.GetProperty("brakeInput").GetDouble();
            clutch = (float)root.GetProperty("clutchInput").GetDouble();
        }
        else if (root.TryGetProperty("wheelbaseAxes", out var wbAxes))
        {
            // No calibrated input yet — apply mapping locally so bars still move
            gas = ReadRawAxis(wbAxes, mapGas);
            brake = ReadRawAxis(wbAxes, mapBrake);
            clutch = ReadRawAxis(wbAxes, mapClutch);
            if (mapGasInv) gas = 1f - gas;
            if (mapBrakeInv) brake = 1f - brake;
            if (mapClutchInv) clutch = 1f - clutch;
        }
        _pbGas.Value = (int)(gas * 100); _lblGasVal.Text = $"{(gas * 100):F0}%"; _lblGasRaw.Text = $"r:{gas:F2}";
        _pbBrake.Value = (int)(brake * 100); _lblBrakeVal.Text = $"{(brake * 100):F0}%"; _lblBrakeRaw.Text = $"r:{brake:F2}";
        _pbClutch.Value = (int)(clutch * 100); _lblClutchVal.Text = $"{(clutch * 100):F0}%"; _lblClutchRaw.Text = $"r:{clutch:F2}";

        // ── Device list (wheelbase + DI devices) ──
        _cmbDevice.Items.Clear();
        _cmbDevice.Items.Add("Wheelbase (via FfbDeviceManager)");

        bool diAvailable = root.TryGetProperty("diAvailable", out var da) && da.GetBoolean();
        if (root.TryGetProperty("devices", out var devices))
        {
            foreach (var d in devices.EnumerateArray())
            {
                string name = d.GetProperty("name").GetString() ?? "?";
                int axes = d.GetProperty("axisCount").GetInt32();
                bool ffb = d.GetProperty("isFfbCapable").GetBoolean();
                _cmbDevice.Items.Add($"{name} ({axes} axes{(ffb ? ", FFB" : "")})");
                if (firstAxisData)
                    Log($"DI device: {name} ({axes} axes, FFB={ffb})");
            }
        }
        if (_cmbDevice.SelectedIndex < 0) _cmbDevice.SelectedIndex = 0;

        if (firstAxisData)
            Log($"Mapping: {mapGas}→Gas {mapBrake}→Brake {mapClutch}→Clutch | inverts: gas={mapGasInv} brake={mapBrakeInv} clutch={mapClutchInv}");
        _lblDeviceStatus.Text = "Wheelbase pedals active via FfbDeviceManager";

        // ── Axis mapping (combo boxes above are separately updated) ──

        // ── Raw axis values ──
        float rawX = 0, rawY = 0, rawZ = 0, rawRx = 0, rawRy = 0, rawRz = 0;
        string dispSl0 = "—", dispSl1 = "—";

        // _selectedDeviceIndex 0 = wheelbase (FfbDevicePedalSource, always works)
        // _selectedDeviceIndex 1+ = DI device (DirectInputPedalSource, USB pedals only)
        if (_selectedDeviceIndex == 0 && root.TryGetProperty("wheelbaseAxes", out var wb))
        {
            rawX = wb.GetProperty("x").GetSingle();
            rawY = wb.GetProperty("y").GetSingle();
            rawZ = wb.GetProperty("z").GetSingle();
            rawRx = wb.GetProperty("rx").GetSingle();
            rawRy = wb.TryGetProperty("ry", out var ry) ? ry.GetSingle() : 0;
            rawRz = wb.TryGetProperty("rz", out var rz) ? rz.GetSingle() : 0;
            dispSl0 = wb.TryGetProperty("sl0", out var s0) && s0.ValueKind == JsonValueKind.Number ? s0.GetSingle().ToString("F3") : "—";
            dispSl1 = wb.TryGetProperty("sl1", out var s1) && s1.ValueKind == JsonValueKind.Number ? s1.GetSingle().ToString("F3") : "—";

            _lblAxX.Text = rawX.ToString("F3");
            _lblAxY.Text = rawY.ToString("F3");
            _lblAxZ.Text = rawZ.ToString("F3");
            _lblAxRx.Text = rawRx.ToString("F3");
            _lblAxRy.Text = rawRy.ToString("F3");
            _lblAxRz.Text = rawRz.ToString("F3");
            _lblAxSl0.Text = dispSl0;
            _lblAxSl1.Text = dispSl1;

            if (firstAxisData)
                Log($"Wheelbase axes: X={rawX:F3} Y={rawY:F3} Z={rawZ:F3} Rx={rawRx:F3} Ry={rawRy:F3} Rz={rawRz:F3} sl0={dispSl0} sl1={dispSl1} | map: {mapGas}→Gas {mapBrake}→Brake {mapClutch}→Clutch");
        }
        else if (root.TryGetProperty("axisSnapshots", out var snapshots))
        {
            foreach (var snap in snapshots.EnumerateObject())
            {
                var v = snap.Value;
                _lblAxX.Text = v.GetProperty("x").GetSingle().ToString("F3");
                _lblAxY.Text = v.GetProperty("y").GetSingle().ToString("F3");
                _lblAxZ.Text = v.GetProperty("z").GetSingle().ToString("F3");
                _lblAxRx.Text = v.GetProperty("rx").GetSingle().ToString("F3");
                _lblAxRy.Text = v.GetProperty("ry").GetSingle().ToString("F3");
                _lblAxRz.Text = v.GetProperty("rz").GetSingle().ToString("F3");
                _lblAxSl0.Text = v.TryGetProperty("sl0", out var vsl0) && vsl0.ValueKind == JsonValueKind.Number ? vsl0.GetSingle().ToString("F3") : "—";
                _lblAxSl1.Text = v.TryGetProperty("sl1", out var vsl1) && vsl1.ValueKind == JsonValueKind.Number ? vsl1.GetSingle().ToString("F3") : "—";
                break;
            }
        }

        // ── Calibration ──
        if (root.TryGetProperty("calibration", out var cal))
        {
            _numDeadzone.Value = (int)(cal.GetProperty("brakeDeadzone").GetSingle() * 100);
            _chkInvert.Checked = cal.GetProperty("gasInvert").GetBoolean();
        }
        _chkEnablePedals.Checked = root.TryGetProperty("pedalInputEnabled", out var pe) && pe.GetBoolean();

        // ── Simulated pedal haptics (pre-routed by FFB app) ──
        if (root.TryGetProperty("pedalHaptics", out var haptics))
        {
            // Read routed values (raw × per-signal gain × master gain, computed server-side)
            float routedAbs = ReadRouted(haptics, "routedAbs", "absModulation");
            float routedTc = ReadRouted(haptics, "routedTc", "tcRumble");
            float routedCurb = ReadRouted(haptics, "routedCurb", "curbModulation");
            float routedRoad = ReadRouted(haptics, "routedRoad", "roadForceModulation");
            float routedScrub = ReadRouted(haptics, "routedScrub", "scrubModulation");
            float routedBrakePress = ReadRouted(haptics, "routedBrakePressure", "brakePressure");

            // Store raw values for local gain spinners
            _rawAbs = haptics.GetProperty("absModulation").GetSingle();
            _rawTc = haptics.GetProperty("tcRumble").GetSingle();
            _rawCurb = haptics.GetProperty("curbModulation").GetSingle();
            _rawRoad = haptics.GetProperty("roadForceModulation").GetSingle();
            _rawScrub = haptics.GetProperty("scrubModulation").GetSingle();
            _rawBrakePress = haptics.GetProperty("brakePressure").GetSingle();

            // Read current routing gains from API
            if (root.TryGetProperty("routing", out var routing))
            {
                _numBrakeMasterGain.Value = (int)(routing.GetProperty("brakeHapticGain").GetSingle() * 100f);
                _numGasMasterGain.Value = (int)(routing.GetProperty("gasHapticGain").GetSingle() * 100f);
            }

            // Display routed values on bars
            DisplayRoutedBar(_pbHapticAbs, _lblHapticAbs, routedAbs);
            DisplayRoutedBar(_pbHapticTc, _lblHapticTc, routedTc);
            DisplayRoutedBar(_pbHapticCurb, _lblHapticCurb, routedCurb);
            DisplayRoutedBar(_pbHapticRoad, _lblHapticRoad, routedRoad);
            DisplayRoutedBar(_pbHapticScrub, _lblHapticScrub, routedScrub);
            DisplayRoutedBar(_pbHapticBrakePressure, _lblHapticBrakePressure, routedBrakePress);

            // Update gain labels to match what FFB app is using
            UpdateGainLabel(_numAbsGain, _lblAbsGainVal, ReadGainFromApi(routing, "abs"));
            UpdateGainLabel(_numTcGain, _lblTcGainVal, ReadGainFromApi(routing, "tc"));
            UpdateGainLabel(_numCurbGain, _lblCurbGainVal, ReadGainFromApi(routing, "curb"));
            UpdateGainLabel(_numRoadGain, _lblRoadGainVal, ReadGainFromApi(routing, "road"));
            UpdateGainLabel(_numScrubGain, _lblScrubGainVal, ReadGainFromApi(routing, "scrub"));
        }
    }

    private static float ReadRouted(JsonElement haptics, string routedKey, string fallbackKey)
    {
        if (haptics.TryGetProperty(routedKey, out var r)) return r.GetSingle();
        return haptics.TryGetProperty(fallbackKey, out var f) ? f.GetSingle() : 0f;
    }

    private static float ReadGainFromApi(JsonElement? routing, string signal)
    {
        if (routing == null || !routing.HasValue) return 1f;
        if (!routing.Value.TryGetProperty("routes", out var routes)) return 1f;
        foreach (var r in routes.EnumerateArray())
        {
            if (r.GetProperty("signal").GetString() == signal)
                return r.GetProperty("gain").GetSingle();
        }
        return 1f;
    }

    private static void DisplayRoutedBar(ProgressBar pb, Label lbl, float value)
    {
        pb.Value = (int)Math.Clamp(value * 100f, 0f, 100f);
        lbl.Text = value.ToString("F4");
    }

    private static void UpdateGainLabel(NumericUpDown ctl, Label lbl, float apiGain)
    {
        if (ctl.InvokeRequired)
        { ctl.BeginInvoke(() => { ctl.Value = (int)(apiGain * 100f); lbl.Text = $"{apiGain:F1}x"; }); }
        else
        { ctl.Value = (int)(apiGain * 100f); lbl.Text = $"{apiGain:F1}x"; }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _pollTimer.Stop();
        _lastData?.Dispose();
        _http.Dispose();
        base.OnFormClosed(e);
    }
}
