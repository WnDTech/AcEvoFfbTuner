//
// fake_rs50kmdf.c — virtual RS50 as a KMDF VHF client. Three VHF devices
// (one per HID++ collection), created with the DEVICE OBJECT (the UMDF
// file-handle channel is broken on the host OS). The HID++ responder and
// capture logging are identical to the UMDF version.
//

#include "fake_rs50kmdf.h"

// ---------------------------------------------------------------------------
// Capture logging — every exchanged byte, appended to C:\Windows\Temp\FakeRs50.log
// (kernel-mode ZwCreateFile/ZwWriteFile; callbacks run at PASSIVE_LEVEL)
// ---------------------------------------------------------------------------

static ULONGLONG
TickMs(
    VOID
    )
{
    // 100ns units since boot -> ms
    return KeQueryInterruptTime() / 10000;
}

static VOID
LogOpen(
    _Inout_ PHANDLE Handle
    )
{
    NTSTATUS status;
    OBJECT_ATTRIBUTES objAttr;
    UNICODE_STRING fileName;
    IO_STATUS_BLOCK ioStatus;

    if (*Handle != NULL)
    {
        return;
    }

    RtlInitUnicodeString(&fileName, L"\\??\\C:\\Windows\\Temp\\FakeRs50.log");
    InitializeObjectAttributes(&objAttr, &fileName, OBJ_CASE_INSENSITIVE | OBJ_KERNEL_HANDLE, NULL, NULL);

    status = ZwCreateFile(Handle,
                          FILE_APPEND_DATA | SYNCHRONIZE,
                          &objAttr,
                          &ioStatus,
                          NULL,
                          FILE_ATTRIBUTE_NORMAL,
                          FILE_SHARE_READ | FILE_SHARE_WRITE,
                          FILE_OPEN_IF,
                          FILE_SYNCHRONOUS_IO_NONALERT,
                          NULL,
                          0);
    if (!NT_SUCCESS(status))
    {
        *Handle = NULL;
    }
}

static VOID
LogWrite(
    _In_ PFAKEWHEEL_CONTEXT Ctx,
    _In_ const WCHAR* Line,
    _In_ ULONG LengthChars
    )
{
    IO_STATUS_BLOCK ioStatus;

    if (Ctx->LogHandle == NULL)
    {
        LogOpen(&Ctx->LogHandle);
        if (Ctx->LogHandle == NULL)
        {
            return;
        }
    }

    ZwWriteFile(Ctx->LogHandle,
                NULL, NULL, NULL,
                &ioStatus,
                (PVOID)Line,
                LengthChars * sizeof(WCHAR),
                NULL,
                NULL);
}

static VOID
LogRaw(
    _In_ PFAKEWHEEL_CONTEXT Ctx,
    _In_ CHAR Dir,
    _In_ const UCHAR* Buf,
    _In_ USHORT Len
    )
{
    WCHAR line[640];
    ULONG n = 0;
    ULONGLONG ms = TickMs();

    {
        WCHAR digits[24];
        ULONG d = 0;
        do { digits[d++] = L"0123456789"[ms % 10]; ms /= 10; } while (ms > 0 && d < 24);
        line[n++] = L'[';
        while (d > 0) { line[n++] = digits[--d]; }
        line[n++] = L']';
        line[n++] = L' ';
        line[n++] = (WCHAR)Dir;
        line[n++] = L' ';
    }

    for (USHORT i = 0; i < Len && n < 620; i++)
    {
        UCHAR b = Buf[i];
        line[n++] = L"0123456789ABCDEF"[b >> 4];
        line[n++] = L"0123456789ABCDEF"[b & 0x0F];
        line[n++] = L' ';
    }
    while (n < 620) { line[n++] = L' '; }
    line[n++] = L'\r';
    line[n++] = L'\n';
    line[n] = L'\0';

    LogWrite(Ctx, line, n);
}

// ---------------------------------------------------------------------------
// HID++ responder helpers
// ---------------------------------------------------------------------------

static UCHAR
FeatureIndexForId(
    _In_ USHORT FeatureId
    )
{
    switch (FeatureId)
    {
    case 0x0000: return 0x00;
    case 0x0001: return 0x01;
    case 0x0002: return 0x02;
    case 0x0003: return 0x03;
    case 0x0005: return 0x05;
    case 0x8110: return FEAT_FORCEFF;
    case 0x8130: return FEAT_OLED;
    case 0x8133: return FEAT_DAMPING;
    case 0x8136: return FEAT_STRENGTH;
    case 0x8137: return FEAT_PROFILE;
    case 0x8138: return FEAT_ROTATION;
    case 0x8139: return FEAT_TRUEFORCE;
    default:     return 0x00;
    }
}

static USHORT
FeatureIdForIndex(
    _In_ UCHAR Index
    )
{
    switch (Index)
    {
    case 0x00: return 0x0000;
    case 0x01: return 0x0001;
    case 0x02: return 0x0002;
    case 0x03: return 0x0003;
    case 0x05: return 0x0005;
    case FEAT_FORCEFF:   return 0x8110;
    case FEAT_OLED:      return 0x8130;
    case FEAT_DAMPING:   return 0x8133;
    case FEAT_STRENGTH:  return 0x8136;
    case FEAT_PROFILE:   return 0x8137;
    case FEAT_ROTATION:  return 0x8138;
    case FEAT_TRUEFORCE: return 0x8139;
    default:             return 0x0000;
    }
}

static VOID
BuildResponseFrame(
    _In_ UCHAR ReportId,
    _In_ UCHAR FeatureIndex,
    _In_ UCHAR Fn,
    _In_ const UCHAR Payload[6],
    _Inout_ UCHAR* Out
    )
{
    ULONG total = (ReportId == RID_SHORT) ? 7 : (ReportId == RID_LONG) ? 20 : 64;

    RtlZeroMemory(Out, total);
    Out[0] = ReportId;
    Out[1] = HIDPP_DEV_INDEX;
    Out[2] = FeatureIndex;
    Out[3] = (UCHAR)((Fn << 4) | HIDPP_SWID);
    RtlCopyMemory(Out + 4, Payload, 6);
}

// ---------------------------------------------------------------------------
// Input report submission queue (VHF: one report in flight per device)
// ---------------------------------------------------------------------------

static VOID
SubmitNext(
    _In_ PFAKEWHEEL_CONTEXT Ctx
    )
{
    NTSTATUS status;
    HID_XFER_PACKET packet;

    if (Ctx->PendCount == 0 || Ctx->VhfHandle[0] == NULL)
    {
        return;
    }

    UCHAR device = Ctx->Pend[0].Device;
    if (Ctx->SubmitInFlight[device])
    {
        return;
    }

    RtlZeroMemory(&packet, sizeof(packet));
    packet.reportBuffer = Ctx->Pend[0].Buf;
    packet.reportBufferLen = Ctx->Pend[0].Len;
    packet.reportId = Ctx->Pend[0].ReportId;

    for (UCHAR i = 1; i < Ctx->PendCount; i++)
    {
        Ctx->Pend[i - 1] = Ctx->Pend[i];
    }
    Ctx->PendCount--;

    status = VhfReadReportSubmit(Ctx->VhfHandle[device], &packet);
    if (!NT_SUCCESS(status))
    {
        return;
    }

    Ctx->SubmitInFlight[device] = TRUE;
}

static VOID
QueueSubmit(
    _In_ PFAKEWHEEL_CONTEXT Ctx,
    _In_ UCHAR Device,
    _In_ UCHAR* Buffer,
    _In_ USHORT Length,
    _In_ UCHAR ReportId
    )
{
    if (Ctx->PendCount >= 16)
    {
        Ctx->PendCount = 0;
    }
    Ctx->Pend[Ctx->PendCount].Buf = Buffer;
    Ctx->Pend[Ctx->PendCount].Len = Length;
    Ctx->Pend[Ctx->PendCount].ReportId = ReportId;
    Ctx->Pend[Ctx->PendCount].Device = Device;
    Ctx->PendCount++;

    SubmitNext(Ctx);
}

VOID
EvtVhfReadyForNextReadReport(
    _In_ PVOID VhfClientContext
    )
{
    PVHF_CLIENT vc = (PVHF_CLIENT)VhfClientContext;
    PFAKEWHEEL_CONTEXT ctx = vc->Ctx;

    ctx->SubmitInFlight[vc->Index] = FALSE;
    SubmitNext(ctx);
}

// ---------------------------------------------------------------------------
// The HID++ feature responder
// ---------------------------------------------------------------------------

static VOID
HandleHidppRequest(
    _In_ PFAKEWHEEL_CONTEXT Ctx,
    _In_ const UCHAR* Buf,
    _In_ USHORT Len
    )
{
    UCHAR featureIndex, fn;
    const UCHAR* p;
    UCHAR payload[6] = { 0, 0, 0, 0, 0, 0 };

    LogRaw(Ctx, 'R', Buf, Len);

    if (Len < 5)
    {
        return;
    }

    featureIndex = Buf[2];
    fn = (UCHAR)(Buf[3] >> 4);
    p = Buf + 4;

    switch (featureIndex)
    {
    case 0x00: // Root — feature discovery
        if (fn == 0) // find feature (params: feature id big-endian)
        {
            USHORT id = (USHORT)((p[0] << 8) | p[1]);
            payload[0] = FeatureIndexForId(id);
        }
        else if (fn == 1) // feature count
        {
            payload[0] = FEATURE_COUNT;
        }
        else if (fn == 2) // get feature by index
        {
            USHORT id = FeatureIdForIndex(p[0]);
            payload[0] = (UCHAR)(id >> 8);
            payload[1] = (UCHAR)(id & 0xFF);
        }
        break;

    case FEAT_STRENGTH: // 0x8136 — strength (Nm * 8191.875, little-endian)
        if (fn == 1)
        {
            payload[0] = (UCHAR)(Ctx->StrengthRaw & 0xFF);
            payload[1] = (UCHAR)(Ctx->StrengthRaw >> 8);
        }
        else if (fn == 2)
        {
            Ctx->StrengthRaw = (USHORT)(p[0] | ((USHORT)p[1] << 8));
        }
        break;

    case FEAT_ROTATION: // 0x8138 — degrees, big-endian
        if (fn == 1)
        {
            payload[0] = (UCHAR)(Ctx->RotationLocks >> 8);
            payload[1] = (UCHAR)(Ctx->RotationLocks & 0xFF);
        }
        else if (fn == 2)
        {
            Ctx->RotationLocks = (USHORT)(((USHORT)p[0] << 8) | p[1]);
        }
        break;

    case FEAT_PROFILE: // 0x8137 — mode (0 = desktop, 1-5 = onboard slot)
        if (fn == 1)
        {
            payload[0] = Ctx->ProfileMode;
        }
        else if (fn == 2)
        {
            Ctx->ProfileMode = p[0];
        }
        break;

    case FEAT_TRUEFORCE: // 0x8139 — level (0-100%, * 655.35, little-endian)
        if (fn == 1)
        {
            payload[0] = (UCHAR)(Ctx->TrueForceRaw & 0xFF);
            payload[1] = (UCHAR)(Ctx->TrueForceRaw >> 8);
        }
        else if (fn == 3)
        {
            Ctx->TrueForceRaw = (USHORT)(p[0] | ((USHORT)p[1] << 8));
        }
        break;

    case FEAT_DAMPING: // 0x8133 — the app reads via fn1; non-zero payload = SET
        if (fn == 1)
        {
            if (p[0] != 0 || p[1] != 0)
            {
                Ctx->DampingRaw = p[0];
            }
            payload[0] = Ctx->DampingRaw;
        }
        break;

    case FEAT_OLED: // 0x8130 — dynamic display
        if (fn == 0)
        {
            payload[0] = 0x0A; // 10 layouts
        }
        else if (fn == 1)
        {
            // Layout J descriptor (verified readback: 09 0A 13 0A 13 0A)
            payload[0] = 0x09;
            payload[1] = 0x0A;
            payload[2] = 0x13;
            payload[3] = 0x0A;
            payload[4] = 0x13;
            payload[5] = 0x0A;
        }
        // fn2 (clear) / fn3 (frame) -> plain ack; frame bytes already logged
        break;

    default:
        break; // unknown feature -> zero-payload ack
    }

    // The real wheel broadcasts its answer on every HID++ report-id collection.
    BuildResponseFrame(RID_SHORT, featureIndex, fn, payload, Ctx->LastResp7);
    BuildResponseFrame(RID_LONG, featureIndex, fn, payload, Ctx->LastResp20);
    BuildResponseFrame(RID_VERYLONG, featureIndex, fn, payload, Ctx->LastResp64);

    LogRaw(Ctx, 'S', Ctx->LastResp64, 64);

    QueueSubmit(Ctx, 2, Ctx->LastResp64, 64, RID_VERYLONG);
    QueueSubmit(Ctx, 1, Ctx->LastResp20, 20, RID_LONG);
    QueueSubmit(Ctx, 0, Ctx->LastResp7, 7, RID_SHORT);
}

// ---------------------------------------------------------------------------
// VHF asynchronous operation callbacks
// ---------------------------------------------------------------------------

VOID
EvtVhfAsyncOperationWriteReport(
    _In_ PVOID VhfClientContext,
    _In_ VHFOPERATIONHANDLE VhfOperationHandle,
    _In_opt_ PVOID VhfOperationContext,
    _In_ PHID_XFER_PACKET HidTransferPacket
    )
{
    PVHF_CLIENT vc = (PVHF_CLIENT)VhfClientContext;
    PFAKEWHEEL_CONTEXT ctx = vc->Ctx;
    NTSTATUS status = STATUS_SUCCESS;

    UNREFERENCED_PARAMETER(VhfOperationContext);

    if (HidTransferPacket == NULL || HidTransferPacket->reportBuffer == NULL)
    {
        status = STATUS_INVALID_PARAMETER;
    }
    else
    {
        HandleHidppRequest(ctx,
            HidTransferPacket->reportBuffer,
            (USHORT)HidTransferPacket->reportBufferLen);
    }

    VhfAsyncOperationComplete(VhfOperationHandle, status);
}

VOID
EvtVhfAsyncOperationGetInputReport(
    _In_ PVOID VhfClientContext,
    _In_ VHFOPERATIONHANDLE VhfOperationHandle,
    _In_opt_ PVOID VhfOperationContext,
    _In_ PHID_XFER_PACKET HidTransferPacket
    )
{
    PVHF_CLIENT vc = (PVHF_CLIENT)VhfClientContext;
    PFAKEWHEEL_CONTEXT ctx = vc->Ctx;
    NTSTATUS status = STATUS_SUCCESS;

    UNREFERENCED_PARAMETER(VhfOperationContext);

    if (HidTransferPacket == NULL || HidTransferPacket->reportBuffer == NULL)
    {
        status = STATUS_INVALID_PARAMETER;
    }
    else
    {
        UCHAR reportId = HidTransferPacket->reportId;
        const UCHAR* src;
        USHORT copy;

        if (reportId == RID_SHORT)
        {
            src = ctx->LastResp7;
            copy = 7;
        }
        else if (reportId == RID_LONG)
        {
            src = ctx->LastResp20;
            copy = 20;
        }
        else
        {
            src = ctx->LastResp64;
            copy = 64;
        }

        if ((ULONG)copy > HidTransferPacket->reportBufferLen)
        {
            copy = (USHORT)HidTransferPacket->reportBufferLen;
        }
        RtlCopyMemory(HidTransferPacket->reportBuffer, src, copy);
    }

    VhfAsyncOperationComplete(VhfOperationHandle, status);
}

VOID
EvtVhfAsyncOperationSetFeature(
    _In_ PVOID VhfClientContext,
    _In_ VHFOPERATIONHANDLE VhfOperationHandle,
    _In_opt_ PVOID VhfOperationContext,
    _In_ PHID_XFER_PACKET HidTransferPacket
    )
{
    // SET_FEATURE from the host — same HID++ semantics as an output report.
    EvtVhfAsyncOperationWriteReport(VhfClientContext,
        VhfOperationHandle, VhfOperationContext, HidTransferPacket);
}

VOID
EvtVhfAsyncOperationGetFeature(
    _In_ PVOID VhfClientContext,
    _In_ VHFOPERATIONHANDLE VhfOperationHandle,
    _In_opt_ PVOID VhfOperationContext,
    _In_ PHID_XFER_PACKET HidTransferPacket
    )
{
    // No feature reports are declared in the descriptor; answer empty.
    UNREFERENCED_PARAMETER(VhfClientContext);
    UNREFERENCED_PARAMETER(VhfOperationContext);
    UNREFERENCED_PARAMETER(HidTransferPacket);

    VhfAsyncOperationComplete(VhfOperationHandle, STATUS_SUCCESS);
}

VOID
EvtVhfCleanup(
    _In_ PVOID VhfClientContext
    )
{
    UNREFERENCED_PARAMETER(VhfClientContext);
}

// ---------------------------------------------------------------------------
// WDF driver plumbing
// ---------------------------------------------------------------------------

static VOID
EvtDeviceCleanup(
    _In_ WDFOBJECT Object
    )
{
    PFAKEWHEEL_CONTEXT ctx = FakeWheelGetContext((WDFDEVICE)Object);

    for (UCHAR i = 0; i < 3; i++)
    {
        if (ctx->VhfHandle[i] != NULL)
        {
            VhfDelete(ctx->VhfHandle[i], TRUE);
            ctx->VhfHandle[i] = NULL;
        }
    }
    if (ctx->LogHandle != NULL)
    {
        ZwClose(ctx->LogHandle);
        ctx->LogHandle = NULL;
    }
}

NTSTATUS
EvtDeviceAdd(
    _In_ WDFDRIVER Driver,
    _Inout_ PWDFDEVICE_INIT DeviceInit
    )
{
    NTSTATUS status;
    WDF_OBJECT_ATTRIBUTES deviceAttributes;
    WDFDEVICE device;
    PFAKEWHEEL_CONTEXT ctx;

    UNREFERENCED_PARAMETER(Driver);

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&deviceAttributes, FAKEWHEEL_CONTEXT);
    deviceAttributes.EvtCleanupCallback = EvtDeviceCleanup;

    status = WdfDeviceCreate(&DeviceInit, &deviceAttributes, &device);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    ctx = FakeWheelGetContext(device);
    ctx->Device = device;
    RtlZeroMemory(ctx->VhfHandle, sizeof(ctx->VhfHandle));
    ctx->LogHandle = NULL;
    RtlZeroMemory(ctx->SubmitInFlight, sizeof(ctx->SubmitInFlight));
    ctx->PendCount = 0;

    // Boot state of the emulated wheel (the tester's verified settings)
    ctx->StrengthRaw = 0xFFFF;   // 8.0 Nm
    ctx->RotationLocks = 1080;   // 1080 deg
    ctx->TrueForceRaw = 0;       // 0%
    ctx->ProfileMode = 0;        // desktop mode
    ctx->DampingRaw = 0;         // 0%

    // Create the three VHF devices — the KMDF pattern passes the DEVICE
    // OBJECT directly (no file handle, no reflector).
    {
        static const UCHAR* const descriptors[3] =
        {
            FakeWheelShortDescriptor,
            FakeWheelLongDescriptor,
            FakeWheelVeryLongDescriptor,
        };
        static const USHORT descriptorLens[3] =
        {
            (USHORT)sizeof(FakeWheelShortDescriptor),
            (USHORT)sizeof(FakeWheelLongDescriptor),
            (USHORT)sizeof(FakeWheelVeryLongDescriptor),
        };

        for (UCHAR i = 0; i < 3; i++)
        {
            VHF_CONFIG vhfConfig;

            VHF_CONFIG_INIT(
                &vhfConfig,
                WdfDeviceWdmGetDeviceObject(device),
                descriptorLens[i],
                (PUCHAR)descriptors[i]);

            ctx->VhfClient[i].Ctx = ctx;
            ctx->VhfClient[i].Index = i;
            vhfConfig.VhfClientContext = &ctx->VhfClient[i];
            vhfConfig.VendorID = FAKEWHEEL_VID;
            vhfConfig.ProductID = FAKEWHEEL_PID;
            vhfConfig.VersionNumber = FAKEWHEEL_VERSION;
            vhfConfig.EvtVhfReadyForNextReadReport = EvtVhfReadyForNextReadReport;
            vhfConfig.EvtVhfAsyncOperationWriteReport = EvtVhfAsyncOperationWriteReport;
            vhfConfig.EvtVhfAsyncOperationGetInputReport = EvtVhfAsyncOperationGetInputReport;
            vhfConfig.EvtVhfAsyncOperationSetFeature = EvtVhfAsyncOperationSetFeature;
            vhfConfig.EvtVhfAsyncOperationGetFeature = EvtVhfAsyncOperationGetFeature;
            vhfConfig.EvtVhfCleanup = EvtVhfCleanup;

            status = VhfCreate(&vhfConfig, &ctx->VhfHandle[i]);
            if (!NT_SUCCESS(status))
            {
                ctx->VhfHandle[i] = NULL;
                return status;
            }

            status = VhfStart(ctx->VhfHandle[i]);
            if (!NT_SUCCESS(status))
            {
                VhfDelete(ctx->VhfHandle[i], TRUE);
                ctx->VhfHandle[i] = NULL;
                return status;
            }
        }
    }

    return STATUS_SUCCESS;
}

NTSTATUS
DriverEntry(
    _In_ PDRIVER_OBJECT DriverObject,
    _In_ PUNICODE_STRING RegistryPath
    )
{
    NTSTATUS status;
    WDF_DRIVER_CONFIG config;

    WDF_DRIVER_CONFIG_INIT(&config, EvtDeviceAdd);

    status = WdfDriverCreate(
        DriverObject,
        RegistryPath,
        WDF_NO_OBJECT_ATTRIBUTES,
        &config,
        WDF_NO_HANDLE);

    return status;
}