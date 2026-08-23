/*++
FakeWheel RS50 — virtual Logitech RS50 USB Function Driver (UFX).
Utility functions: logging, helpers.
--*/

#include "fake_rs50ufx.h"

//
// Capture log — every exchanged byte, appended to C:\Windows\Temp\FakeRs50.log
//

VOID
FwLogOpen(_In_ PFW_DEVICE_CONTEXT DeviceContext)
{
    NTSTATUS status;
    IO_STATUS_BLOCK ioStatus;
    UNICODE_STRING uniName;
    OBJECT_ATTRIBUTES objAttr;

    RtlInitUnicodeString(&uniName, L"\\DosDevices\\C:\\Windows\\Temp\\FakeRs50.log");
    InitializeObjectAttributes(&objAttr, &uniName,
                               OBJ_CASE_INSENSITIVE | OBJ_KERNEL_HANDLE,
                               NULL, NULL);

    status = ZwCreateFile(&DeviceContext->LogHandle,
                          GENERIC_WRITE,
                          &objAttr,
                          &ioStatus,
                          NULL,
                          FILE_ATTRIBUTE_NORMAL,
                          FILE_SHARE_READ,
                          FILE_OPEN_IF,
                          FILE_SYNCHRONOUS_IO_NONALERT,
                          NULL, 0);
    if (!NT_SUCCESS(status)) {
        DeviceContext->LogHandle = NULL;
        KdPrint(("Failed to open log file: 0x%x\n", status));
    }
}

VOID
FwLogWrite(_In_ PFW_DEVICE_CONTEXT DeviceContext, _In_ PCWSTR Line, _In_ ULONG LengthChars)
{
    if (!DeviceContext->LogHandle) return;
    IO_STATUS_BLOCK ioStatus;
    ZwWriteFile(DeviceContext->LogHandle, NULL, NULL, NULL, &ioStatus,
                (PVOID)Line, LengthChars * sizeof(WCHAR), NULL, NULL);
}

VOID
FwLogRaw(_In_ PFW_DEVICE_CONTEXT DeviceContext, _In_ CHAR Dir, _In_ const UCHAR* Buf, _In_ ULONG Len)
{
    WCHAR line[640];
    ULONG n = 0;
    ULONGLONG ms = FwGetTickMs();

    {
        WCHAR digits[24];
        ULONG d = 0;
        do { digits[d++] = L"0123456789"[ms % 10]; ms /= 10; } while (ms > 0 && d < 24);
        line[n++] = L'[';
        while (d > 0) { line[n++] = digits[--d]; }
        line[n++] = L']'; line[n++] = L' ';
        line[n++] = (WCHAR)Dir; line[n++] = L' ';
    }

    for (ULONG i = 0; i < Len && n < 620; i++) {
        UCHAR b = Buf[i];
        line[n++] = L"0123456789ABCDEF"[b >> 4];
        line[n++] = L"0123456789ABCDEF"[b & 0x0F];
        line[n++] = L' ';
    }
    while (n < 620) { line[n++] = L' '; }
    line[n++] = L'\r'; line[n++] = L'\n'; line[n] = L'\0';
    FwLogWrite(DeviceContext, line, n);
}

ULONGLONG
FwGetTickMs(VOID)
{
    LARGE_INTEGER tick;
    tick = KeQueryInterruptTimePrecise(NULL);
    return tick.QuadPart / 10000;
}

NTSTATUS
FwRequestCopyFromBuffer(_In_ WDFREQUEST Request, _In_ PVOID SourceBuffer, _In_ size_t NumBytesToCopyFrom)
{
    NTSTATUS status;
    WDFMEMORY memory;

    status = WdfMemoryCreatePreallocated(WDF_NO_OBJECT_ATTRIBUTES,
                                         SourceBuffer, NumBytesToCopyFrom,
                                         &memory);
    if (!NT_SUCCESS(status)) return status;

    status = WdfRequestSetInformation(Request, NumBytesToCopyFrom);
    if (!NT_SUCCESS(status)) return status;

    return WdfRequestWriteMemory(Request, memory, NULL);
}