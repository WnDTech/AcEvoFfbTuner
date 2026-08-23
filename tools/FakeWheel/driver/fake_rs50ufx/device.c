/*++
FakeWheel RS50 — virtual Logitech RS50 USB Function Driver (UFX).
Main driver entry point and device initialization.
--*/

#include "fake_rs50ufx.h"

//
// Driver Entry Point
//
NTSTATUS
DriverEntry(
    _In_ PDRIVER_OBJECT DriverObject,
    _In_ PUNICODE_STRING RegistryPath
    )
{
    WDF_DRIVER_CONFIG config;
    NTSTATUS status;

    KdPrint(("FakeWheel RS50 UFX DriverEntry\n"));

    ExInitializeDriverRuntime(DrvRtPoolNxOptIn);

    WDF_DRIVER_CONFIG_INIT(&config, EvtDeviceAdd);
    config.EvtDriverUnload = EvtDriverContextCleanup;

    status = WdfDriverCreate(DriverObject, RegistryPath,
                             WDF_NO_OBJECT_ATTRIBUTES, &config, WDF_NO_HANDLE);
    if (!NT_SUCCESS(status)) {
        KdPrint(("Error: WdfDriverCreate failed 0x%x\n", status));
        return status;
    }

    return status;
}

VOID
EvtDriverContextCleanup(
    _In_ WDFOBJECT DriverObject
    )
{
    UNREFERENCED_PARAMETER(DriverObject);
    KdPrint(("FakeWheel RS50 UFX DriverUnload\n"));
}

NTSTATUS
EvtDeviceAdd(
    _In_ WDFDRIVER Driver,
    _Inout_ PWDFDEVICE_INIT DeviceInit
    )
{
    NTSTATUS status;
    WDF_OBJECT_ATTRIBUTES deviceAttributes;
    WDFDEVICE wdfDevice;
    PFW_DEVICE_CONTEXT deviceContext;
    UNREFERENCED_PARAMETER(Driver);

    KdPrint(("EvtDeviceAdd\n"));

    //
    // Initialize UFX
    //
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&deviceAttributes, FW_DEVICE_CONTEXT);

    status = UfxFdoInit(Driver, DeviceInit, &deviceAttributes);
    if (!NT_SUCCESS(status)) {
        KdPrint(("Error: UfxFdoInit failed 0x%x\n", status));
        return status;
    }

    //
    // Set PnP/Power callbacks
    //
    WDF_PNPPOWER_EVENT_CALLBACKS pnpCallbacks;
    WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&pnpCallbacks);
    pnpCallbacks.EvtDevicePrepareHardware = EvtDevicePrepareHardware;
    pnpCallbacks.EvtDeviceReleaseHardware = EvtDeviceReleaseHardware;
    pnpCallbacks.EvtDeviceD0Entry = EvtDeviceD0Entry;
    pnpCallbacks.EvtDeviceD0Exit = EvtDeviceD0Exit;
    WdfDeviceInitSetPnpPowerEventCallbacks(DeviceInit, &pnpCallbacks);

    status = WdfDeviceCreate(&DeviceInit, &deviceAttributes, &wdfDevice);
    if (!NT_SUCCESS(status)) {
        KdPrint(("Error: WdfDeviceCreate failed 0x%x\n", status));
        return status;
    }

    deviceContext = FwDeviceGetContext(wdfDevice);
    deviceContext->WdfDevice = wdfDevice;
    deviceContext->IsConnected = FALSE;
    deviceContext->UsbState = UsbfnDeviceStateDetached;
    deviceContext->PortType = UsbfnUnknownPort;
    deviceContext->IsIdle = TRUE;

    // Wheel settings (RS50 defaults)
    deviceContext->StrengthRaw = STRENGTH_DEFAULT;
    deviceContext->RotationLocks = ROTATION_DEFAULT;
    deviceContext->TrueForceRaw = TRUEFORCE_DEFAULT;
    deviceContext->ProfileMode = PROFILE_DEFAULT;
    deviceContext->DampingRaw = DAMPING_DEFAULT;
    deviceContext->HasPendingHidppResponse = FALSE;
    deviceContext->PendingHidppResponse = NULL;
    deviceContext->PendingHidppResponseLen = 0;

    //
    // Set alignment requirement
    //
    WdfDeviceSetAlignmentRequirement(wdfDevice, 0x1000);

    //
    // Create UFXDEVICE
    //
    status = FwDeviceCreate(wdfDevice);
    if (!NT_SUCCESS(status)) {
        KdPrint(("Error: FwDeviceCreate failed 0x%x\n", status));
        return status;
    }

    FwLogOpen(deviceContext);
    KdPrint(("FakeWheel RS50 UFX device created successfully\n"));

    return STATUS_SUCCESS;
}

NTSTATUS
FwDeviceCreate(_In_ WDFDEVICE WdfDevice)
{
    NTSTATUS status;
    PFW_DEVICE_CONTEXT deviceContext = FwDeviceGetContext(WdfDevice);
    UFXDEVICE ufxDevice;
    UFX_DEVICE_CALLBACKS ufxCallbacks;
    UFX_DEVICE_CAPABILITIES ufxCapabilities;
    WDF_OBJECT_ATTRIBUTES attributes;

    PAGED_CODE();

    //
    // Initialize UFX device capabilities
    //
    UFX_DEVICE_CAPABILITIES_INIT(&ufxCapabilities);
    ufxCapabilities.MaxSpeed = UsbSuperSpeed;
    ufxCapabilities.RemoteWakeSignalDelay = 10;

    // IN endpoint bitmap: EP0 + EP1 (0x81) + EP2 (0x82)
    ufxCapabilities.InEndpointBitmap = 0x0107;  // bits 0, 1, 2
    // OUT endpoint bitmap: EP0 + EP3 (0x03)
    ufxCapabilities.OutEndpointBitmap = 0x0009; // bits 0, 3

    //
    // Set UFX callbacks
    //
    UFX_DEVICE_CALLBACKS_INIT(&ufxCallbacks);
    ufxCallbacks.EvtDeviceHostConnect = EvtDeviceHostConnect;
    ufxCallbacks.EvtDeviceHostDisconnect = EvtDeviceHostDisconnect;
    ufxCallbacks.EvtDeviceAddressed = EvtDeviceAddressed;
    ufxCallbacks.EvtDeviceEndpointAdd = EvtDeviceEndpointAdd;
    ufxCallbacks.EvtDeviceDefaultEndpointAdd = EvtDeviceDefaultEndpointAdd;
    ufxCallbacks.EvtDeviceUsbStateChange = EvtDeviceUsbStateChange;
    ufxCallbacks.EvtDevicePortChange = EvtDevicePortChange;
    ufxCallbacks.EvtDevicePortDetect = EvtDevicePortDetect;
    ufxCallbacks.EvtDeviceRemoteWakeupSignal = EvtDeviceRemoteWakeupSignal;
    ufxCallbacks.EvtDeviceTestModeSet = EvtDeviceTestModeSet;
    ufxCallbacks.EvtDeviceSuperSpeedPowerFeature = EvtDeviceSuperSpeedPowerFeature;

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, UFXDEVICE_CONTEXT);
    attributes.EvtCleanupCallback = UfxDevice_EvtCleanupCallback;

    status = UfxDeviceCreate(WdfDevice, &ufxCallbacks, &ufxCapabilities, &attributes, &ufxDevice);
    if (!NT_SUCCESS(status)) {
        KdPrint(("Error: UfxDeviceCreate failed 0x%x\n", status));
        return status;
    }

    deviceContext->UfxDevice = ufxDevice;

    //
    // Initialize UFX device context
    //
    PUFXDEVICE_CONTEXT ufxDeviceContext = UfxDeviceGetContext(ufxDevice);
    ufxDeviceContext->FdoWdfDevice = WdfDevice;
    ufxDeviceContext->UsbState = UsbfnDeviceStateDetached;
    ufxDeviceContext->UsbPort = UsbfnUnknownPort;
    ufxDeviceContext->IsIdle = TRUE;

    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
    attributes.ParentObject = ufxDevice;
    status = WdfCollectionCreate(&attributes, &ufxDeviceContext->Endpoints);
    if (!NT_SUCCESS(status)) {
        KdPrint(("Error: WdfCollectionCreate failed 0x%x\n", status));
        return status;
    }

    //
    // Initialize device endpoints
    //
    status = FwDeviceInitializeEndpoints(ufxDevice);
    if (!NT_SUCCESS(status)) {
        KdPrint(("Error: FwDeviceInitializeEndpoints failed 0x%x\n", status));
        return status;
    }

    return STATUS_SUCCESS;
}

NTSTATUS
FwDeviceInitializeEndpoints(_In_ UFXDEVICE UfxDevice)
{
    NTSTATUS status;

    //
    // Add default control endpoint (EP0)
    //
    USB_ENDPOINT_DESCRIPTOR ep0Desc = {
        sizeof(USB_ENDPOINT_DESCRIPTOR),
        USB_ENDPOINT_DESCRIPTOR_TYPE,
        0x00,
        USB_ENDPOINT_TYPE_CONTROL,
        FAKEWHEEL_USB_MAX_PACKET_SIZE_0, 0,
        0
    };

    UFXENDPOINT_INIT ep0Init;
    UFX_ENDPOINT_INIT_INIT(&ep0Init);
    status = FwEndpointAdd(UfxDevice, &ep0Desc, &ep0Init);
    if (!NT_SUCCESS(status)) {
        KdPrint(("Error: EP0 add failed 0x%x\n", status));
        return status;
    }

    //
    // Add Joystick endpoint (EP 0x81 IN)
    //
    USB_ENDPOINT_DESCRIPTOR epJoystick = {
        sizeof(USB_ENDPOINT_DESCRIPTOR),
        USB_ENDPOINT_DESCRIPTOR_TYPE,
        EP_JOYSTICK_IN,
        USB_ENDPOINT_TYPE_INTERRUPT,
        30, 0,
        1
    };

    UFXENDPOINT_INIT epJoyInit;
    UFX_ENDPOINT_INIT_INIT(&epJoyInit);
    status = FwEndpointAdd(UfxDevice, &epJoystick, &epJoyInit);
    if (!NT_SUCCESS(status)) {
        KdPrint(("Error: Joystick EP add failed 0x%x\n", status));
        return status;
    }

    //
    // Add HID++ endpoint (EP 0x82 IN)
    //
    USB_ENDPOINT_DESCRIPTOR epHidpp = {
        sizeof(USB_ENDPOINT_DESCRIPTOR),
        USB_ENDPOINT_DESCRIPTOR_TYPE,
        EP_HIDPP_IN,
        USB_ENDPOINT_TYPE_INTERRUPT,
        64, 0,
        1
    };

    UFXENDPOINT_INIT epHidppInit;
    UFX_ENDPOINT_INIT_INIT(&epHidppInit);
    status = FwEndpointAdd(UfxDevice, &epHidpp, &epHidppInit);
    if (!NT_SUCCESS(status)) {
        KdPrint(("Error: HID++ EP add failed 0x%x\n", status));
        return status;
    }

    //
    // Add Force Feedback endpoint (EP 0x03 OUT)
    //
    USB_ENDPOINT_DESCRIPTOR epFfb = {
        sizeof(USB_ENDPOINT_DESCRIPTOR),
        USB_ENDPOINT_DESCRIPTOR_TYPE,
        EP_FFB_OUT,
        USB_ENDPOINT_TYPE_INTERRUPT,
        64, 0,
        1
    };

    UFXENDPOINT_INIT epFfbInit;
    UFX_ENDPOINT_INIT_INIT(&epFfbInit);
    status = FwEndpointAdd(UfxDevice, &epFfb, &epFfbInit);
    if (!NT_SUCCESS(status)) {
        KdPrint(("Error: FFB EP add failed 0x%x\n", status));
        return status;
    }

    return STATUS_SUCCESS;
}

NTSTATUS
FwEndpointAdd(_In_ UFXDEVICE UfxDevice, _In_ PUSB_ENDPOINT_DESCRIPTOR EndpointDescriptor, _Inout_ PUFXENDPOINT_INIT EndpointInit)
{
    NTSTATUS status;
    UFXENDPOINT endpoint;
    UFX_ENDPOINT_CALLBACKS callbacks;
    PFW_DEVICE_CONTEXT deviceContext = FwDeviceGetContext(UfxDeviceGetContext(UfxDevice)->FdoWdfDevice);
    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_IO_QUEUE_CONFIG queueConfig;
    WDF_OBJECT_ATTRIBUTES queueAttributes;
    WDFQUEUE transferQueue, commandQueue;
    PFW_ENDPOINT_CONTEXT epContext;
    PENDPOINT_QUEUE_CONTEXT queueContext;
    WDF_OBJECT_ATTRIBUTES transferQueueAttributes, commandQueueAttributes;

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, FW_ENDPOINT_CONTEXT);
    attributes.EvtCleanupCallback = FwEndpoint_EvtCleanupCallback;

    UFX_ENDPOINT_CALLBACKS_INIT(&callbacks);

    //
    // Create transfer queue (manual dispatch for IN endpoints)
    //
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&transferQueueAttributes, ENDPOINT_QUEUE_CONTEXT);
    transferQueueAttributes.ExecutionLevel = WdfExecutionLevelPassive;

    WDF_IO_QUEUE_CONFIG_INIT(&queueConfig, WdfIoQueueDispatchManual);
    queueConfig.AllowZeroLengthRequests = TRUE;
    queueConfig.EvtIoStop = EvtEndpointQueueIoStop;

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&commandQueueAttributes, ENDPOINT_QUEUE_CONTEXT);
    commandQueueAttributes.ExecutionLevel = WdfExecutionLevelPassive;

    WDF_IO_QUEUE_CONFIG_INIT(&queueConfig, WdfIoQueueDispatchSequential);
    queueConfig.EvtIoInternalDeviceControl = EvtEndpointCommandQueue;

    status = UfxEndpointCreate(UfxDevice, EndpointInit, &attributes,
                               &queueConfig, &transferQueueAttributes,
                               &queueConfig, &commandQueueAttributes,
                               &endpoint);
    if (!NT_SUCCESS(status)) {
        KdPrint(("Error: UfxEndpointCreate failed 0x%x\n", status));
        return status;
    }

    //
    // Store endpoint in device context collection
    //
    PUFXDEVICE_CONTEXT ufxDeviceContext = UfxDeviceGetContext(UfxDevice);
    status = WdfCollectionAdd(ufxDeviceContext->Endpoints, endpoint);
    if (!NT_SUCCESS(status)) {
        KdPrint(("Error: WdfCollectionAdd failed 0x%x\n", status));
        return status;
    }

    //
    // Initialize endpoint context
    //
    epContext = FwEndpointGetContext(endpoint);
    epContext->UfxEndpoint = endpoint;
    epContext->WdfDevice = UfxDeviceGetContext(UfxDevice)->FdoWdfDevice;
    epContext->EndpointAddress = EndpointDescriptor->bEndpointAddress;
    epContext->InterfaceNumber = (EndpointDescriptor->bEndpointAddress & 0x80) ? 
                                  (EndpointDescriptor->bEndpointAddress & 0x0F) : 0;
    epContext->IsControlEndpoint = (EndpointDescriptor->bEndpointAddress == 0x00);
    epContext->IsInEndpoint = (EndpointDescriptor->bEndpointAddress & 0x80) != 0;
    epContext->MaxPacketSize = EndpointDescriptor->wMaxPacketSize;
    epContext->HasPendingResponse = FALSE;
    epContext->PendingResponse = NULL;
    epContext->PendingResponseLen = 0;

    epContext->TransferQueue = UfxEndpointGetTransferQueue(endpoint);
    epContext->CommandQueue = UfxEndpointGetCommandQueue(endpoint);

    queueContext = EndpointQueueGetContext(epContext->TransferQueue);
    queueContext->Endpoint = endpoint;

    queueContext = EndpointQueueGetContext(epContext->CommandQueue);
    queueContext->Endpoint = endpoint;

    //
    // Initialize transfers for this endpoint
    //
    status = FwTransferInitialize(endpoint);
    if (!NT_SUCCESS(status)) {
        KdPrint(("Error: FwTransferInitialize failed 0x%x\n", status));
        return status;
    }

    //
    // Configure hardware for this endpoint
    //
    FwConfigureEndpoint(endpoint, epContext->IsControlEndpoint);

    return STATUS_SUCCESS;
}

NTSTATUS
FwConfigureEndpoint(_In_ UFXENDPOINT Endpoint, _In_ BOOLEAN IsControlEndpoint)
{
    PFW_ENDPOINT_CONTEXT epContext = FwEndpointGetContext(Endpoint);
    ULONG address = epContext->EndpointAddress & 0x0F;

    if (IsControlEndpoint || address == 0) {
        // Control endpoint - configure both IN and OUT
    } else if (epContext->IsInEndpoint) {
        // IN endpoint (Interrupt IN)
    } else {
        // OUT endpoint (Interrupt OUT)
    }

    if (address != 0) {
        FwTransferStart(Endpoint);
    }

    return STATUS_SUCCESS;
}

//
// UFX Device Callbacks
//

VOID
EvtDeviceHostConnect(_In_ UFXDEVICE UfxDevice)
{
    PFW_DEVICE_CONTEXT deviceContext = FwDeviceGetContext(UfxDeviceGetContext(UfxDevice)->FdoWdfDevice);
    deviceContext->IsConnected = TRUE;
    UfxDeviceSetRunStop(UfxDevice, TRUE);
}

VOID
EvtDeviceHostDisconnect(_In_ UFXDEVICE UfxDevice)
{
    PFW_DEVICE_CONTEXT deviceContext = FwDeviceGetContext(UfxDeviceGetContext(UfxDevice)->FdoWdfDevice);
    deviceContext->IsConnected = FALSE;
    FwDevice_Reset(UfxDevice);
    UfxDeviceSetRunStop(UfxDevice, FALSE);
}

VOID
EvtDeviceAddressed(_In_ UFXDEVICE UfxDevice, _In_ USHORT DeviceAddress)
{
    // TODO: Set device address on hardware
    UfxDeviceEventComplete(UfxDevice, STATUS_SUCCESS);
}

NTSTATUS
EvtDeviceEndpointAdd(_In_ UFXDEVICE UfxDevice, _In_ const PUSB_ENDPOINT_DESCRIPTOR EndpointDescriptor, _Inout_ PUFXENDPOINT_INIT EndpointInit)
{
    return FwEndpointAdd(UfxDevice, EndpointDescriptor, EndpointInit);
}

VOID
EvtDeviceDefaultEndpointAdd(_In_ UFXDEVICE UfxDevice, _In_ USHORT MaxPacketSize, _Inout_ PUFXENDPOINT_INIT EndpointInit)
{
    USB_ENDPOINT_DESCRIPTOR descriptor = {
        sizeof(USB_ENDPOINT_DESCRIPTOR),
        USB_ENDPOINT_DESCRIPTOR_TYPE,
        0x00,
        USB_ENDPOINT_TYPE_CONTROL,
        MaxPacketSize, 0,
        0
    };

    NTSTATUS status = FwEndpointAdd(UfxDevice, &descriptor, EndpointInit);
    UfxDeviceEventComplete(UfxDevice, status);
}

VOID
EvtDeviceUsbStateChange(_In_ UFXDEVICE UfxDevice, _In_ USBFN_DEVICE_STATE NewState)
{
    PFW_DEVICE_CONTEXT deviceContext = FwDeviceGetContext(UfxDeviceGetContext(UfxDevice)->FdoWdfDevice);
    PUFXDEVICE_CONTEXT ufxDeviceContext = UfxDeviceGetContext(UfxDevice);
    USBFN_DEVICE_STATE oldState = ufxDeviceContext->UsbState;

    ufxDeviceContext->UsbState = NewState;

    if (NewState == UsbfnDeviceStateConfigured && oldState != UsbfnDeviceStateSuspended) {
        // Configure all non-default endpoints
        for (ULONG i = 1; i < WdfCollectionGetCount(ufxDeviceContext->Endpoints); i++) {
            UFXENDPOINT endpoint = (UFXENDPOINT)WdfCollectionGetItem(ufxDeviceContext->Endpoints, i);
            FwConfigureEndpoint(endpoint, FALSE);
        }
    }

    if (NewState == UsbfnDeviceStateDetached) {
        deviceContext->IsConnected = FALSE;
    }

    UfxDeviceEventComplete(UfxDevice, STATUS_SUCCESS);
}

VOID
EvtDevicePortChange(_In_ UFXDEVICE UfxDevice, _In_ USBFN_PORT_TYPE NewPort)
{
    PUFXDEVICE_CONTEXT ufxDeviceContext = UfxDeviceGetContext(UfxDevice);
    ufxDeviceContext->UsbPort = NewPort;
    UfxDeviceEventComplete(UfxDevice, STATUS_SUCCESS);
}

VOID
EvtDevicePortDetect(_In_ UFXDEVICE UfxDevice)
{
    UfxDevicePortDetectComplete(UfxDevice, UsbfnStandardDownstreamPort);
}

VOID
EvtDeviceRemoteWakeupSignal(_In_ UFXDEVICE UfxDevice)
{
    PUFXDEVICE_CONTEXT ufxDeviceContext = UfxDeviceGetContext(UfxDevice);
    NTSTATUS status = WdfDeviceStopIdle(ufxDeviceContext->FdoWdfDevice, TRUE);
    if (NT_SUCCESS(status)) {
        WdfDeviceResumeIdle(ufxDeviceContext->FdoWdfDevice);
    }
    UfxDeviceEventComplete(UfxDevice, status);
}

VOID
EvtDeviceTestModeSet(_In_ UFXDEVICE UfxDevice, _In_ ULONG TestMode)
{
    UNREFERENCED_PARAMETER(TestMode);
    UfxDeviceEventComplete(UfxDevice, STATUS_SUCCESS);
}

VOID
EvtDeviceSuperSpeedPowerFeature(_In_ UFXDEVICE Device, _In_ USHORT Feature, _In_ BOOLEAN Set)
{
    UNREFERENCED_PARAMETER(Device);
    UNREFERENCED_PARAMETER(Feature);
    UNREFERENCED_PARAMETER(Set);
    UfxDeviceEventComplete(Device, STATUS_SUCCESS);
}

VOID
FwDevice_Reset(_In_ UFXDEVICE UfxDevice)
{
    PUFXDEVICE_CONTEXT ufxDeviceContext = UfxDeviceGetContext(UfxDevice);

    // Reset all endpoints
    for (ULONG i = 0; i < WdfCollectionGetCount(ufxDeviceContext->Endpoints); i++) {
        UFXENDPOINT endpoint = (UFXENDPOINT)WdfCollectionGetItem(ufxDeviceContext->Endpoints, i);
        FwTransferReset(endpoint);
    }
}

//
// Hardware Power Management
//

VOID
UfxDeviceSetRunStop(_In_ UFXDEVICE UfxDevice, _In_ BOOLEAN Set)
{
    BOOLEAN eventComplete = TRUE;

    if (Set) {
        // TODO: Set run state on hardware (pull-up D+ for FS, etc.)
    } else {
        // TODO: Clear run state on hardware
    }

    if (eventComplete) {
        UfxDeviceEventComplete(UfxDevice, STATUS_SUCCESS);
    }
}

//
// Cleanup Callbacks
//

VOID
UfxDevice_EvtCleanupCallback(_In_ WDFOBJECT UfxDevice)
{
    PUFXDEVICE_CONTEXT deviceContext = UfxDeviceGetContext(UfxDevice);
}

VOID
FwEndpoint_EvtCleanupCallback(_In_ WDFOBJECT Object)
{
    UFXENDPOINT endpoint = (UFXENDPOINT)Object;
    PFW_ENDPOINT_CONTEXT epContext = FwEndpointGetContext(endpoint);
    PUFXDEVICE_CONTEXT deviceContext = UfxDeviceGetContext(epContext->UfxEndpoint);

    // Remove from collection
    WdfCollectionRemove(deviceContext->Endpoints, endpoint);

    // Cleanup transfers
    FwTransferDestroy(endpoint);
}

//
// PnP/Power Callbacks
//

NTSTATUS
EvtDevicePrepareHardware(_In_ WDFDEVICE Device, _In_ WDFCMRESLIST ResourcesRaw, _In_ WDFCMRESLIST ResourcesTranslated)
{
    UNREFERENCED_PARAMETER(ResourcesRaw);
    UNREFERENCED_PARAMETER(ResourcesTranslated);
    // For virtual device, no physical hardware
    return STATUS_SUCCESS;
}

NTSTATUS
EvtDeviceReleaseHardware(_In_ WDFDEVICE Device, _In_ WDFCMRESLIST ResourcesTranslated)
{
    UNREFERENCED_PARAMETER(ResourcesTranslated);
    // TODO: Unmap registers, cleanup
    return STATUS_SUCCESS;
}

NTSTATUS
EvtDeviceD0Entry(_In_ WDFDEVICE Device, _In_ WDF_POWER_DEVICE_STATE PreviousState)
{
    PFW_DEVICE_CONTEXT deviceContext = FwDeviceGetContext(Device);

    if (PreviousState > WdfPowerDeviceD1) {
        // Soft reset
        FwDevice_Reset(deviceContext->UfxDevice);

        // Notify UFX hardware is ready
        UfxDeviceNotifyHardwareReady(deviceContext->UfxDevice);
    }

    if (PreviousState == WdfPowerDeviceD3Final) {
        UfxDeviceNotifyHardwareReady(deviceContext->UfxDevice);
    }

    return STATUS_SUCCESS;
}

NTSTATUS
EvtDeviceD0Exit(_In_ WDFDEVICE Device, _In_ WDF_POWER_DEVICE_STATE TargetState)
{
    PFW_DEVICE_CONTEXT deviceContext = FwDeviceGetContext(Device);
    PUFXDEVICE_CONTEXT ufxDeviceContext = UfxDeviceGetContext(deviceContext->UfxDevice);

    if (TargetState == WdfPowerDeviceD3Final) {
        if (deviceContext->IsConnected) {
            // Simulate detach
            UfxDeviceNotifyDetach(deviceContext->UfxDevice);
            deviceContext->IsConnected = FALSE;
        }
    }

    return STATUS_SUCCESS;
}