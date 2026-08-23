/*++
FakeWheel RS50 — virtual Logitech RS50 USB Function Driver (UFX).
Implements a virtual USB device matching real RS50:
- Interface 0 (MI_00): Joystick HID (31-byte reports)
- Interface 1 (MI_01): HID++ with 3 collections (Report IDs 0x10, 0x11, 0x12)
- Interface 2 (MI_02): TrueForce Stream (page 0xFFFD, 250Hz, 64 bytes)
--*/

#ifdef _KERNEL_MODE
#include <ntddk.h>
#else
#include <windows.h>
#endif

#include <wdf.h>
#include <ufx/1.1/ufxclient.h>
#include <usbfnioctl.h>
#include <usbfnattach.h>

//
// USB Device Descriptor
//
#define FAKEWHEEL_USB_VID                 0x046D
#define FAKEWHEEL_USB_PID                 0xC276
#define FAKEWHEEL_USB_BCD_DEVICE          0x0100
#define FAKEWHEEL_USB_BCD_USB             0x0200
#define FAKEWHEEL_USB_MAX_PACKET_SIZE_0   64

//
// String Descriptor Indices
//
#define STR_ID_MANUFACTURER               1
#define STR_ID_PRODUCT                    2
#define STR_ID_SERIAL                     3
#define STR_ID_INTERFACE_0                4  // Joystick
#define STR_ID_INTERFACE_1                5  // HID++
#define STR_ID_INTERFACE_2                6  // TrueForce
#define STR_ID_CONFIG                     7

//
// Interface Numbers
//
#define INTERFACE_JOYSTICK                0
#define INTERFACE_HIDPP                   1
#define INTERFACE_TRUEFORCE               2

//
// Endpoint Addresses
//
#define EP_JOYSTICK_IN                    0x81   // Interface 0, IN
#define EP_HIDPP_IN                       0x82   // Interface 1, IN
#define EP_TRUEFORCE_OUT                  0x03   // Interface 2, OUT

//
// Report IDs for HID++ Interface (Interface 1)
//
#define RID_HIDPP_SHORT                   0x10   // 7 bytes
#define RID_HIDPP_LONG                    0x11   // 20 bytes
#define RID_HIDPP_VERY_LONG               0x12   // 64 bytes
#define HIDPP_DEV_INDEX                   0xFF

//
// HID++ Feature Indices (from real RS50)
//
#define FEAT_ROOT                         0x00   // Feature 0x0000
#define FEAT_DEVICE_INFO                  0x01   // Feature 0x0001
#define FEAT_FORCEFF                      0x10   // Feature 0x8110
#define FEAT_OLED                         0x12   // Feature 0x8130
#define FEAT_DAMPING                      0x14   // Feature 0x8133
#define FEAT_STRENGTH                     0x16   // Feature 0x8136
#define FEAT_PROFILE                      0x17   // Feature 0x8137
#define FEAT_ROTATION                     0x18   // Feature 0x8138
#define FEAT_TRUEFORCE                    0x19   // Feature 0x8139

//
// Wheel Settings (RS50 defaults)
//
#define STRENGTH_DEFAULT                  0xFFFF   // 8.0 Nm
#define ROTATION_DEFAULT                  1080     // 1080 deg
#define TRUEFORCE_DEFAULT                 0
#define PROFILE_DEFAULT                   5        // Onboard slot 5
#define DAMPING_DEFAULT                   0

//
// UFX Client Tags
//
#define FW_TAG_DEVICE                     'DevF'
#define FW_TAG_ENDPOINT                   'EpF'
#define FW_TAG_TRANSFER                   'XfrF'
#define FW_TAG_POOL                       'PolF'

//
// Forward declarations
//
DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD EvtDeviceAdd;
EVT_WDF_OBJECT_CONTEXT_CLEANUP EvtDriverContextCleanup;
EVT_UFX_DEVICE_HOST_CONNECT EvtDeviceHostConnect;
EVT_UFX_DEVICE_HOST_DISCONNECT EvtDeviceHostDisconnect;
EVT_UFX_DEVICE_ADDRESSED EvtDeviceAddressed;
EVT_UFX_DEVICE_ENDPOINT_ADD EvtDeviceEndpointAdd;
EVT_UFX_DEVICE_DEFAULT_ENDPOINT_ADD EvtDeviceDefaultEndpointAdd;
EVT_UFX_DEVICE_USB_STATE_CHANGE EvtDeviceUsbStateChange;
EVT_UFX_DEVICE_PORT_CHANGE EvtDevicePortChange;
EVT_UFX_DEVICE_PORT_DETECT EvtDevicePortDetect;
EVT_UFX_DEVICE_REMOTE_WAKEUP_SIGNAL EvtDeviceRemoteWakeupSignal;
EVT_UFX_DEVICE_TEST_MODE_SET EvtDeviceTestModeSet;
EVT_UFX_DEVICE_SUPER_SPEED_POWER_FEATURE EvtDeviceSuperSpeedPowerFeature;

//
// Context Structures
//
typedef struct _FW_DEVICE_CONTEXT {
    WDFDEVICE               WdfDevice;
    UFXDEVICE               UfxDevice;
    BOOLEAN                 IsConnected;
    USBFN_DEVICE_STATE      UsbState;
    USBFN_PORT_TYPE         PortType;
    BOOLEAN                 IsIdle;
    
    // Wheel settings
    USHORT                  StrengthRaw;
    USHORT                  RotationLocks;
    USHORT                  TrueForceRaw;
    UCHAR                   ProfileMode;
    UCHAR                   DampingRaw;
    
    // HID++ pending response
    UCHAR*                  PendingHidppResponse;
    ULONG                   PendingHidppResponseLen;
    BOOLEAN                 HasPendingHidppResponse;
    
    // Capture log
    HANDLE                  LogHandle;
} FW_DEVICE_CONTEXT, *PFW_DEVICE_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(FW_DEVICE_CONTEXT, FwDeviceGetContext);

typedef struct _FW_ENDPOINT_CONTEXT {
    UFXENDPOINT             UfxEndpoint;
    WDFQUEUE                TransferQueue;
    WDFQUEUE                CommandQueue;
    UCHAR                   EndpointAddress;
    UCHAR                   InterfaceNumber;
    BOOLEAN                 IsControlEndpoint;
    BOOLEAN                 IsInEndpoint;
    ULONG                   MaxPacketSize;
    
    // For HID++ endpoint
    UCHAR*                  PendingResponse;
    ULONG                   PendingResponseLen;
    BOOLEAN                 HasPendingResponse;
} FW_ENDPOINT_CONTEXT, *PFW_ENDPOINT_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(FW_ENDPOINT_CONTEXT, FwEndpointGetContext);

//
// Utility Functions
//
ULONGLONG FwGetTickMs(VOID);
VOID FwLogOpen(_In_ PFW_DEVICE_CONTEXT DeviceContext);
VOID FwLogWrite(_In_ PFW_DEVICE_CONTEXT DeviceContext, _In_ PCWSTR Line, _In_ ULONG LengthChars);
VOID FwLogRaw(_In_ PFW_DEVICE_CONTEXT DeviceContext, _In_ CHAR Dir, _In_ const UCHAR* Buf, _In_ ULONG Len);
NTSTATUS FwRequestCopyFromBuffer(_In_ WDFREQUEST Request, _In_ PVOID SourceBuffer, _In_ size_t NumBytesToCopyFrom);

//
// HID++ Protocol
//
VOID FwHidppProcessCommand(_In_ UFXENDPOINT Endpoint, _In_ PUCHAR Buffer, _In_ ULONG Length);
VOID FwHidppBuildResponse(_In_ UCHAR FeatureIdx, _In_ UCHAR Fn, _In_ UCHAR ReportId, _Out_ PUCHAR OutBuf, _In_ ULONG OutLen);
NTSTATUS FwHidppGetPendingResponse(_In_ PFW_DEVICE_CONTEXT DeviceContext, _Out_ PUCHAR* Response, _Out_ PULONG Length);

//
// Transfer Handling
//
NTSTATUS FwTransferInitialize(_In_ UFXENDPOINT Endpoint);
VOID FwTransferStart(_In_ UFXENDPOINT Endpoint);
VOID FwTransferReset(_In_ UFXENDPOINT Endpoint);

//
// Queue Callbacks
//
EVT_WDF_IO_QUEUE_IO_STOP EvtEndpointQueueIoStop;
EVT_WDF_IO_QUEUE_IO_CANCELED_ON_QUEUE EvtEndpointQueueIoCanceled;
EVT_WDF_IO_QUEUE_IO_INTERNAL_DEVICE_CONTROL EvtEndpointCommandQueue;
EVT_WDF_OBJECT_CONTEXT_CLEANUP FwEndpoint_EvtCleanupCallback;

//
// Descriptor declarations
//
extern USB_DEVICE_DESCRIPTOR G_UsbDeviceDescriptor;
extern USB_CONFIGURATION_DESCRIPTOR G_UsbConfigDescriptor;
extern USB_DEVICE_QUALIFIER_DESCRIPTOR G_UsbDeviceQualifier;
extern PUCHAR G_StringDescriptors[];
extern UCHAR G_JoystickReportDescriptor[];
extern USHORT G_JoystickReportDescriptorLength;
extern UCHAR G_HidppReportDescriptor[];
extern USHORT G_HidppReportDescriptorLength;
extern UCHAR G_TrueforceReportDescriptor[];
extern USHORT G_TrueforceReportDescriptorLength;
extern HID_DESCRIPTOR G_JoystickHidDescriptor;
extern HID_DESCRIPTOR G_HidppHidDescriptor;
extern HID_DESCRIPTOR G_TrueforceHidDescriptor;