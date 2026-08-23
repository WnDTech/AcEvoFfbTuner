/*++

FakeWheel RS50 — virtual Logitech RS50 (VID_046D / PID_C276) HID minidriver.
UMDF2 HID minidriver based on Microsoft's vhidmini2 sample, serving the RS50's
three HID++ report-id collections on usage page 0xFF43.

--*/

#pragma once

#ifdef _KERNEL_MODE
#include <ntddk.h>
#else
#include <windows.h>
#endif

#include <wdf.h>

#include <hidport.h>  // located in $(DDK_INC_PATH)/wdm

#include "common.h"

typedef UCHAR HID_REPORT_DESCRIPTOR, *PHID_REPORT_DESCRIPTOR;

DRIVER_INITIALIZE                   DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD           EvtDeviceAdd;
EVT_WDF_TIMER                       EvtTimerFunc;

//
// HID++ report ids of the real RS50 (mi_01, page 0xFF43)
//
#define RID_SHORT         0x10
#define RID_LONG          0x11
#define RID_VERYLONG      0x12

#define HIDPP_DEV_INDEX   0xFF
#define HIDPP_SWID        0x0A

//
// RS50 feature indices (from the tester's successful connect)
//
#define FEAT_FORCEFF     0x10 // 0x8110 force feedback
#define FEAT_OLED        0x12 // 0x8130 dynamic display
#define FEAT_DAMPING     0x14 // 0x8133 dampening
#define FEAT_STRENGTH    0x16 // 0x8136 steering wheel (strength)
#define FEAT_PROFILE     0x17 // 0x8137 profile / mode
#define FEAT_ROTATION    0x18 // 0x8138 rotation range
#define FEAT_TRUEFORCE   0x19 // 0x8139 TrueForce

#define FEATURE_COUNT    0x1A

#define FAKEWHEEL_VID        0x046D
#define FAKEWHEEL_PID        0xC276
#define FAKEWHEEL_VERSION    0x0100

typedef struct _DEVICE_CONTEXT
{
    WDFDEVICE               Device;
    WDFQUEUE                DefaultQueue;
    WDFQUEUE                ManualQueue;
    HID_DEVICE_ATTRIBUTES   HidDeviceAttributes;
    BYTE                    DeviceData;
    HID_DESCRIPTOR          HidDescriptor;
    PHID_REPORT_DESCRIPTOR  ReportDescriptor;
    BOOLEAN                 ReadReportDescFromRegistry;

    // Emulated wheel settings (RS50 encodings, verified on hardware)
    USHORT                  StrengthRaw;    // Nm * 8191.875 LE (8.0 -> FF FF)
    USHORT                  RotationLocks;  // degrees BE (1080 -> 04 38)
    USHORT                  TrueForceRaw;   // % * 655.35 LE
    UCHAR                   ProfileMode;    // 0 = desktop, 1-5 = onboard slot
    UCHAR                   DampingRaw;     // percent

    // Last computed HID++ response in each broadcast form
    UCHAR                   LastResp64[64];
    UCHAR                   LastResp20[20];
    UCHAR                   LastResp7[7];

    // Log file handle for the byte-for-byte capture
    HANDLE                  LogHandle;

} DEVICE_CONTEXT, *PDEVICE_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(DEVICE_CONTEXT, GetDeviceContext);

typedef struct _QUEUE_CONTEXT
{
    WDFQUEUE                Queue;
    PDEVICE_CONTEXT         DeviceContext;
    UCHAR                   OutputReport;

} QUEUE_CONTEXT, *PQUEUE_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(QUEUE_CONTEXT, GetQueueContext);

NTSTATUS
QueueCreate(
    _In_  WDFDEVICE         Device,
    _Out_ WDFQUEUE          *Queue
    );

typedef struct _MANUAL_QUEUE_CONTEXT
{
    WDFQUEUE                Queue;
    PDEVICE_CONTEXT         DeviceContext;
    WDFTIMER                Timer;

} MANUAL_QUEUE_CONTEXT, *PMANUAL_QUEUE_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(MANUAL_QUEUE_CONTEXT, GetManualQueueContext);

NTSTATUS
ManualQueueCreate(
    _In_  WDFDEVICE         Device,
    _Out_ WDFQUEUE          *Queue
    );

NTSTATUS
ReadReport(
    _In_  PQUEUE_CONTEXT    QueueContext,
    _In_  WDFREQUEST        Request,
    _Always_(_Out_)
          BOOLEAN*          CompleteRequest
    );

NTSTATUS
WriteReport(
    _In_  PQUEUE_CONTEXT    QueueContext,
    _In_  WDFREQUEST        Request
    );

NTSTATUS
GetFeature(
    _In_  PQUEUE_CONTEXT    QueueContext,
    _In_  WDFREQUEST        Request
    );

NTSTATUS
SetFeature(
    _In_  PQUEUE_CONTEXT    QueueContext,
    _In_  WDFREQUEST        Request
    );

NTSTATUS
GetInputReport(
    _In_  PQUEUE_CONTEXT    QueueContext,
    _In_  WDFREQUEST        Request
    );

NTSTATUS
SetOutputReport(
    _In_  PQUEUE_CONTEXT    QueueContext,
    _In_  WDFREQUEST        Request
    );

NTSTATUS
GetString(
    _In_  WDFREQUEST        Request
    );

NTSTATUS
GetIndexedString(
    _In_  WDFREQUEST        Request
    );

NTSTATUS
GetStringId(
    _In_  WDFREQUEST        Request,
    _Out_ ULONG            *StringId,
    _Out_ ULONG            *LanguageId
    );

NTSTATUS
RequestCopyFromBuffer(
    _In_  WDFREQUEST        Request,
    _In_  PVOID             SourceBuffer,
    _When_(NumBytesToCopyFrom == 0, __drv_reportError(NumBytesToCopyFrom cannot be zero))
    _In_  size_t            NumBytesToCopyFrom
    );

NTSTATUS
RequestGetHidXferPacket_ToReadFromDevice(
    _In_  WDFREQUEST        Request,
    _Out_ HID_XFER_PACKET  *Packet
    );

NTSTATUS
RequestGetHidXferPacket_ToWriteToDevice(
    _In_  WDFREQUEST        Request,
    _Out_ HID_XFER_PACKET  *Packet
    );

NTSTATUS
CheckRegistryForDescriptor(
    _In_ WDFDEVICE Device
    );

NTSTATUS
ReadDescriptorFromRegistry(
    _In_ WDFDEVICE Device
    );

VOID
HandleHidppRequest(
    _In_ PDEVICE_CONTEXT  DeviceContext,
    _In_ const UCHAR*     Buffer,
    _In_ USHORT           Length
    );

//
// Misc definitions
//
#define CONTROL_FEATURE_REPORT_ID   0x01