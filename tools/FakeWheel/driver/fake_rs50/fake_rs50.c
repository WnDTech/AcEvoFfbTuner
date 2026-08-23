//
// fake_rs50.c — virtual RS50: three VHF devices (one per HID++ collection),
// file handles obtained by CreateFile on the driver's own device interface
// (the open-by-file path fails on this machine; a plain handle is accepted
// by VhfCreate as long as the descriptor passes its parser).
//

#include "fake_rs50.h"

// ---------------------------------------------------------------------------
// Bootstrap trace
// ---------------------------------------------------------------------------

static VOID
TraceMsg(
    _In_ PCWSTR Text
    )
{
    HANDLE h = CreateFileW(
        L"\\??\\C:\\Windows\\Temp\\FakeRs50.trace",
        FILE_APPEND_DATA,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE)
    {
        return;
    }

    WCHAR line[160];
    ULONG n = 0;
    ULONGLONG ms = GetTickCount64();
    {
        WCHAR digits[24];
        ULONG d = 0;
        do { digits[d++] = L"0123456789"[ms % 10]; ms /= 10; } while (ms > 0 && d < 24);
        line[n++] = L'[';
        while (d > 0) { line[n++] = digits[--d]; }
        line[n++] = L']'; line[n++] = L' ';
    }
    while (*Text && n < 140) { line[n++] = *Text++; }
    line[n++] = L'\r';
    line[n++] = L'\n';
    line[n] = L'\0';

    DWORD wrote = 0;
    WriteFile(h, line, n * sizeof(WCHAR), &wrote, NULL);
    FlushFileBuffers(h);
    CloseHandle(h);
}

static VOID
TraceStatus(
    _In_ PCWSTR Tag,
    _In_ NTSTATUS Status
    )
{
    WCHAR buf[40];
    ULONG n = 0;
    static const WCHAR hexc[] = L"0123456789ABCDEF";
    ULONG v = (ULONG)Status;
    while (*Tag && n < 22) { buf[n++] = *Tag++; }
    buf[n++] = L'='; buf[n++] = L'0'; buf[n++] = L'x';
    for (int sh = 28; sh >= 0; sh -= 4) { buf[n++] = hexc[(v >> sh) & 0xF]; }
    buf[n] = L'\0';
    TraceMsg(buf);
}

// ---------------------------------------------------------------------------
// Capture logging
// ---------------------------------------------------------------------------

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
    ULONGLONG ms = GetTickCount64();

    if (Ctx->LogHandle == NULL)
    {
        Ctx->LogHandle = CreateFileW(
            L"\\??\\C:\\Windows\\Temp\\FakeRs50.log",
            FILE_APPEND_DATA,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
        if (Ctx->LogHandle == INVALID_HANDLE_VALUE)
        {
            Ctx->LogHandle = NULL;
            return;
        }
    }

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

    DWORD wrote = 0;
    WriteFile(Ctx->LogHandle, line, n * sizeof(WCHAR), &wrote, NULL);
    FlushFileBuffers(Ctx->LogHandle);
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
    case 0x00:
        if (fn == 0)
        {
            USHORT id = (USHORT)((p[0] << 8) | p[1]);
            payload[0] = FeatureIndexForId(id);
        }
        else if (fn == 1)
        {
            payload[0] = FEATURE_COUNT;
        }
        else if (fn == 2)
        {
            USHORT id = FeatureIdForIndex(p[0]);
            payload[0] = (UCHAR)(id >> 8);
            payload[1] = (UCHAR)(id & 0xFF);
        }
        break;

    case FEAT_STRENGTH:
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

    case FEAT_ROTATION:
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

    case FEAT_PROFILE:
        if (fn == 1)
        {
            payload[0] = Ctx->ProfileMode;
        }
        else if (fn == 2)
        {
            Ctx->ProfileMode = p[0];
        }
        break;

    case FEAT_TRUEFORCE:
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

    case FEAT_DAMPING:
        if (fn == 1)
        {
            if (p[0] != 0 || p[1] != 0)
            {
                Ctx->DampingRaw = p[0];
            }
            payload[0] = Ctx->DampingRaw;
        }
        break;

    case FEAT_OLED:
        if (fn == 0)
        {
            payload[0] = 0x0A;
        }
        else if (fn == 1)
        {
            payload[0] = 0x09;
            payload[1] = 0x0A;
            payload[2] = 0x13;
            payload[3] = 0x0A;
            payload[4] = 0x13;
            payload[5] = 0x0A;
        }
        break;

    default:
        break;
    }

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

static const GUID GUID_FAKEWHEEL_VHF_INTERFACE =
    { 0xD6B4A1F0, 0x5C2E, 0x4B8A, { 0x9F, 0x3D, 0x24, 0xA1, 0xC2, 0x76, 0x04, 0x6D } };

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
        CloseHandle(ctx->LogHandle);
        ctx->LogHandle = NULL;
    }
}

static NTSTATUS
EvtDeviceSelfManagedIoInit(
    _In_ WDFDEVICE Device
    );

static VOID
EvtFileCreateNoop(
    _In_ WDFDEVICE Device,
    _In_ WDFREQUEST Request,
    _In_ WDFFILEOBJECT FileObject
    )
{
    UNREFERENCED_PARAMETER(Device);
    UNREFERENCED_PARAMETER(Request);
    UNREFERENCED_PARAMETER(FileObject);
}

static VOID
EvtFileCloseNoop(
    _In_ WDFFILEOBJECT FileObject
    )
{
    UNREFERENCED_PARAMETER(FileObject);
}

static VOID
EvtFileCleanupNoop(
    _In_ WDFFILEOBJECT FileObject
    )
{
    UNREFERENCED_PARAMETER(FileObject);
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
    WDF_FILEOBJECT_CONFIG fileConfig;
    WDF_OBJECT_ATTRIBUTES fileObjAttrs;
    WDF_PNPPOWER_EVENT_CALLBACKS pnpPowerCallbacks;

    UNREFERENCED_PARAMETER(Driver);

    // VHF requires a file object on the device (the local-target-by-file open
    // creates the file handle that VhfUm.dll uses to reach vhf.sys). DMF
    // registers real file-object callbacks — mirror that.
    WDF_FILEOBJECT_CONFIG_INIT(&fileConfig,
                               EvtFileCreateNoop,
                               EvtFileCloseNoop,
                               EvtFileCleanupNoop);
    WDF_OBJECT_ATTRIBUTES_INIT(&fileObjAttrs);
    WdfDeviceInitSetFileObjectConfig(DeviceInit, &fileConfig, &fileObjAttrs);

    WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&pnpPowerCallbacks);
    pnpPowerCallbacks.EvtDeviceSelfManagedIoInit = EvtDeviceSelfManagedIoInit;
    pnpPowerCallbacks.EvtDeviceReleaseHardware = EvtDeviceReleaseHardware;
    WdfDeviceInitSetPnpPowerEventCallbacks(DeviceInit, &pnpPowerCallbacks);

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&deviceAttributes, FAKEWHEEL_CONTEXT);
    deviceAttributes.EvtCleanupCallback = EvtDeviceCleanup;

    status = WdfDeviceCreate(&DeviceInit, &deviceAttributes, &device);
    TraceMsg(L"DeviceCreate");
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    // Device interface used to open the device for the VHF file handles.
    status = WdfDeviceCreateDeviceInterface(device, &GUID_FAKEWHEEL_VHF_INTERFACE, NULL);
    TraceMsg(L"DeviceInterface");
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

    ctx->StrengthRaw = 0xFFFF;
    ctx->RotationLocks = 1080;
    ctx->TrueForceRaw = 0;
    ctx->ProfileMode = 0;
    ctx->DampingRaw = 0;

    return STATUS_SUCCESS;
}

static NTSTATUS
PerformVhfSetup(
    _In_ PFAKEWHEEL_CONTEXT Ctx
    )
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
        NTSTATUS status;
        WDF_IO_TARGET_OPEN_PARAMS openParams;
        WDF_OBJECT_ATTRIBUTES objectAttributes;
        WDFIOTARGET ioTarget = NULL;
        VHF_CONFIG vhfConfig;
        HANDLE fileHandle;

        // The VHF file handle: a local target opened BY FILE on our own device.
        WDF_OBJECT_ATTRIBUTES_INIT(&objectAttributes);
        objectAttributes.ParentObject = Ctx->Device;
        status = WdfIoTargetCreate(Ctx->Device, &objectAttributes, &ioTarget);
        if (!NT_SUCCESS(status))
        {
            TraceStatus(L"IoTargetCreate", status);
            return status;
        }

        WDF_IO_TARGET_OPEN_PARAMS_INIT_OPEN_BY_FILE(&openParams, NULL);
        {
            UNICODE_STRING fileName;
            RtlInitUnicodeString(&fileName, L"ROOT#SAMPLE#0000");
            openParams.FileName = fileName;
        }
        status = WdfIoTargetOpen(ioTarget, &openParams);
        if (!NT_SUCCESS(status))
        {
            TraceStatus(L"IoTargetOpen", status);
            return status;
        }

        fileHandle = WdfIoTargetWdmGetTargetFileHandle(ioTarget);
        if (fileHandle == NULL || fileHandle == INVALID_HANDLE_VALUE)
        {
            TraceStatus(L"GetFileHandle", STATUS_INVALID_HANDLE);
            return STATUS_INVALID_HANDLE;
        }

        VHF_CONFIG_INIT(
            &vhfConfig,
            fileHandle,
            descriptorLens[i],
            (PUCHAR)descriptors[i]);

        Ctx->VhfClient[i].Ctx = Ctx;
        Ctx->VhfClient[i].Index = i;
        vhfConfig.VhfClientContext = &Ctx->VhfClient[i];
        vhfConfig.VendorID = FAKEWHEEL_VID;
        vhfConfig.ProductID = FAKEWHEEL_PID;
        vhfConfig.VersionNumber = FAKEWHEEL_VERSION;
        vhfConfig.EvtVhfReadyForNextReadReport = EvtVhfReadyForNextReadReport;
        vhfConfig.EvtVhfAsyncOperationWriteReport = EvtVhfAsyncOperationWriteReport;
        vhfConfig.EvtVhfAsyncOperationGetInputReport = EvtVhfAsyncOperationGetInputReport;
        vhfConfig.EvtVhfAsyncOperationSetFeature = EvtVhfAsyncOperationSetFeature;
        vhfConfig.EvtVhfAsyncOperationGetFeature = EvtVhfAsyncOperationGetFeature;
        vhfConfig.EvtVhfCleanup = EvtVhfCleanup;

        status = VhfCreate(&vhfConfig, &Ctx->VhfHandle[i]);
        if (!NT_SUCCESS(status))
        {
            TraceStatus(L"VhfCreate", status);
            Ctx->VhfHandle[i] = NULL;
            return status;
        }

        status = VhfStart(Ctx->VhfHandle[i]);
        if (!NT_SUCCESS(status))
        {
            TraceStatus(L"VhfStart", status);
            VhfDelete(Ctx->VhfHandle[i], TRUE);
            Ctx->VhfHandle[i] = NULL;
            return status;
        }
    }

    return STATUS_SUCCESS;
}

static VOID
EvtVhfSetupTimer(
    _In_ WDFTIMER Timer
    )
{
    PFAKEWHEEL_CONTEXT ctx = FakeWheelGetContext((WDFDEVICE)WdfTimerGetParentObject(Timer));
    NTSTATUS status;

    if (ctx->VhfHandle[0] != NULL)
    {
        WdfTimerStop(Timer, FALSE);
        return;
    }

    status = PerformVhfSetup(ctx);
    if (NT_SUCCESS(status))
    {
        TraceMsg(L"VhfSetup=OK");
        WdfTimerStop(Timer, FALSE);
        return;
    }

    if (++ctx->SetupAttempts >= 50)
    {
        TraceMsg(L"VhfSetup=GIVEUP");
        WdfTimerStop(Timer, FALSE);
    }
}

static NTSTATUS
EvtDeviceSelfManagedIoInit(
    _In_ WDFDEVICE Device
    )
{
    PFAKEWHEEL_CONTEXT ctx = FakeWheelGetContext(Device);
    NTSTATUS status;
    WDF_TIMER_CONFIG timerConfig;
    WDF_OBJECT_ATTRIBUTES timerAttributes;
    WDFTIMER setupTimer = NULL;

    TraceMsg(L"SelfManagedIoInit");

    if (ctx->VhfHandle[0] != NULL)
    {
        return STATUS_SUCCESS;
    }

    WDF_TIMER_CONFIG_INIT_PERIODIC(&timerConfig, EvtVhfSetupTimer, 200);
    WDF_OBJECT_ATTRIBUTES_INIT(&timerAttributes);
    timerAttributes.ParentObject = Device;
    status = WdfTimerCreate(&timerConfig, &timerAttributes, &setupTimer);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    ctx->SetupTimer = setupTimer;
    ctx->SetupAttempts = 0;
    WdfTimerStart(setupTimer, WDF_REL_TIMEOUT_IN_MS(100));

    return STATUS_SUCCESS;
}

NTSTATUS
EvtDeviceReleaseHardware(
    _In_ WDFDEVICE Device,
    _In_ WDFCMRESLIST ResourcesTranslated
    )
{
    PFAKEWHEEL_CONTEXT ctx = FakeWheelGetContext(Device);

    UNREFERENCED_PARAMETER(ResourcesTranslated);

    for (UCHAR i = 0; i < 3; i++)
    {
        if (ctx->VhfHandle[i] != NULL)
        {
            VhfDelete(ctx->VhfHandle[i], TRUE);
            ctx->VhfHandle[i] = NULL;
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

    TraceMsg(L"DriverEntry");

    WDF_DRIVER_CONFIG_INIT(&config, EvtDeviceAdd);

    status = WdfDriverCreate(
        DriverObject,
        RegistryPath,
        WDF_NO_OBJECT_ATTRIBUTES,
        &config,
        WDF_NO_HANDLE);

    return status;
}