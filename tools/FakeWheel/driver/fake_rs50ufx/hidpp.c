/*++
FakeWheel RS50 — virtual Logitech RS50 USB Function Driver (UFX).
HID++ Protocol Implementation.
--*/

#include "fake_rs50ufx.h"

//
// Feature ID to Index mapping
//
static UCHAR FwFeatureIdToIndex(_In_ USHORT FeatureId)
{
    switch (FeatureId) {
    case 0x0000: return FEAT_ROOT;        // Root
    case 0x0001: return FEAT_DEVICE_INFO; // Device Info
    case 0x0002: return 0x02;             // Feature Set
    case 0x0003: return 0x03;             // Device Type
    case 0x0005: return 0x05;             // Device Friendly Name
    case 0x8110: return FEAT_FORCEFF;     // Force Feedback
    case 0x8130: return FEAT_OLED;        // OLED Display
    case 0x8133: return FEAT_DAMPING;     // Damping
    case 0x8136: return FEAT_STRENGTH;    // Strength
    case 0x8137: return FEAT_PROFILE;     // Profile
    case 0x8138: return FEAT_ROTATION;    // Rotation
    case 0x8139: return FEAT_TRUEFORCE;   // TrueForce
    default: return 0x00;
    }
}

static USHORT FwFeatureIndexToId(_In_ UCHAR FeatureIdx)
{
    switch (FeatureIdx) {
    case FEAT_ROOT:        return 0x0000;
    case FEAT_DEVICE_INFO: return 0x0001;
    case 0x02:             return 0x0002;
    case 0x03:             return 0x0003;
    case 0x05:             return 0x0005;
    case FEAT_FORCEFF:     return 0x8110;
    case FEAT_OLED:        return 0x8130;
    case FEAT_DAMPING:     return 0x8133;
    case FEAT_STRENGTH:    return 0x8136;
    case FEAT_PROFILE:     return 0x8137;
    case FEAT_ROTATION:    return 0x8138;
    case FEAT_TRUEFORCE:   return 0x8139;
    default:               return 0x0000;
    }
}

//
// Build HID++ Response
//
VOID
FwHidppBuildResponse(_In_ UCHAR FeatureIdx, _In_ UCHAR Fn, _In_ UCHAR ReportId, _Out_ PUCHAR OutBuf, _In_ ULONG OutLen)
{
    RtlZeroMemory(OutBuf, OutLen);
    OutBuf[0] = ReportId;
    OutBuf[1] = HIDPP_DEV_INDEX;
    OutBuf[2] = FeatureIdx;
    OutBuf[3] = Fn;

    USHORT val;
    switch (FwFeatureIndexToId(FeatureIdx)) {
    case 0x0000: // Root
        OutBuf[4] = FeatureIdx;
        OutBuf[5] = 0x01;
        OutBuf[6] = 0x00;
        break;
    case 0x0001: // Device Info
        OutBuf[4] = 0x01; // Device type
        OutBuf[5] = 0x00;
        OutBuf[6] = 0x00;
        break;
    case 0x8136: // Strength
        val = 0xFFFF; // 8.0 Nm default
        OutBuf[4] = (UCHAR)(val & 0xFF);
        OutBuf[5] = (UCHAR)(val >> 8);
        break;
    case 0x8138: // Rotation
        val = 1080; // 1080 deg default
        OutBuf[4] = (UCHAR)(val >> 8);
        OutBuf[5] = (UCHAR)(val & 0xFF);
        break;
    case 0x8139: // TrueForce
        val = 0;
        OutBuf[4] = (UCHAR)(val & 0xFF);
        OutBuf[5] = (UCHAR)(val >> 8);
        break;
    case 0x8137: // Profile
        OutBuf[4] = 0; // Desktop mode
        break;
    case 0x8133: // Damping
        OutBuf[4] = 0;
        break;
    case 0x8110: // Force Feedback
        OutBuf[4] = 0x01;
        OutBuf[5] = 0x00;
        OutBuf[6] = 0x00;
        break;
    default:
        break;
    }
}

//
// Process HID++ Command
//
VOID
FwHidppProcessCommand(_In_ UFXENDPOINT Endpoint, _In_ PUCHAR Buffer, _In_ ULONG Length)
{
    PFW_DEVICE_CONTEXT deviceContext = FwDeviceGetContext(UfxEndpointGetDevice(Endpoint));
    PFW_ENDPOINT_CONTEXT epContext = FwEndpointGetContext(Endpoint);

    if (Length < 4) {
        return;
    }

    UCHAR reportId = Buffer[0];
    UCHAR featIdx = Buffer[2];
    UCHAR fn = Buffer[3];

    // Store active report ID for response
    epContext->ActiveReportId = reportId;

    // Determine response length based on report ID
    USHORT respLen;
    switch (reportId) {
    case RID_HIDPP_SHORT:     respLen = 7; break;
    case RID_HIDPP_LONG:      respLen = 20; break;
    case RID_HIDPP_VERY_LONG: respLen = 64; break;
    default:                  respLen = 7; break;
    }
    if (respLen > Length) respLen = Length;

    // Allocate response buffer
    UCHAR* resp = ExAllocatePoolWithTag(NonPagedPoolNx, respLen, FW_TAG_TRANSFER);
    if (!resp) {
        return;
    }
    RtlZeroMemory(resp, respLen);

    // Build response
    FwHidppBuildResponse(featIdx, fn, reportId, resp, respLen);

    // Log command and response
    FwLogRaw(deviceContext, '>', Buffer, (USHORT)Length);
    FwLogRaw(deviceContext, '<', resp, respLen);

    // Store pending response
    if (deviceContext->HasPendingHidppResponse && deviceContext->PendingHidppResponse) {
        ExFreePoolWithTag(deviceContext->PendingHidppResponse, FW_TAG_TRANSFER);
    }
    deviceContext->PendingHidppResponse = resp;
    deviceContext->PendingHidppResponseLen = respLen;
    deviceContext->HasPendingHidppResponse = TRUE;
}

//
// Get Response for IN Transfer
//
NTSTATUS
FwHidppGetPendingResponse(_In_ PFW_DEVICE_CONTEXT DeviceContext, _Out_ PUCHAR* Response, _Out_ PULONG Length)
{
    if (DeviceContext->HasPendingHidppResponse && DeviceContext->PendingHidppResponse) {
        *Response = DeviceContext->PendingHidppResponse;
        *Length = DeviceContext->PendingHidppResponseLen;
        DeviceContext->HasPendingHidppResponse = FALSE;
        DeviceContext->PendingHidppResponse = NULL;
        DeviceContext->PendingHidppResponseLen = 0;
        return STATUS_SUCCESS;
    }
    return STATUS_NOT_FOUND;
}