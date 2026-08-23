/*++

FakeWheel RS50 — virtual Logitech RS50 HID minidriver (UMDF2, mshidumdf).
Based on Microsoft's vhidmini2 sample. Presents the RS50's three HID++
report-id collections (page 0xFF43, report ids 0x10/0x11/0x12) and answers
HID++ feature discovery, setting reads and writes from the tester's verified
response bytes. Every byte the host sends is logged — the driver is the capture.

--*/

#include "fake_rs50mini.h"

//
// The RS50 HID++ report descriptor: three collections on page 0xFF43.
//   0x0701 short 7-byte (report 0x10), 0x0702 long 20-byte (report 0x11),
//   0x0704 very-long 64-byte (report 0x12).
//
HID_REPORT_DESCRIPTOR G_DefaultReportDescriptor[] = {
    // ---- Collection 0x0701 (HID++ short), report 0x10 ----
    0x06, 0x43, 0xFF,               // USAGE_PAGE (0xFF43)
    0x0A, 0x01, 0x07,               // USAGE (0x0701)
    0xA1, 0x01,                     // COLLECTION (Application)
        0x85, 0x10,                 //   REPORT_ID (0x10)
        0x09, 0x01,                 //   USAGE (0x0701)
        0x15, 0x00,                 //   LOGICAL_MINIMUM (0)
        0x26, 0xFF, 0x00,           //   LOGICAL_MAXIMUM (255)
        0x75, 0x08,                 //   REPORT_SIZE (8)
        0x95, 0x06,                 //   REPORT_COUNT (6)  -> 7 bytes with id
        0x81, 0x00,                 //   INPUT (Data,Ary,Abs)
        0x95, 0x06,                 //   REPORT_COUNT (6)
        0x91, 0x00,                 //   OUTPUT (Data,Ary,Abs)
    0xC0,                           // END_COLLECTION
    // ---- Collection 0x0702 (HID++ long), report 0x11 ----
    0x06, 0x43, 0xFF,
    0x0A, 0x02, 0x07,
    0xA1, 0x01,
        0x85, 0x11,
        0x09, 0x01,
        0x15, 0x00,
        0x26, 0xFF, 0x00,
        0x75, 0x08,
        0x95, 0x13,                 //   REPORT_COUNT (19)  -> 20 bytes with id
        0x81, 0x00,
        0x95, 0x13,
        0x91, 0x00,
    0xC0,
    // ---- Collection 0x0704 (HID++ very long), report 0x12 -----
    0x06, 0x43, 0xFF,
    0x0A, 0x04, 0x07,
    0xA1, 0x01,
        0x85, 0x12,
        0x09, 0x01,
        0x15, 0x00,
        0x26, 0xFF, 0x00,
        0x75, 0x08,
        0x95, 0x3F,                 //   REPORT_COUNT (63)  -> 64 bytes with id
        0x81, 0x00,
        0x95, 0x3F,
        0x91, 0x00,
    0xC0,
};

HID_DESCRIPTOR G_DefaultHidDescriptor = {
    0x09,   // length of HID descriptor
    0x21,   // descriptor type == HID  0x21
    0x0111, // hid spec release 1.11
    0x00,   // country code == Not Specified
    0x01,   // number of HID class descriptors
    {
        0x22,                               // report descriptor type 0x22
        sizeof(G_DefaultReportDescriptor)   // total length
    }
};

#define FAKEWHEEL_PRODUCT_STRING     L"Logitech G HUB RS50 (USB)"
#define FAKEWHEEL_MANUFACTURER_STRING L"Logitech"
#define FAKEWHEEL_SERIAL_NUMBER_STRING L"RS50FAKE0001"
#define FAKEWHEEL_DEVICE_STRING      L"Logitech G HUB RS50 (USB)"
#define FAKEWHEEL_DEVICE_STRING_INDEX 5

// ---------------------------------------------------------------------------
// Capture logging — every exchanged byte, appended to C:\Windows\Temp\FakeRs50.log
// ---------------------------------------------------------------------------

static VOID
LogRaw(
    _In_ PDEVICE_CONTEXT Ctx,
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

static VOID
ServePendingRead(
    _In_ PDEVICE_CONTEXT Ctx
    )
{
    NTSTATUS status;
    WDFREQUEST request = NULL;
    WDFMEMORY memory;
    size_t outputBufferLength;
    const UCHAR* src;
    USHORT copy;

    // Complete one pending interrupt read with the last response.
    status = WdfIoQueueRetrieveNextRequest(Ctx->ManualQueue, &request);
    if (!NT_SUCCESS(status))
    {
        return;
    }

    status = WdfRequestRetrieveOutputMemory(request, &memory);
    if (!NT_SUCCESS(status))
    {
        WdfRequestComplete(request, status);
        return;
    }

    WdfMemoryGetBuffer(memory, &outputBufferLength);

    // Pick the broadcast form that fits the reader's buffer.
    if (outputBufferLength >= 64)
    {
        src = Ctx->LastResp64;
        copy = 64;
    }
    else if (outputBufferLength >= 20)
    {
        src = Ctx->LastResp20;
        copy = 20;
    }
    else
    {
        src = Ctx->LastResp7;
        copy = 7;
    }

    if (outputBufferLength < copy)
    {
        WdfRequestComplete(request, STATUS_INVALID_BUFFER_SIZE);
        return;
    }

    status = WdfMemoryCopyFromBuffer(memory, 0, (PVOID)src, copy);
    if (!NT_SUCCESS(status))
    {
        WdfRequestComplete(request, status);
        return;
    }

    WdfRequestSetInformation(request, copy);
    WdfRequestComplete(request, STATUS_SUCCESS);
}

VOID
HandleHidppRequest(
    _In_ PDEVICE_CONTEXT Ctx,
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

    ServePendingRead(Ctx);
}

NTSTATUS
DriverEntry(
    _In_  PDRIVER_OBJECT    DriverObject,
    _In_  PUNICODE_STRING   RegistryPath
    )
{
    WDF_DRIVER_CONFIG       config;
    NTSTATUS                status;

    WDF_DRIVER_CONFIG_INIT(&config, EvtDeviceAdd);

    status = WdfDriverCreate(DriverObject,
                            RegistryPath,
                            WDF_NO_OBJECT_ATTRIBUTES,
                            &config,
                            WDF_NO_HANDLE);
    return status;
}

NTSTATUS
EvtDeviceAdd(
    _In_  WDFDRIVER         Driver,
    _Inout_ PWDFDEVICE_INIT DeviceInit
    )
{
    NTSTATUS                status;
    WDF_OBJECT_ATTRIBUTES   deviceAttributes;
    WDFDEVICE               device;
    PDEVICE_CONTEXT         deviceContext;
    PHID_DEVICE_ATTRIBUTES  hidAttributes;
    UNREFERENCED_PARAMETER  (Driver);

    //
    // Mark ourselves as a filter, which also relinquishes power policy ownership
    //
    WdfFdoInitSetFilter(DeviceInit);

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(
                            &deviceAttributes,
                            DEVICE_CONTEXT);

    status = WdfDeviceCreate(&DeviceInit,
                            &deviceAttributes,
                            &device);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    deviceContext = GetDeviceContext(device);
    deviceContext->Device       = device;
    deviceContext->DeviceData   = 0;

    hidAttributes = &deviceContext->HidDeviceAttributes;
    RtlZeroMemory(hidAttributes, sizeof(HID_DEVICE_ATTRIBUTES));
    hidAttributes->Size          = sizeof(HID_DEVICE_ATTRIBUTES);
    hidAttributes->VendorID      = FAKEWHEEL_VID;
    hidAttributes->ProductID     = FAKEWHEEL_PID;
    hidAttributes->VersionNumber = FAKEWHEEL_VERSION;

    // Boot state of the emulated wheel (the tester's verified settings)
    deviceContext->StrengthRaw   = 0xFFFF;   // 8.0 Nm
    deviceContext->RotationLocks = 1080;     // 1080 deg
    deviceContext->TrueForceRaw  = 0;        // 0%
    deviceContext->ProfileMode   = 0;        // desktop mode
    deviceContext->DampingRaw    = 0;        // 0%
    deviceContext->LogHandle     = NULL;

    status = QueueCreate(device,
                         &deviceContext->DefaultQueue);
    if( !NT_SUCCESS(status) ) {
        return status;
    }

    status = ManualQueueCreate(device,
                               &deviceContext->ManualQueue);
    if( !NT_SUCCESS(status) ) {
        return status;
    }

    deviceContext->HidDescriptor = G_DefaultHidDescriptor;

    status = CheckRegistryForDescriptor(device);
    if (NT_SUCCESS(status)){
        status = ReadDescriptorFromRegistry(device);
    }

    if (!NT_SUCCESS(status)){
        deviceContext->ReportDescriptor = G_DefaultReportDescriptor;
        status = STATUS_SUCCESS;
    }

    return status;
}

#ifdef _KERNEL_MODE
EVT_WDF_IO_QUEUE_IO_INTERNAL_DEVICE_CONTROL EvtIoDeviceControl;
#else
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL          EvtIoDeviceControl;
#endif

NTSTATUS
QueueCreate(
    _In_  WDFDEVICE         Device,
    _Out_ WDFQUEUE          *Queue
    )
{
    NTSTATUS                status;
    WDF_IO_QUEUE_CONFIG     queueConfig;
    WDF_OBJECT_ATTRIBUTES   queueAttributes;
    WDFQUEUE                queue;
    PQUEUE_CONTEXT          queueContext;

    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(
                            &queueConfig,
                            WdfIoQueueDispatchParallel);

#ifdef _KERNEL_MODE
    queueConfig.EvtIoInternalDeviceControl  = EvtIoDeviceControl;
#else
    //
    // HIDclass uses INTERNAL_IOCTL which is not supported by UMDF. Therefore
    // the hidumdf.sys changes the IOCTL type to DEVICE_CONTROL for next stack
    // and sends it down
    //
    queueConfig.EvtIoDeviceControl          = EvtIoDeviceControl;
#endif

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(
                            &queueAttributes,
                            QUEUE_CONTEXT);

    status = WdfIoQueueCreate(
                            Device,
                            &queueConfig,
                            &queueAttributes,
                            &queue);

    if( !NT_SUCCESS(status) ) {
        return status;
    }

    queueContext = GetQueueContext(queue);
    queueContext->Queue         = queue;
    queueContext->DeviceContext = GetDeviceContext(Device);
    queueContext->OutputReport  = 0;

    *Queue = queue;
    return status;
}

VOID
EvtIoDeviceControl(
    _In_  WDFQUEUE          Queue,
    _In_  WDFREQUEST        Request,
    _In_  size_t            OutputBufferLength,
    _In_  size_t            InputBufferLength,
    _In_  ULONG             IoControlCode
    )
{
    NTSTATUS                status;
    BOOLEAN                 completeRequest = TRUE;
    WDFDEVICE               device = WdfIoQueueGetDevice(Queue);
    PDEVICE_CONTEXT         deviceContext = NULL;
    PQUEUE_CONTEXT          queueContext = GetQueueContext(Queue);
    UNREFERENCED_PARAMETER  (OutputBufferLength);
    UNREFERENCED_PARAMETER  (InputBufferLength);

    deviceContext = GetDeviceContext(device);

    switch (IoControlCode)
    {
    case IOCTL_HID_GET_DEVICE_DESCRIPTOR:
        status = RequestCopyFromBuffer(Request,
                            &deviceContext->HidDescriptor,
                            deviceContext->HidDescriptor.bLength);
        break;

    case IOCTL_HID_GET_DEVICE_ATTRIBUTES:
        status = RequestCopyFromBuffer(Request,
                            &queueContext->DeviceContext->HidDeviceAttributes,
                            sizeof(HID_DEVICE_ATTRIBUTES));
        break;

    case IOCTL_HID_GET_REPORT_DESCRIPTOR:
        status = RequestCopyFromBuffer(Request,
                            deviceContext->ReportDescriptor,
                            deviceContext->HidDescriptor.DescriptorList[0].wReportLength);
        break;

    case IOCTL_HID_READ_REPORT:
        status = ReadReport(queueContext, Request, &completeRequest);
        break;

    case IOCTL_HID_WRITE_REPORT:
        status = WriteReport(queueContext, Request);
        break;

#ifdef _KERNEL_MODE

    case IOCTL_HID_GET_FEATURE:
        status = GetFeature(queueContext, Request);
        break;

    case IOCTL_HID_SET_FEATURE:
        status = SetFeature(queueContext, Request);
        break;

    case IOCTL_HID_GET_INPUT_REPORT:
        status = GetInputReport(queueContext, Request);
        break;

    case IOCTL_HID_SET_OUTPUT_REPORT:
        status = SetOutputReport(queueContext, Request);
        break;

#else // UMDF specific

    case IOCTL_UMDF_HID_GET_FEATURE:
        status = GetFeature(queueContext, Request);
        break;

    case IOCTL_UMDF_HID_SET_FEATURE:
        status = SetFeature(queueContext, Request);
        break;

    case IOCTL_UMDF_HID_GET_INPUT_REPORT:
        status = GetInputReport(queueContext, Request);
        break;

    case IOCTL_UMDF_HID_SET_OUTPUT_REPORT:
        status = SetOutputReport(queueContext, Request);
        break;

#endif // _KERNEL_MODE

    case IOCTL_HID_GET_STRING:
        status = GetString(Request);
        break;

    case IOCTL_HID_GET_INDEXED_STRING:
        status = GetIndexedString(Request);
        break;

    case IOCTL_HID_SEND_IDLE_NOTIFICATION_REQUEST:
    case IOCTL_HID_ACTIVATE_DEVICE:
    case IOCTL_HID_DEACTIVATE_DEVICE:
    case IOCTL_GET_PHYSICAL_DESCRIPTOR:
    default:
        status = STATUS_NOT_IMPLEMENTED;
        break;
    }

    if (completeRequest) {
        WdfRequestComplete(Request, status);
    }
}

NTSTATUS
RequestCopyFromBuffer(
    _In_  WDFREQUEST        Request,
    _In_  PVOID             SourceBuffer,
    _When_(NumBytesToCopyFrom == 0, __drv_reportError(NumBytesToCopyFrom cannot be zero))
    _In_  size_t            NumBytesToCopyFrom
    )
{
    NTSTATUS                status;
    WDFMEMORY               memory;
    size_t                  outputBufferLength;

    status = WdfRequestRetrieveOutputMemory(Request, &memory);
    if( !NT_SUCCESS(status) ) {
        return status;
    }

    WdfMemoryGetBuffer(memory, &outputBufferLength);
    if (outputBufferLength < NumBytesToCopyFrom) {
        return STATUS_INVALID_BUFFER_SIZE;
    }

    status = WdfMemoryCopyFromBuffer(memory,
                                    0,
                                    SourceBuffer,
                                    NumBytesToCopyFrom);
    if( !NT_SUCCESS(status) ) {
        return status;
    }

    WdfRequestSetInformation(Request, NumBytesToCopyFrom);
    return status;
}

NTSTATUS
ReadReport(
    _In_  PQUEUE_CONTEXT    QueueContext,
    _In_  WDFREQUEST        Request,
    _Always_(_Out_)
          BOOLEAN*          CompleteRequest
    )
{
    NTSTATUS                status;

    //
    // forward the request to manual queue; it is completed when a HID++
    // response is generated (or by the periodic timer resending the last one).
    //
    status = WdfRequestForwardToIoQueue(
                            Request,
                            QueueContext->DeviceContext->ManualQueue);
    if( !NT_SUCCESS(status) ) {
        *CompleteRequest = TRUE;
    }
    else {
        *CompleteRequest = FALSE;
    }

    return status;
}

NTSTATUS
WriteReport(
    _In_  PQUEUE_CONTEXT    QueueContext,
    _In_  WDFREQUEST        Request
    )
{
    NTSTATUS                status;
    HID_XFER_PACKET         packet;

    status = RequestGetHidXferPacket_ToWriteToDevice(
                            Request,
                            &packet);
    if( !NT_SUCCESS(status) ) {
        return status;
    }

    if (packet.reportId != RID_SHORT &&
        packet.reportId != RID_LONG &&
        packet.reportId != RID_VERYLONG) {
        return STATUS_INVALID_PARAMETER;
    }

    HandleHidppRequest(QueueContext->DeviceContext,
                       packet.reportBuffer,
                       (USHORT)packet.reportBufferLen);

    WdfRequestSetInformation(Request, packet.reportBufferLen);
    return STATUS_SUCCESS;
}

NTSTATUS
GetFeature(
    _In_  PQUEUE_CONTEXT    QueueContext,
    _In_  WDFREQUEST        Request
    )
{
    // No feature reports are declared; answer empty.
    UNREFERENCED_PARAMETER(QueueContext);
    UNREFERENCED_PARAMETER(Request);

    return STATUS_NOT_IMPLEMENTED;
}

NTSTATUS
SetFeature(
    _In_  PQUEUE_CONTEXT    QueueContext,
    _In_  WDFREQUEST        Request
    )
{
    // Same HID++ semantics as an output report.
    return WriteReport(QueueContext, Request);
}

NTSTATUS
GetInputReport(
    _In_  PQUEUE_CONTEXT    QueueContext,
    _In_  WDFREQUEST        Request
    )
{
    NTSTATUS                status;
    HID_XFER_PACKET         packet;
    PDEVICE_CONTEXT         ctx = QueueContext->DeviceContext;
    const UCHAR*            src;
    USHORT                  copy;

    status = RequestGetHidXferPacket_ToReadFromDevice(
                            Request,
                            &packet);
    if( !NT_SUCCESS(status) ) {
        return status;
    }

    if (packet.reportId == RID_SHORT)
    {
        src = ctx->LastResp7;
        copy = 7;
    }
    else if (packet.reportId == RID_LONG)
    {
        src = ctx->LastResp20;
        copy = 20;
    }
    else
    {
        src = ctx->LastResp64;
        copy = 64;
    }

    if (packet.reportBufferLen < copy)
    {
        return STATUS_INVALID_BUFFER_SIZE;
    }

    RtlCopyMemory(packet.reportBuffer, src, copy);
    WdfRequestSetInformation(Request, copy);
    return STATUS_SUCCESS;
}

NTSTATUS
SetOutputReport(
    _In_  PQUEUE_CONTEXT    QueueContext,
    _In_  WDFREQUEST        Request
    )
{
    return WriteReport(QueueContext, Request);
}

NTSTATUS
GetStringId(
    _In_  WDFREQUEST        Request,
    _Out_ ULONG            *StringId,
    _Out_ ULONG            *LanguageId
    )
{
    NTSTATUS                status;
    ULONG                   inputValue;

#ifdef _KERNEL_MODE

    WDF_REQUEST_PARAMETERS  requestParameters;

    WDF_REQUEST_PARAMETERS_INIT(&requestParameters);
    WdfRequestGetParameters(Request, &requestParameters);

    inputValue = PtrToUlong(
        requestParameters.Parameters.DeviceIoControl.Type3InputBuffer);

    status = STATUS_SUCCESS;

#else

    WDFMEMORY               inputMemory;
    size_t                  inputBufferLength;
    PVOID                   inputBuffer;

    status = WdfRequestRetrieveInputMemory(Request, &inputMemory);
    if( !NT_SUCCESS(status) ) {
        return status;
    }
    inputBuffer = WdfMemoryGetBuffer(inputMemory, &inputBufferLength);

    if (inputBufferLength < sizeof(ULONG))
    {
        return STATUS_INVALID_BUFFER_SIZE;
    }

    inputValue = (*(PULONG)inputBuffer);

#endif

    *StringId = (inputValue & 0x0ffff);
    *LanguageId = (inputValue >> 16);

    return status;
}

NTSTATUS
GetIndexedString(
    _In_  WDFREQUEST        Request
    )
{
    NTSTATUS                status;
    ULONG                   languageId, stringIndex;

    status = GetStringId(Request, &stringIndex, &languageId);

    UNREFERENCED_PARAMETER(languageId);

    if (NT_SUCCESS(status)) {

        if (stringIndex != FAKEWHEEL_DEVICE_STRING_INDEX)
        {
            return STATUS_INVALID_PARAMETER;
        }

        status = RequestCopyFromBuffer(Request, FAKEWHEEL_DEVICE_STRING, sizeof(FAKEWHEEL_DEVICE_STRING));
    }
    return status;
}

NTSTATUS
GetString(
    _In_  WDFREQUEST        Request
    )
{
    NTSTATUS                status;
    ULONG                   languageId, stringId;
    size_t                  stringSizeCb;
    PWSTR                   string;

    status = GetStringId(Request, &stringId, &languageId);

    UNREFERENCED_PARAMETER(languageId);

    if (!NT_SUCCESS(status)) {
        return status;
    }

    switch (stringId){
    case HID_STRING_ID_IMANUFACTURER:
        stringSizeCb = sizeof(FAKEWHEEL_MANUFACTURER_STRING);
        string = FAKEWHEEL_MANUFACTURER_STRING;
        break;
    case HID_STRING_ID_IPRODUCT:
        stringSizeCb = sizeof(FAKEWHEEL_PRODUCT_STRING);
        string = FAKEWHEEL_PRODUCT_STRING;
        break;
    case HID_STRING_ID_ISERIALNUMBER:
        stringSizeCb = sizeof(FAKEWHEEL_SERIAL_NUMBER_STRING);
        string = FAKEWHEEL_SERIAL_NUMBER_STRING;
        break;
    default:
        return STATUS_INVALID_PARAMETER;
    }

    status = RequestCopyFromBuffer(Request, string, stringSizeCb);
    return status;
}

NTSTATUS
ManualQueueCreate(
    _In_  WDFDEVICE         Device,
    _Out_ WDFQUEUE          *Queue
    )
{
    NTSTATUS                status;
    WDF_IO_QUEUE_CONFIG     queueConfig;
    WDF_OBJECT_ATTRIBUTES   queueAttributes;
    WDFQUEUE                queue;
    PMANUAL_QUEUE_CONTEXT   queueContext;
    WDF_TIMER_CONFIG        timerConfig;
    WDF_OBJECT_ATTRIBUTES   timerAttributes;
    ULONG                   timerPeriodInSeconds = 2;

    WDF_IO_QUEUE_CONFIG_INIT(
                            &queueConfig,
                            WdfIoQueueDispatchManual);

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(
                            &queueAttributes,
                            MANUAL_QUEUE_CONTEXT);

    status = WdfIoQueueCreate(
                            Device,
                            &queueConfig,
                            &queueAttributes,
                            &queue);

    if( !NT_SUCCESS(status) ) {
        return status;
    }

    queueContext = GetManualQueueContext(queue);
    queueContext->Queue         = queue;
    queueContext->DeviceContext = GetDeviceContext(Device);

    WDF_TIMER_CONFIG_INIT_PERIODIC(
                            &timerConfig,
                            EvtTimerFunc,
                            timerPeriodInSeconds * 1000);

    WDF_OBJECT_ATTRIBUTES_INIT(&timerAttributes);
    timerAttributes.ParentObject = queue;
    status = WdfTimerCreate(&timerConfig,
                            &timerAttributes,
                            &queueContext->Timer);

    if( !NT_SUCCESS(status) ) {
        return status;
    }

    WdfTimerStart(queueContext->Timer, WDF_REL_TIMEOUT_IN_SEC(1));

    *Queue = queue;

    return status;
}

void
EvtTimerFunc(
    _In_  WDFTIMER          Timer
    )
{
    WDFQUEUE                queue;
    PMANUAL_QUEUE_CONTEXT   queueContext;
    NTSTATUS                status;
    WDFREQUEST              request;

    queue = (WDFQUEUE)WdfTimerGetParentObject(Timer);
    queueContext = GetManualQueueContext(queue);

    //
    // Resend the last response (the real wheel re-broadcasts its state
    // continuously, and hidclass has a pending read).
    //
    status = WdfIoQueueRetrieveNextRequest(
                            queueContext->Queue,
                            &request);

    if (NT_SUCCESS(status)) {
        //
        // Re-inject by completing the read with the last 64-byte response.
        //
        WDFMEMORY               memory;
        size_t                  outputBufferLength;

        status = WdfRequestRetrieveOutputMemory(request, &memory);
        if (NT_SUCCESS(status)) {
            WdfMemoryGetBuffer(memory, &outputBufferLength);
            if (outputBufferLength >= 64) {
                status = WdfMemoryCopyFromBuffer(
                                memory, 0,
                                queueContext->DeviceContext->LastResp64, 64);
                if (NT_SUCCESS(status)) {
                    WdfRequestSetInformation(request, 64);
                }
            }
            else {
                status = STATUS_INVALID_BUFFER_SIZE;
            }
        }

        WdfRequestComplete(request, status);
    }
}

NTSTATUS
CheckRegistryForDescriptor(
        WDFDEVICE Device
        )
{
    WDFKEY          hKey = NULL;
    NTSTATUS        status;
    UNICODE_STRING  valueName;
    ULONG           value;

    status = WdfDeviceOpenRegistryKey(Device,
                                  PLUGPLAY_REGKEY_DEVICE,
                                  KEY_READ,
                                  WDF_NO_OBJECT_ATTRIBUTES,
                                  &hKey);
    if (NT_SUCCESS(status)) {

        RtlInitUnicodeString(&valueName, L"ReadFromRegistry");

        status = WdfRegistryQueryULong (hKey,
                                  &valueName,
                                  &value);

        if (NT_SUCCESS (status)) {
            if (value == 0) {
                status = STATUS_UNSUCCESSFUL;
            }
        }

        WdfRegistryClose(hKey);
    }

    return status;
}

NTSTATUS
ReadDescriptorFromRegistry(
        WDFDEVICE Device
        )
{
    WDFKEY          hKey = NULL;
    NTSTATUS        status;
    UNICODE_STRING  valueName;
    WDFMEMORY       memory;
    size_t          bufferSize;
    PVOID           reportDescriptor;
    PDEVICE_CONTEXT deviceContext;
    WDF_OBJECT_ATTRIBUTES   attributes;

    deviceContext = GetDeviceContext(Device);

    status = WdfDeviceOpenRegistryKey(Device,
                                  PLUGPLAY_REGKEY_DEVICE,
                                  KEY_READ,
                                  WDF_NO_OBJECT_ATTRIBUTES,
                                  &hKey);

    if (NT_SUCCESS(status)) {

        RtlInitUnicodeString(&valueName, L"MyReportDescriptor");

        WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
        attributes.ParentObject = Device;

        status = WdfRegistryQueryMemory (hKey,
                                  &valueName,
                                  NonPagedPool,
                                  &attributes,
                                  &memory,
                                  NULL);

        if (NT_SUCCESS (status)) {

            reportDescriptor = WdfMemoryGetBuffer(memory, &bufferSize);

            deviceContext->ReadReportDescFromRegistry = TRUE;
            deviceContext->ReportDescriptor = reportDescriptor;
            deviceContext->HidDescriptor.DescriptorList[0].wReportLength = (USHORT)bufferSize;
        }

        WdfRegistryClose(hKey);
    }

    return status;
}