/*++
FakeWheel RS50 — virtual Logitech RS50 USB Function Driver (UFX).
Transfer handling for endpoints.
--*/

#include "fake_rs50ufx.h"

//
// Transfer Context
//
typedef struct _FW_TRANSFER_CONTEXT {
    WDFREQUEST              Request;
    UFXENDPOINT             Endpoint;
    BOOLEAN                 IsInTransfer;
    PUCHAR                  Buffer;
    ULONG                   BufferLength;
    ULONG                   BytesTransferred;
} FW_TRANSFER_CONTEXT, *PFW_TRANSFER_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(FW_TRANSFER_CONTEXT, FwTransferGetContext);

//
// Transfer Initialize
//
NTSTATUS
FwTransferInitialize(_In_ UFXENDPOINT Endpoint)
{
    NTSTATUS status;
    WDF_IO_QUEUE_CONFIG queueConfig;
    WDF_OBJECT_ATTRIBUTES queueAttributes;
    WDFQUEUE queue;
    PUFXDEVICE_CONTEXT deviceContext = UfxDeviceGetContext(UfxEndpointGetDevice(Endpoint));
    PFW_ENDPOINT_CONTEXT epContext = FwEndpointGetContext(Endpoint);
    WDF_OBJECT_ATTRIBUTES attributes;

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, FW_TRANSFER_CONTEXT);
    attributes.ParentObject = Endpoint;

    WDF_IO_QUEUE_CONFIG_INIT(&queueConfig, WdfIoQueueDispatchManual);
    queueConfig.AllowZeroLengthRequests = TRUE;
    queueConfig.EvtIoStop = EvtEndpointQueueIoStop;

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&queueAttributes, ENDPOINT_QUEUE_CONTEXT);
    queueAttributes.ParentObject = Endpoint;

    status = WdfIoQueueCreate(deviceContext->FdoWdfDevice, &queueConfig, &queueAttributes, &queue);
    if (!NT_SUCCESS(status)) {
        KdPrint(("Error: Transfer queue create failed 0x%x\n", status));
        return status;
    }

    PENDPOINT_QUEUE_CONTEXT queueContext = EndpointQueueGetContext(queue);
    queueContext->Endpoint = Endpoint;

    return STATUS_SUCCESS;
}

VOID
FwTransferDestroy(_In_ UFXENDPOINT Endpoint)
{
    // TODO: Cleanup pending transfers
}

VOID
FwTransferReset(_In_ UFXENDPOINT Endpoint)
{
    PFW_ENDPOINT_CONTEXT epContext = FwEndpointGetContext(Endpoint);
    
    // Cancel all pending transfers
    WdfIoQueuePurgeSynchronously(epContext->TransferQueue);
    
    if (epContext->HasPendingResponse && epContext->PendingResponse) {
        ExFreePoolWithTag(epContext->PendingResponse, FW_TAG_TRANSFER);
        epContext->PendingResponse = NULL;
        epContext->PendingResponseLen = 0;
        epContext->HasPendingResponse = FALSE;
    }
}

VOID
FwTransferStart(_In_ UFXENDPOINT Endpoint)
{
    PFW_ENDPOINT_CONTEXT epContext = FwEndpointGetContext(Endpoint);
    WdfIoQueueStart(epContext->TransferQueue);
}

//
// Queue Callbacks
//

VOID
EvtEndpointQueueIoStop(_In_ WDFQUEUE Queue, _In_ WDFREQUEST Request, _In_ ULONG ActionFlags)
{
    UNREFERENCED_PARAMETER(Queue);
    UNREFERENCED_PARAMETER(ActionFlags);

    WdfRequestComplete(Request, STATUS_CANCELLED);
}

VOID
EvtEndpointQueueIoCanceled(_In_ WDFQUEUE Queue, _In_ WDFREQUEST Request)
{
    UNREFERENCED_PARAMETER(Queue);
    UNREFERENCED_PARAMETER(Request);
}

VOID
EvtEndpointCommandQueue(_In_ WDFQUEUE Queue, _In_ WDFREQUEST Request, _In_ size_t OutputBufferLength, _In_ size_t InputBufferLength, _In_ ULONG IoControlCode)
{
    UNREFERENCED_PARAMETER(OutputBufferLength);
    UNREFERENCED_PARAMETER(InputBufferLength);

    PENDPOINT_QUEUE_CONTEXT queueContext = EndpointQueueGetContext(Queue);
    PFW_ENDPOINT_CONTEXT epContext = FwEndpointGetContext(queueContext->Endpoint);
    NTSTATUS status = STATUS_SUCCESS;

    PVOID outputBuffer;
    status = WdfRequestRetrieveOutputBuffer(Request, 0, &outputBuffer, NULL);
    if (!NT_SUCCESS(status)) {
        KdPrint(("Error: Retrieve command output buffer failed 0x%x\n", status));
        goto Complete;
    }

    switch (IoControlCode) {
    case IOCTL_INTERNAL_USBFN_GET_PIPE_STATE:
        // Return pipe stall state
        *(PBOOLEAN)outputBuffer = FALSE; // Not stalled
        WdfRequestComplete(Request, STATUS_SUCCESS);
        break;

    case IOCTL_INTERNAL_USBFN_SET_PIPE_STATE:
        if (*(PBOOLEAN)outputBuffer) {
            // Stall the pipe
        } else {
            // Clear stall
        }
        WdfRequestComplete(Request, STATUS_SUCCESS);
        break;

    case IOCTL_INTERNAL_USBFN_DESCRIPTOR_UPDATE:
        // Update endpoint descriptor
        WdfRequestComplete(Request, STATUS_SUCCESS);
        break;

    default:
        status = STATUS_INVALID_DEVICE_REQUEST;
        goto Complete;
    }

Complete:
    if (!NT_SUCCESS(status)) {
        WdfRequestComplete(Request, status);
    }
}