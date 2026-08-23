//
// minimal.c — temporary probe driver: no VHF. If the host loads THIS, the
// problem is in the VHF path; if it also fails, the issue is environmental.
//

#include <windows.h>
#include <wdf.h>

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

NTSTATUS
EvtDeviceAdd(
    _In_ WDFDRIVER Driver,
    _Inout_ PWDFDEVICE_INIT DeviceInit
    )
{
    NTSTATUS status;
    WDF_OBJECT_ATTRIBUTES deviceAttributes;
    WDFDEVICE device;

    UNREFERENCED_PARAMETER(Driver);

    WDF_OBJECT_ATTRIBUTES_INIT(&deviceAttributes);
    status = WdfDeviceCreate(&DeviceInit, &deviceAttributes, &device);
    TraceMsg(L"minimal: DeviceCreate");
    return status;
}

NTSTATUS
DriverEntry(
    _In_ PDRIVER_OBJECT DriverObject,
    _In_ PUNICODE_STRING RegistryPath
    )
{
    NTSTATUS status;
    WDF_DRIVER_CONFIG config;

    TraceMsg(L"minimal: DriverEntry");
    WDF_DRIVER_CONFIG_INIT(&config, EvtDeviceAdd);
    status = WdfDriverCreate(
        DriverObject,
        RegistryPath,
        WDF_NO_OBJECT_ATTRIBUTES,
        &config,
        WDF_NO_HANDLE);
    return status;
}