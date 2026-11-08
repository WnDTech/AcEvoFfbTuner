using AcEvoFfbTuner.Core.FfbProviders;

namespace AcEvoFfbTuner.Core.DeviceDetection;

/// <summary>One physical sim-racing device found by <see cref="RaceHardwareScanner"/>.</summary>
public sealed record RaceHardwareItem
{
    public DeviceCategory Category { get; init; }
    public string VendorName { get; init; } = "";
    public WheelbaseVendor WheelVendor { get; init; } = WheelbaseVendor.Unknown;
    public string ProductName { get; init; } = "";
    public ushort VendorId { get; init; }
    public ushort ProductId { get; init; }
    public bool IsFfbCapable { get; init; }
    /// <summary>Why the category was chosen — shown in tooltips / diagnostics.</summary>
    public string Reason { get; init; } = "";

    public string Key => $"{VendorId:X4}:{ProductId:X4}";
    public string VidPid => $"VID {VendorId:X4} / PID {ProductId:X4}";

    public string CategoryLabel => Category switch
    {
        DeviceCategory.WheelBase => "Wheelbase",
        DeviceCategory.WheelRim => "Wheel rim",
        DeviceCategory.Pedals => "Pedals",
        DeviceCategory.HapticPad => "Haptics",
        DeviceCategory.ButtonBox => "Button box",
        DeviceCategory.Shifter => "Shifter",
        DeviceCategory.Handbrake => "Handbrake",
        _ => "Controller",
    };
}

/// <summary>
/// System-wide sim-hardware detection. One HID pass, grouped per physical
/// device (VID/PID), classified by: DirectInput FFB capability → product-name
/// keywords → vendor ID. Only VIDs already used elsewhere in this app are
/// trusted; PIDs are NOT guessed (except Moza PHF 0x002F, verified in
/// MozaPhfProvider). Unknown devices fall back to name keywords so new
/// hardware still lands in the right category.
/// </summary>
public static class RaceHardwareScanner
{
    // HID Generic Desktop page + game-controller usages.
    private const ushort UsagePageGenericDesktop = 0x01;
    private const ushort UsageJoystick = 0x04;
    private const ushort UsageGamepad = 0x05;
    private const ushort UsageMultiAxis = 0x08;

    private const ushort MozaVid = 0x346E;
    private const ushort MozaPhfPid = 0x002F;

    /// <summary>Sim-racing vendor IDs (same table as WheelbaseFactory).
    /// 0x0483 (STMicro) and 0x16D0 (MCS) are SHARED hobbyist VIDs — they only
    /// count when the product/manufacturer name confirms a sim brand.</summary>
    private static readonly Dictionary<ushort, (string name, WheelbaseVendor vendor, bool shared)> Vendors = new()
    {
        [0x346E] = ("Moza", WheelbaseVendor.Moza, false),
        [0x0EB7] = ("Fanatec", WheelbaseVendor.Fanatec, false),
        [0x3235] = ("Simagic", WheelbaseVendor.Simagic, false),
        [0x044F] = ("Thrustmaster", WheelbaseVendor.Thrustmaster, false),
        [0x046D] = ("Logitech", WheelbaseVendor.Logitech, false),
        [0x2433] = ("Asetek", WheelbaseVendor.Asetek, false),
        [0x3480] = ("Cammus", WheelbaseVendor.Cammus, false),
        [0x16D0] = ("Simucube", WheelbaseVendor.Simucube, true),
        [0x0483] = ("STM-based", WheelbaseVendor.Unknown, true),
    };

    /// <summary>Brand names recognised in product/manufacturer strings.</summary>
    private static readonly (string token, string name, WheelbaseVendor vendor)[] NameBrands =
    [
        ("SIMUCUBE", "Simucube", WheelbaseVendor.Simucube),
        ("MOZA", "Moza", WheelbaseVendor.Moza),
        ("GUDSEN", "Moza", WheelbaseVendor.Moza),
        ("FANATEC", "Fanatec", WheelbaseVendor.Fanatec),
        ("SIMAGIC", "Simagic", WheelbaseVendor.Simagic),
        ("THRUSTMASTER", "Thrustmaster", WheelbaseVendor.Thrustmaster),
        ("LOGITECH", "Logitech", WheelbaseVendor.Logitech),
        ("ASETEK", "Asetek", WheelbaseVendor.Asetek),
        ("VNM", "VNM", WheelbaseVendor.VNM),
        ("CAMMUS", "Cammus", WheelbaseVendor.Cammus),
        ("HEUSINKVELD", "Heusinkveld", WheelbaseVendor.Unknown),
        ("SIMTREC", "Simtrecs", WheelbaseVendor.Unknown),
        ("SIM-LAB", "Sim-Lab", WheelbaseVendor.Unknown),
        ("SIMLAB", "Sim-Lab", WheelbaseVendor.Unknown),
        ("CUBE CONTROLS", "Cube Controls", WheelbaseVendor.Unknown),
        ("ASCHER", "Ascher Racing", WheelbaseVendor.Unknown),
        ("CONSPIT", "Conspit", WheelbaseVendor.Unknown),
        ("SIMSONN", "Simsonn", WheelbaseVendor.Unknown),
        ("PXN", "PXN", WheelbaseVendor.Unknown),
    ];

    /// <summary>Brands that also sell non-racing controllers (flight sim, gamepads).</summary>
    private static readonly HashSet<string> MixedUseVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        "Thrustmaster", "Logitech", "PXN",
    };

    private static readonly (DeviceCategory cat, string[] tokens)[] CategoryKeywords =
    [
        // Order matters: most specific first ("handbrake" contains "brake",
        // "pedal haptic" must be haptics not pedals).
        (DeviceCategory.Handbrake, ["HANDBRAKE", "HAND BRAKE", "E-BRAKE", "EBRAKE", "HBP"]),
        (DeviceCategory.HapticPad, ["HAPTIC", "HF8", "PHF", "SHAKER", "TRANSDUCER", "VIBRATION"]),
        (DeviceCategory.Shifter, ["SHIFTER", "SEQUENTIAL", "H-PATTERN", "HPATTERN", "GEARBOX", "TH8"]),
        (DeviceCategory.Pedals, ["PEDAL", "LOAD CELL", "LOADCELL", "CRP", "T-LCM", "T3PA"]),
        (DeviceCategory.ButtonBox, ["BUTTON BOX", "BUTTONBOX", "BUTTON PANEL", "BBOX", "MULTI-FUNCTION", "MFSW"]),
        (DeviceCategory.WheelRim, ["STEERING WHEEL", "WHEEL RIM", "FORMULA WHEEL"]),
        (DeviceCategory.WheelBase, ["BASE", "DIRECT DRIVE", "WHEELBASE", "RACING WHEEL", "DRIVING FORCE",
                                     " DD", "CSL", "PODIUM", "T300", "T248", "T-GT", "TS-PC", "TS-XW", "T818",
                                     "G29", "G920", "G923", "G27", "G25", "RS50", "SIMUCUBE", "FORTE", "INVICTA", "LA PRIMA"]),
    ];

    /// <summary>
    /// Scans all HID devices. <paramref name="ffbVidPids"/> are VID/PID pairs
    /// DirectInput reports as force-feedback capable (from
    /// <see cref="VidPidFromProductGuid"/>) — those are wheelbases.
    /// </summary>
    public static List<RaceHardwareItem> Scan(IReadOnlySet<uint>? ffbVidPids = null)
    {
        List<HidDeviceInfo> all;
        try { all = HidDeviceEnumerator.EnumerateAll(); }
        catch { return []; }
        return Classify(all, ffbVidPids);
    }

    /// <summary>Hash of present HID interfaces (no device I/O) — changes when
    /// anything is plugged in, unplugged or powered on/off.</summary>
    public static int PresentDeviceFingerprint()
    {
        try { return HidDeviceEnumerator.GetPresentFingerprint(); }
        catch { return 0; }
    }

    /// <summary>Pure classification (unit-testable, no hardware access).</summary>
    public static List<RaceHardwareItem> Classify(IEnumerable<HidDeviceInfo> collections, IReadOnlySet<uint>? ffbVidPids = null)
    {
        var result = new List<RaceHardwareItem>();

        foreach (var group in collections.GroupBy(c => ((uint)c.ProductId << 16) | c.VendorId))
        {
            var cols = group.ToList();
            ushort vid = cols[0].VendorId, pid = cols[0].ProductId;
            bool ffb = ffbVidPids?.Contains(group.Key) == true;

            string product = cols.Select(c => c.ProductString).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "";
            string manufacturer = cols.Select(c => c.ManufacturerString).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "";
            string nameUpper = (manufacturer + " " + product).ToUpperInvariant();

            bool isGameController = cols.Any(c => c.UsagePage == UsagePageGenericDesktop
                && (c.Usage is UsageJoystick or UsageMultiAxis or UsageGamepad));
            bool isJoystickClass = cols.Any(c => c.UsagePage == UsagePageGenericDesktop
                && (c.Usage is UsageJoystick or UsageMultiAxis));

            // Vendor identity: brand in the name wins, then a non-shared VID.
            var brand = NameBrands.FirstOrDefault(b => nameUpper.Contains(b.token));
            string vendorName = brand.name ?? "";
            var wheelVendor = brand.vendor;
            bool knownSimVendor = brand.name != null;
            if (Vendors.TryGetValue(vid, out var v) && !v.shared)
            {
                knownSimVendor = true;
                if (vendorName.Length == 0) { vendorName = v.name; wheelVendor = v.vendor; }
            }

            // Moza PHF control box (verified PID in MozaPhfProvider).
            if (vid == MozaVid && pid == MozaPhfPid)
            {
                result.Add(Make(DeviceCategory.HapticPad, "Moza", WheelbaseVendor.Moza,
                    product.Length > 0 ? product : "Moza Pedal Haptic Feedback", vid, pid, ffb, "Moza PHF PID"));
                continue;
            }

            // Not a game controller and not a sim brand → keyboards, mice, headsets, etc.
            if (!ffb && !isGameController) continue;
            // Gamepads (Xbox/PS pads) from non-sim vendors are not race hardware.
            if (!ffb && !knownSimVendor && !isJoystickClass) continue;
            // Logitech makes plenty of non-racing joysticks/pads — require a racing name or FFB.
            // (Handled naturally below: unmatched names become OtherController.)

            DeviceCategory cat;
            string reason;
            if (ffb)
            {
                // A force-feedback device is a wheelbase unless its name clearly says
                // otherwise (FFB pedals / active pedals are rare but exist).
                var kw = MatchKeyword(nameUpper);
                cat = kw is DeviceCategory.Pedals or DeviceCategory.HapticPad ? kw.Value : DeviceCategory.WheelBase;
                reason = "DirectInput force feedback";
            }
            else if (MatchKeyword(nameUpper) is { } kwCat)
            {
                cat = kwCat;
                reason = "product name";
            }
            else
            {
                // Unrecognised product. Keep it only for racing-only brands —
                // Thrustmaster/Logitech also make flight gear (rudders,
                // throttles), and generic joysticks / vJoy are not race hardware.
                bool racingOnlyBrand = knownSimVendor
                    && vendorName.Length > 0
                    && !MixedUseVendors.Contains(vendorName);
                if (!racingOnlyBrand) continue;
                cat = DeviceCategory.OtherController;
                reason = "sim vendor, unrecognised product";
            }

            string display = product.Length > 0 ? product
                : vendorName.Length > 0 ? $"{vendorName} device" : $"Controller {vid:X4}:{pid:X4}";
            if (vendorName.Length == 0 && manufacturer.Length > 0) vendorName = manufacturer;

            result.Add(Make(cat, vendorName, wheelVendor, display, vid, pid, ffb, reason));
        }

        return result
            .OrderBy(r => r.Category)
            .ThenBy(r => r.ProductName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static DeviceCategory? MatchKeyword(string nameUpper)
    {
        foreach (var (cat, tokens) in CategoryKeywords)
            if (tokens.Any(nameUpper.Contains))
                return cat;
        return null;
    }

    private static RaceHardwareItem Make(DeviceCategory cat, string vendor, WheelbaseVendor wv, string product,
        ushort vid, ushort pid, bool ffb, string reason) => new()
    {
        Category = cat,
        VendorName = vendor,
        WheelVendor = wv,
        ProductName = product,
        VendorId = vid,
        ProductId = pid,
        IsFfbCapable = ffb,
        Reason = reason,
    };

    /// <summary>
    /// DirectInput HID product GUIDs encode the USB ids in Data1 as
    /// (PID &lt;&lt; 16) | VID ("PIDVID" GUID, e.g. {0004346E-0000-0000-0000-504944564944}).
    /// Returns the same packed key <see cref="Classify"/> groups by, or null
    /// when the GUID is not a PIDVID GUID.
    /// </summary>
    public static uint? VidPidFromProductGuid(Guid productGuid)
    {
        var b = productGuid.ToByteArray();
        // Bytes 10..15 spell "PIDVID" for HID-backed DirectInput devices.
        if (b[10] != (byte)'P' || b[11] != (byte)'I' || b[12] != (byte)'D'
            || b[13] != (byte)'V' || b[14] != (byte)'I' || b[15] != (byte)'D')
            return null;
        return BitConverter.ToUInt32(b, 0);
    }
}
