/*++
FakeWheel RS50 — virtual Logitech RS50 USB Function Driver (UFX).
USB and HID Descriptors.
--*/

#include "fake_rs50ufx.h"

//
// Joystick Report Descriptor (Interface 0) - 30 bytes, no report ID
// Usage Page: Generic Desktop (0x01), Usage: Joystick (0x04)
//
UCHAR G_JoystickReportDescriptor[] = {
    0x05, 0x01,        // Usage Page (Generic Desktop Ctrls)
    0x09, 0x04,        // Usage (Joystick)
    0xA1, 0x01,        // Collection (Application)
    0x09, 0x01,        //   Usage (Pointer)
    0xA1, 0x00,        //   Collection (Physical)
    0x09, 0x30,        //     Usage (X)
    0x09, 0x31,        //     Usage (Y)
    0x09, 0x32,        //     Usage (Z)
    0x09, 0x35,        //     Usage (Rz)
    0x15, 0x00,        //     Logical Minimum (0)
    0x26, 0xFF, 0x00,  //     Logical Maximum (255)
    0x75, 0x08,        //     Report Size (8)
    0x95, 0x04,        //     Report Count (4)
    0x81, 0x02,        //     Input (Data,Var,Abs)
    0x09, 0x39,        //     Usage (Hat switch)
    0x15, 0x00,        //     Logical Minimum (0)
    0x25, 0x07,        //     Logical Maximum (7)
    0x75, 0x04,        //     Report Size (4)
    0x95, 0x01,        //     Report Count (1)
    0x81, 0x02,        //     Input (Data,Var,Abs)
    0x05, 0x09,        //     Usage Page (Button)
    0x19, 0x01,        //     Usage Minimum (0x01)
    0x29, 0x0C,        //     Usage Maximum (0x0C)
    0x15, 0x00,        //     Logical Minimum (0)
    0x25, 0x01,        //     Logical Maximum (1)
    0x75, 0x01,        //     Report Size (1)
    0x95, 0x0C,        //     Report Count (12)
    0x81, 0x02,        //     Input (Data,Var,Abs)
    0x75, 0x01,        //     Report Size (1)
    0x95, 0x04,        //     Report Count (4)
    0x81, 0x01,        //     Input (Const,Array,Abs) - padding
    0xC0,              //   End Collection
    0xC0               // End Collection
};
USHORT G_JoystickReportDescriptorLength = sizeof(G_JoystickReportDescriptor);

//
// HID++ Report Descriptor (Interface 1) - Three collections with Report IDs 0x10, 0x11, 0x12
// Usage Page: Vendor Defined (0xFF43), Usage: 0x0701/0x0702/0x0704
//
UCHAR G_HidppReportDescriptor[] = {
    // Collection 1: Short Report (Report ID 0x10) - 7 bytes
    0x06, 0x43, 0xFF,  // Usage Page (FF43h, Logitech HID++)
    0x0A, 0x01, 0x07,  // Usage (0701h)
    0xA1, 0x01,        // Collection (Application)
    0x85, 0x10,        //   Report ID (10h)
    0x09, 0x01,        //   Usage (0001h)
    0x15, 0x00,        //   Logical Minimum (0)
    0x26, 0xFF, 0x00,  //   Logical Maximum (255)
    0x75, 0x08,        //   Report Size (8)
    0x95, 0x06,        //   Report Count (6)
    0x81, 0x00,        //   Input (Data,Array,Abs)
    0x95, 0x06,        //   Report Count (6)
    0x91, 0x00,        //   Output (Data,Array,Abs)
    0xC0,              // End Collection

    // Collection 2: Long Report (Report ID 0x11) - 20 bytes
    0x06, 0x43, 0xFF,  // Usage Page (FF43h)
    0x0A, 0x02, 0x07,  // Usage (0702h)
    0xA1, 0x01,        // Collection (Application)
    0x85, 0x11,        //   Report ID (11h)
    0x09, 0x01,        //   Usage (0001h)
    0x15, 0x00,        //   Logical Minimum (0)
    0x26, 0xFF, 0x00,  //   Logical Maximum (255)
    0x75, 0x08,        //   Report Size (8)
    0x95, 0x13,        //   Report Count (19)
    0x81, 0x00,        //   Input (Data,Array,Abs)
    0x95, 0x13,        //   Report Count (19)
    0x91, 0x00,        //   Output (Data,Array,Abs)
    0xC0,              // End Collection

    // Collection 3: Very Long Report (Report ID 0x12) - 64 bytes
    0x06, 0x43, 0xFF,  // Usage Page (FF43h)
    0x0A, 0x04, 0x07,  // Usage (0704h)
    0xA1, 0x01,        // Collection (Application)
    0x85, 0x12,        //   Report ID (12h)
    0x09, 0x01,        //   Usage (0001h)
    0x15, 0x00,        //   Logical Minimum (0)
    0x26, 0xFF, 0x00,  //   Logical Maximum (255)
    0x75, 0x08,        //   Report Size (8)
    0x95, 0x3F,        //   Report Count (63)
    0x81, 0x00,        //   Input (Data,Array,Abs)
    0x95, 0x3F,        //   Report Count (63)
    0x91, 0x00,        //   Output (Data,Array,Abs)
    0xC0               // End Collection
};
USHORT G_HidppReportDescriptorLength = sizeof(G_HidppReportDescriptor);

//
// Force Feedback Report Descriptor (Interface 2) - PID page (0x0F)
//
UCHAR G_FfbReportDescriptor[] = {
    0x05, 0x0F,        // Usage Page (Physical Interface Device)
    0x09, 0x21,        // Usage (Set Effect Report)
    0xA1, 0x01,        // Collection (Application)
    0x85, 0x01,        //   Report ID (1)
    0x09, 0x22,        //   Usage (Effect Block Index)
    0x15, 0x01,        //   Logical Minimum (1)
    0x25, 0x28,        //   Logical Maximum (40)
    0x75, 0x08,        //   Report Size (8)
    0x95, 0x01,        //   Report Count (1)
    0x91, 0x02,        //   Output (Data,Var,Abs)
    0x09, 0x23,        //   Usage (Effect Type)
    0x15, 0x01,        //   Logical Minimum (1)
    0x25, 0x0C,        //   Logical Maximum (12)
    0x75, 0x08,        //   Report Size (8)
    0x95, 0x01,        //   Report Count (1)
    0x91, 0x02,        //   Output (Data,Var,Abs)
    0x09, 0x24,        //   Usage (Duration)
    0x15, 0x00,        //   Logical Minimum (0)
    0x26, 0x10, 0x27,  //   Logical Maximum (10000)
    0x75, 0x10,        //   Report Size (16)
    0x95, 0x01,        //   Report Count (1)
    0x91, 0x02,        //   Output (Data,Var,Abs)
    0x09, 0x25,        //   Usage (Trigger Repeat Interval)
    0x15, 0x00,        //   Logical Minimum (0)
    0x26, 0x10, 0x27,  //   Logical Maximum (10000)
    0x75, 0x10,        //   Report Size (16)
    0x95, 0x01,        //   Report Count (1)
    0x91, 0x02,        //   Output (Data,Var,Abs)
    0x09, 0x26,        //   Usage (Sample Period)
    0x15, 0x00,        //   Logical Minimum (0)
    0x26, 0x10, 0x27,  //   Logical Maximum (10000)
    0x75, 0x10,        //   Report Size (16)
    0x95, 0x01,        //   Report Count (1)
    0x91, 0x02,        //   Output (Data,Var,Abs)
    0x09, 0x27,        //   Usage (Gain)
    0x15, 0x00,        //   Logical Minimum (0)
    0x25, 0xFF,        //   Logical Maximum (255)
    0x75, 0x08,        //   Report Size (8)
    0x95, 0x01,        //   Report Count (1)
    0x91, 0x02,        //   Output (Data,Var,Abs)
    0x09, 0x28,        //   Usage (Trigger Button)
    0x15, 0x01,        //   Logical Minimum (1)
    0x25, 0x08,        //   Logical Maximum (8)
    0x75, 0x08,        //   Report Size (8)
    0x95, 0x01,        //   Report Count (1)
    0x91, 0x02,        //   Output (Data,Var,Abs)
    0x09, 0x29,        //   Usage (Axes Enable)
    0x15, 0x00,        //   Logical Minimum (0)
    0x25, 0xFF,        //   Logical Maximum (255)
    0x75, 0x08,        //   Report Size (8)
    0x95, 0x01,        //   Report Count (1)
    0x91, 0x02,        //   Output (Data,Var,Abs)
    0x09, 0x2A,        //   Usage (Direction Enable)
    0x15, 0x00,        //   Logical Minimum (0)
    0x25, 0xFF,        //   Logical Maximum (255)
    0x75, 0x08,        //   Report Size (8)
    0x95, 0x01,        //   Report Count (1)
    0x91, 0x02,        //   Output (Data,Var,Abs)
    0x09, 0x2B,        //   Usage (Direction)
    0x15, 0x00,        //   Logical Minimum (0)
    0x26, 0xFF, 0x00,  //   Logical Maximum (255)
    0x75, 0x10,        //   Report Size (16)
    0x95, 0x01,        //   Report Count (1)
    0x91, 0x02,        //   Output (Data,Var,Abs)
    0x09, 0x2C,        //   Usage (Type Specific Block Offset)
    0x15, 0x00,        //   Logical Minimum (0)
    0x26, 0xFF, 0x00,  //   Logical Maximum (255)
    0x75, 0x08,        //   Report Size (8)
    0x95, 0x01,        //   Report Count (1)
    0x91, 0x02,        //   Output (Data,Var,Abs)
    0x09, 0x2D,        //   Usage (Block Load Report)
    0xA1, 0x02,        //   Collection (Logical)
    0x85, 0x02,        //     Report ID (2)
    0x09, 0x2E,        //     Usage (Effect Block Index)
    0x15, 0x01,        //     Logical Minimum (1)
    0x25, 0x28,        //     Logical Maximum (40)
    0x75, 0x08,        //     Report Size (8)
    0x95, 0x01,        //     Report Count (1)
    0x81, 0x02,        //     Input (Data,Var,Abs)
    0x09, 0x2F,        //     Usage (Load Status)
    0x15, 0x00,        //     Logical Minimum (0)
    0x25, 0x03,        //     Logical Maximum (3)
    0x75, 0x08,        //     Report Size (8)
    0x95, 0x01,        //     Report Count (1)
    0x81, 0x02,        //     Input (Data,Var,Abs)
    0x09, 0x30,        //     Usage (RAM Pool Available)
    0x15, 0x00,        //     Logical Minimum (0)
    0x25, 0xFF,        //     Logical Maximum (255)
    0x75, 0x08,        //     Report Size (8)
    0x95, 0x01,        //     Report Count (1)
    0x81, 0x02,        //     Input (Data,Var,Abs)
    0xC0,              //   End Collection
    0x09, 0x31,        //   Usage (PID Pool Report)
    0xA1, 0x02,        //   Collection (Logical)
    0x85, 0x03,        //     Report ID (3)
    0x09, 0x32,        //     Usage (RAM Pool Size)
    0x15, 0x00,        //     Logical Minimum (0)
    0x26, 0xFF, 0x00,  //     Logical Maximum (255)
    0x75, 0x08,        //     Report Size (8)
    0x95, 0x01,        //     Report Count (1)
    0x81, 0x02,        //     Input (Data,Var,Abs)
    0x09, 0x33,        //     Usage (Simultaneous Effects Max)
    0x15, 0x00,        //     Logical Minimum (0)
    0x25, 0xFF,        //     Logical Maximum (255)
    0x75, 0x08,        //     Report Size (8)
    0x95, 0x01,        //     Report Count (1)
    0x81, 0x02,        //     Input (Data,Var,Abs)
    0x09, 0x34,        //     Usage (Device Managed Pool)
    0x15, 0x00,        //     Logical Minimum (0)
    0x25, 0x01,        //     Logical Maximum (1)
    0x75, 0x01,        //     Report Size (1)
    0x95, 0x01,        //     Report Count (1)
    0x81, 0x02,        //     Input (Data,Var,Abs)
    0x09, 0x35,        //     Usage (Shared Parameter Blocks)
    0x15, 0x00,        //     Logical Minimum (0)
    0x25, 0xFF,        //     Logical Maximum (255)
    0x75, 0x08,        //     Report Size (8)
    0x95, 0x01,        //     Report Count (1)
    0x81, 0x02,        //     Input (Data,Var,Abs)
    0xC0,              //   End Collection
    0xC0               // End Collection
};
USHORT G_FfbReportDescriptorLength = sizeof(G_FfbReportDescriptor);

//
// HID Descriptors
//
HID_DESCRIPTOR G_JoystickHidDescriptor = {
    0x09, 0x21, 0x0111, 0x00, 0x01,
    { 0x22, sizeof(G_JoystickReportDescriptor) }
};

HID_DESCRIPTOR G_HidppHidDescriptor = {
    0x09, 0x21, 0x0111, 0x00, 0x01,
    { 0x22, sizeof(G_HidppReportDescriptor) }
};

HID_DESCRIPTOR G_FfbHidDescriptor = {
    0x09, 0x21, 0x0111, 0x00, 0x01,
    { 0x22, sizeof(G_FfbReportDescriptor) }
};

//
// String Descriptors
//
UCHAR G_StringDescriptor0[] = { 0x04, 0x03, 0x09, 0x04 };  // English (US)

UCHAR G_StringDescriptorManufacturer[] = {
    0x2E, 0x03,
    'L', 0, 'o', 0, 'g', 0, 'i', 0, 't', 0, 'e', 0, 'c', 0, 'h', 0
};

UCHAR G_StringDescriptorProduct[] = {
    0x2A, 0x03,
    'R', 0, 'S', 0, '5', 0, '0', 0, ' ', 0, 'B', 0, 'a', 0, 's', 0, 'e', 0, ' ', 0, 'f', 0, 'o', 0, 'r', 0, ' ', 0, 'P', 0, 'C'
};

UCHAR G_StringDescriptorSerial[] = {
    0x1E, 0x03,
    'R', 0, 'S', 0, '5', 0, '0', 0, 'F', 0, 'A', 0, 'K', 0, 'E', 0, '0', 0, '0', 0, '1'
};

UCHAR G_StringDescriptorJoystick[] = {
    0x22, 0x03,
    'J', 0, 'o', 0, 'y', 0, 's', 0, 't', 0, 'i', 0, 'c', 0, 'k', 0
};

UCHAR G_StringDescriptorHidpp[] = {
    0x1E, 0x03,
    'H', 0, 'I', 0, 'D', 0, '+', 0, '+', 0
};

UCHAR G_StringDescriptorFfb[] = {
    0x28, 0x03,
    'F', 0, 'o', 0, 'r', 0, 'c', 0, 'e', 0, ' ', 0, 'F', 0, 'e', 0, 'e', 0, 'd', 0, 'b', 0, 'a', 0, 'c', 0, 'k'
};

UCHAR G_StringDescriptorConfig[] = {
    0x1E, 0x03,
    'R', 0, 'S', 0, '5', 0, '0', 0, ' ', 0, 'C', 0, 'o', 0, 'n', 0, 'f', 0, 'i', 0, 'g'
};

//
// String Descriptor Array
//
PUCHAR G_StringDescriptors[] = {
    G_StringDescriptor0,
    G_StringDescriptorManufacturer,
    G_StringDescriptorProduct,
    G_StringDescriptorSerial,
    G_StringDescriptorJoystick,
    G_StringDescriptorHidpp,
    G_StringDescriptorFfb,
    G_StringDescriptorConfig
};

//
// USB Device Descriptor
//
USB_DEVICE_DESCRIPTOR G_UsbDeviceDescriptor = {
    sizeof(USB_DEVICE_DESCRIPTOR),
    USB_DEVICE_DESCRIPTOR_TYPE,
    FAKEWHEEL_USB_BCD_USB,
    0x00,  // Class
    0x00,  // SubClass
    0x00,  // Protocol
    FAKEWHEEL_USB_MAX_PACKET_SIZE_0,
    FAKEWHEEL_USB_VID,
    FAKEWHEEL_USB_PID,
    FAKEWHEEL_USB_BCD_DEVICE,
    STR_ID_MANUFACTURER,
    STR_ID_PRODUCT,
    STR_ID_SERIAL,
    1  // Number of configurations
};

//
// USB Configuration Descriptor with 3 interfaces
//
typedef struct _USB_CONFIGURATION_DESCRIPTOR_FULL {
    USB_CONFIGURATION_DESCRIPTOR Config;
    
    // Interface 0: Joystick
    USB_INTERFACE_DESCRIPTOR     Iface0;
    USB_HID_DESCRIPTOR           Hid0;
    USB_ENDPOINT_DESCRIPTOR      Ep0In;
    
    // Interface 1: HID++
    USB_INTERFACE_DESCRIPTOR     Iface1;
    USB_HID_DESCRIPTOR           Hid1;
    USB_ENDPOINT_DESCRIPTOR      Ep1In;
    
    // Interface 2: Force Feedback
    USB_INTERFACE_DESCRIPTOR     Iface2;
    USB_HID_DESCRIPTOR           Hid2;
    USB_ENDPOINT_DESCRIPTOR      Ep2Out;
} USB_CONFIGURATION_DESCRIPTOR_FULL;

USB_CONFIGURATION_DESCRIPTOR_FULL G_UsbConfigDescriptor = {
    // Configuration Descriptor
    {
        sizeof(USB_CONFIGURATION_DESCRIPTOR),
        USB_CONFIGURATION_DESCRIPTOR_TYPE,
        sizeof(USB_CONFIGURATION_DESCRIPTOR_FULL), 0,
        3,  // Number of interfaces
        STR_ID_CONFIG,
        0x80,  // Self-powered
        250    // MaxPower (500mA)
    },
    // Interface 0: Joystick
    {
        sizeof(USB_INTERFACE_DESCRIPTOR),
        USB_INTERFACE_DESCRIPTOR_TYPE,
        INTERFACE_JOYSTICK,
        0,  // Alternate setting
        1,  // Number of endpoints
        0x03,  // HID
        0x00,  // No subclass
        0x00,  // No protocol
        STR_ID_INTERFACE_0
    },
    // HID Descriptor for Interface 0
    {
        0x09, 0x21, 0x0111, 0x00, 0x01,
        { 0x22, sizeof(G_JoystickReportDescriptor) }
    },
    // Endpoint 0x81 IN (Interrupt)
    {
        sizeof(USB_ENDPOINT_DESCRIPTOR),
        USB_ENDPOINT_DESCRIPTOR_TYPE,
        EP_JOYSTICK_IN,
        USB_ENDPOINT_TYPE_INTERRUPT,
        30, 0,  // wMaxPacketSize = 30
        1  // bInterval = 1ms
    },
    // Interface 1: HID++
    {
        sizeof(USB_INTERFACE_DESCRIPTOR),
        USB_INTERFACE_DESCRIPTOR_TYPE,
        INTERFACE_HIDPP,
        0,  // Alternate setting
        1,  // Number of endpoints
        0x03,  // HID
        0x00,  // No subclass
        0x00,  // No protocol
        STR_ID_INTERFACE_1
    },
    // HID Descriptor for Interface 1
    {
        0x09, 0x21, 0x0111, 0x00, 0x01,
        { 0x22, sizeof(G_HidppReportDescriptor) }
    },
    // Endpoint 0x82 IN (Interrupt)
    {
        sizeof(USB_ENDPOINT_DESCRIPTOR),
        USB_ENDPOINT_DESCRIPTOR_TYPE,
        EP_HIDPP_IN,
        USB_ENDPOINT_TYPE_INTERRUPT,
        64, 0,  // wMaxPacketSize = 64
        1  // bInterval = 1ms
    },
    // Interface 2: Force Feedback
    {
        sizeof(USB_INTERFACE_DESCRIPTOR),
        USB_INTERFACE_DESCRIPTOR_TYPE,
        INTERFACE_FFB,
        0,  // Alternate setting
        1,  // Number of endpoints
        0x03,  // HID
        0x00,  // No subclass
        0x00,  // No protocol
        STR_ID_INTERFACE_2
    },
    // HID Descriptor for Interface 2
    {
        0x09, 0x21, 0x0111, 0x00, 0x01,
        { 0x22, sizeof(G_FfbReportDescriptor) }
    },
    // Endpoint 0x03 OUT (Interrupt)
    {
        sizeof(USB_ENDPOINT_DESCRIPTOR),
        USB_ENDPOINT_DESCRIPTOR_TYPE,
        EP_FFB_OUT,
        USB_ENDPOINT_TYPE_INTERRUPT,
        64, 0,  // wMaxPacketSize = 64
        1  // bInterval = 1ms
    }
};

//
// Device Qualifier Descriptor (for high-speed)
//
USB_DEVICE_QUALIFIER_DESCRIPTOR G_UsbDeviceQualifier = {
    sizeof(USB_DEVICE_QUALIFIER_DESCRIPTOR),
    USB_DEVICE_QUALIFIER_DESCRIPTOR_TYPE,
    FAKEWHEEL_USB_BCD_USB,
    0x00, 0x00, 0x00,
    FAKEWHEEL_USB_MAX_PACKET_SIZE_0,
    1,  // Number of configurations
    0
};