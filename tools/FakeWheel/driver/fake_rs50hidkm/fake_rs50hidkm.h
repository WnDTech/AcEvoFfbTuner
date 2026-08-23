/* FakeWheel RS50 3-device mshidkmdf */
#pragma once
#include <ntddk.h>
#include <wdf.h>
#include <hidport.h>
#include "common.h"
typedef struct _DEVICE_CONTEXT { WDFDEVICE Device; WDFQUEUE DefaultQueue; WDFQUEUE ManualQueue; HID_DEVICE_ATTRIBUTES HidDeviceAttributes; HID_DESCRIPTOR HidDescriptor; PUCHAR ReportDescriptor; } DEVICE_CONTEXT, *PDEVICE_CONTEXT;
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(DEVICE_CONTEXT, GetDeviceContext)
typedef struct _QUEUE_CONTEXT { WDFQUEUE Queue; PDEVICE_CONTEXT DeviceContext; } QUEUE_CONTEXT, *PQUEUE_CONTEXT;
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(QUEUE_CONTEXT, GetQueueContext)
typedef struct _MANUAL_QUEUE_CONTEXT { WDFQUEUE Queue; PDEVICE_CONTEXT DeviceContext; WDFTIMER Timer; } MANUAL_QUEUE_CONTEXT, *PMANUAL_QUEUE_CONTEXT;
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(MANUAL_QUEUE_CONTEXT, GetManualQueueContext)
DRIVER_INITIALIZE DriverEntry; EVT_WDF_DRIVER_DEVICE_ADD EvtDeviceAdd; EVT_WDF_OBJECT_CONTEXT_CLEANUP EvtDriverContextCleanup;
EVT_WDF_IO_QUEUE_IO_INTERNAL_DEVICE_CONTROL EvtIoDeviceControl;
NTSTATUS QueueCreate(_In_ WDFDEVICE,_Out_ WDFQUEUE*); NTSTATUS ManualQueueCreate(_In_ WDFDEVICE,_Out_ WDFQUEUE*);
NTSTATUS RequestCopyFromBuffer(_In_ WDFREQUEST,_In_ PVOID,_In_ size_t);
NTSTATUS RequestGetHidXferPacket_ToReadFromDevice(_In_ WDFREQUEST,_Out_ HID_XFER_PACKET*);
NTSTATUS RequestGetHidXferPacket_ToWriteToDevice(_In_ WDFREQUEST,_Out_ HID_XFER_PACKET*);
extern UCHAR G_ShortReportDescriptor[]; extern UCHAR G_LongReportDescriptor[]; extern UCHAR G_VeryLongReportDescriptor[];
extern HID_DESCRIPTOR G_ShortHidDescriptor; extern HID_DESCRIPTOR G_LongHidDescriptor; extern HID_DESCRIPTOR G_VeryLongHidDescriptor;
