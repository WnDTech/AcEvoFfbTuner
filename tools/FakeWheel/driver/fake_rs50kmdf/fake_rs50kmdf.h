#pragma once

//
// FakeWheel — virtual Logitech RS50 (VID_046D / PID_C276) HID device.
// KMDF VHF client driver. Dev-machine / VM test rig only.
//

#include <ntddk.h>
#include <wdf.h>
#include <vhf.h>

#define FAKEWHEEL_TAG 'FRS0'

#define FAKEWHEEL_VID        0x046D
#define FAKEWHEEL_PID        0xC276
#define FAKEWHEEL_VERSION    0x0100

#define RID_SHORT         0x10
#define RID_LONG          0x11
#define RID_VERYLONG      0x12

#define HIDPP_DEV_INDEX   0xFF
#define HIDPP_SWID        0x0A

#define FEAT_FORCEFF     0x10 // 0x8110 force feedback
#define FEAT_OLED        0x12 // 0x8130 dynamic display
#define FEAT_DAMPING     0x14 // 0x8133 dampening
#define FEAT_STRENGTH    0x16 // 0x8136 steering wheel (strength)
#define FEAT_PROFILE     0x17 // 0x8137 profile / mode
#define FEAT_ROTATION    0x18 // 0x8138 rotation range
#define FEAT_TRUEFORCE   0x19 // 0x8139 TrueForce

#define FEATURE_COUNT    0x1A

//
// HID++ report descriptor: one VHF device per collection (VHF rejects
// multiple top-level collections), page 0xFF00 (VHF rejects most other
// pages when report IDs are used), 8-bit USAGE items only (VHF rejects
// 16-bit USAGE). Report IDs and sizes mirror the real RS50's mi_01:
//   0x10 short 7B / 0x11 long 20B / 0x12 very-long 64B.
//
static const UCHAR FakeWheelShortDescriptor[] =
{
    0x06, 0x00, 0xFF,       // USAGE_PAGE (0xFF00)
    0x09, 0x01,             // USAGE (0x01)
    0xA1, 0x01,             // COLLECTION (Application)
        0x85, 0x10,         //   REPORT_ID (0x10)
        0x09, 0x01,         //   USAGE (0x01)
        0x15, 0x00,         //   LOGICAL_MINIMUM (0)
        0x26, 0xFF, 0x00,   //   LOGICAL_MAXIMUM (255)
        0x75, 0x08,         //   REPORT_SIZE (8)
        0x95, 0x06,         //   REPORT_COUNT (6)
        0x81, 0x02,         //   INPUT (Data,Var,Abs)
        0x95, 0x06,         //   REPORT_COUNT (6)
        0x91, 0x02,         //   OUTPUT (Data,Var,Abs)
    0xC0,                   // END_COLLECTION
};

static const UCHAR FakeWheelLongDescriptor[] =
{
    0x06, 0x00, 0xFF,       // USAGE_PAGE (0xFF00)
    0x09, 0x02,             // USAGE (0x02)
    0xA1, 0x01,             // COLLECTION (Application)
        0x85, 0x11,         //   REPORT_ID (0x11)
        0x09, 0x01,         //   USAGE (0x01)
        0x15, 0x00,         //   LOGICAL_MINIMUM (0)
        0x26, 0xFF, 0x00,   //   LOGICAL_MAXIMUM (255)
        0x75, 0x08,         //   REPORT_SIZE (8)
        0x95, 0x13,         //   REPORT_COUNT (19)
        0x81, 0x02,         //   INPUT (Data,Var,Abs)
        0x95, 0x13,         //   REPORT_COUNT (19)
        0x91, 0x02,         //   OUTPUT (Data,Var,Abs)
    0xC0,                   // END_COLLECTION
};

static const UCHAR FakeWheelVeryLongDescriptor[] =
{
    0x06, 0x00, 0xFF,       // USAGE_PAGE (0xFF00)
    0x09, 0x04,             // USAGE (0x04)
    0xA1, 0x01,             // COLLECTION (Application)
        0x85, 0x12,         //   REPORT_ID (0x12)
        0x09, 0x01,         //   USAGE (0x01)
        0x15, 0x00,         //   LOGICAL_MINIMUM (0)
        0x26, 0xFF, 0x00,   //   LOGICAL_MAXIMUM (255)
        0x75, 0x08,         //   REPORT_SIZE (8)
        0x95, 0x3F,         //   REPORT_COUNT (63)
        0x81, 0x02,         //   INPUT (Data,Var,Abs)
        0x95, 0x3F,         //   REPORT_COUNT (63)
        0x91, 0x02,         //   OUTPUT (Data,Var,Abs)
    0xC0,                   // END_COLLECTION
};

typedef struct _FAKEWHEEL_PEND
{
    UCHAR*  Buf;
    USHORT  Len;
    UCHAR   ReportId;
    UCHAR   Device;        // 0 = short, 1 = long, 2 = very-long
} FAKEWHEEL_PEND;

typedef struct _FAKEWHEEL_CONTEXT FAKEWHEEL_CONTEXT, *PFAKEWHEEL_CONTEXT;

typedef struct _VHF_CLIENT
{
    PFAKEWHEEL_CONTEXT Ctx;
    UCHAR              Index;
} VHF_CLIENT, *PVHF_CLIENT;

typedef struct _FAKEWHEEL_CONTEXT
{
    WDFDEVICE   Device;
    VHFHANDLE   VhfHandle[3];   // short / long / very-long VHF devices
    VHF_CLIENT  VhfClient[3];

    // Emulated wheel settings (RS50 encodings, verified on hardware)
    USHORT      StrengthRaw;    // Nm * 8191.875, little-endian (8.0 -> FF FF)
    USHORT      RotationLocks;  // degrees, big-endian (1080 -> 04 38)
    USHORT      TrueForceRaw;   // percent * 655.35, little-endian
    UCHAR       ProfileMode;    // 0 = desktop, 1-5 = onboard slot
    UCHAR       DampingRaw;     // percent

    // Last computed response in each broadcast form
    UCHAR       LastResp64[64];
    UCHAR       LastResp20[20];
    UCHAR       LastResp7[7];

    // Read report submission queue (VHF allows one in flight per device)
    FAKEWHEEL_PEND Pend[16];
    UCHAR           PendCount;
    BOOLEAN         SubmitInFlight[3];

    // Log file handle for the byte-for-byte capture
    HANDLE      LogHandle;
} FAKEWHEEL_CONTEXT, *PFAKEWHEEL_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(FAKEWHEEL_CONTEXT, FakeWheelGetContext)

//
// fake_rs50kmdf.c
//
DRIVER_INITIALIZE DriverEntry;

NTSTATUS
EvtDeviceAdd(
    _In_ WDFDRIVER Driver,
    _Inout_ PWDFDEVICE_INIT DeviceInit
    );