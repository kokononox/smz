#include <stddef.h>
#include <string.h>

#include "tusb.h"

#define USB_VID 0x2e8au
#define USB_PID 0x10a1u
#define USB_BCD 0x0100u

enum {
    ITF_NUM_CDC = 0,
    ITF_NUM_CDC_DATA,
    ITF_NUM_HID,
    ITF_NUM_TOTAL,
};

enum {
    STRID_LANGID = 0,
    STRID_MANUFACTURER,
    STRID_PRODUCT,
    STRID_SERIAL,
    STRID_CDC,
    STRID_HID,
};

#define EPNUM_CDC_NOTIF 0x81u
#define EPNUM_CDC_OUT   0x02u
#define EPNUM_CDC_IN    0x82u
#define EPNUM_HID_IN    0x83u

static const tusb_desc_device_t device_descriptor = {
    .bLength = sizeof(tusb_desc_device_t),
    .bDescriptorType = TUSB_DESC_DEVICE,
    .bcdUSB = 0x0200,
    .bDeviceClass = TUSB_CLASS_MISC,
    .bDeviceSubClass = MISC_SUBCLASS_COMMON,
    .bDeviceProtocol = MISC_PROTOCOL_IAD,
    .bMaxPacketSize0 = CFG_TUD_ENDPOINT0_SIZE,
    .idVendor = USB_VID,
    .idProduct = USB_PID,
    .bcdDevice = USB_BCD,
    .iManufacturer = STRID_MANUFACTURER,
    .iProduct = STRID_PRODUCT,
    .iSerialNumber = STRID_SERIAL,
    .bNumConfigurations = 1,
};

uint8_t const *tud_descriptor_device_cb(void) {
    return (uint8_t const *)&device_descriptor;
}

static const uint8_t hid_report_descriptor[] = {
    TUD_HID_REPORT_DESC_KEYBOARD()
};

uint8_t const *tud_hid_descriptor_report_cb(uint8_t instance) {
    (void)instance;
    return hid_report_descriptor;
}

#define CONFIG_TOTAL_LEN \
    (TUD_CONFIG_DESC_LEN + TUD_CDC_DESC_LEN + TUD_HID_DESC_LEN)

static const uint8_t configuration_descriptor[] = {
    TUD_CONFIG_DESCRIPTOR(1, ITF_NUM_TOTAL, 0, CONFIG_TOTAL_LEN,
                          TUSB_DESC_CONFIG_ATT_REMOTE_WAKEUP, 100),
    TUD_CDC_DESCRIPTOR(ITF_NUM_CDC, STRID_CDC, EPNUM_CDC_NOTIF, 8,
                       EPNUM_CDC_OUT, EPNUM_CDC_IN, 64),
    TUD_HID_DESCRIPTOR(ITF_NUM_HID, STRID_HID, HID_ITF_PROTOCOL_KEYBOARD,
                       sizeof(hid_report_descriptor), EPNUM_HID_IN, 16, 1),
};

uint8_t const *tud_descriptor_configuration_cb(uint8_t index) {
    (void)index;
    return configuration_descriptor;
}

static const char *string_descriptors[] = {
    (const char[]){0x09, 0x04},
    "AMS",
    "AMS ABVM Native Pico",
    "ABVM-RP2040",
    "ABVM Diagnostics",
    "ABVM Keyboard",
};

static uint16_t string_buffer[32];

uint16_t const *tud_descriptor_string_cb(uint8_t index, uint16_t langid) {
    (void)langid;
    size_t count;
    if (index == STRID_LANGID) {
        memcpy(&string_buffer[1], string_descriptors[0], 2);
        count = 1;
    } else {
        if (index >= sizeof(string_descriptors) / sizeof(string_descriptors[0]))
            return NULL;
        const char *value = string_descriptors[index];
        count = strlen(value);
        if (count > 31u) count = 31u;
        for (size_t i = 0; i < count; ++i)
            string_buffer[1 + i] = (uint8_t)value[i];
    }
    string_buffer[0] = (uint16_t)((TUSB_DESC_STRING << 8) | (2u * count + 2u));
    return string_buffer;
}

uint16_t tud_hid_get_report_cb(uint8_t instance, uint8_t report_id,
                               hid_report_type_t report_type, uint8_t *buffer,
                               uint16_t requested_len) {
    (void)instance;
    (void)report_id;
    (void)report_type;
    (void)buffer;
    (void)requested_len;
    return 0;
}

void tud_hid_set_report_cb(uint8_t instance, uint8_t report_id,
                           hid_report_type_t report_type,
                           uint8_t const *buffer, uint16_t bufsize) {
    (void)instance;
    (void)report_id;
    (void)report_type;
    (void)buffer;
    (void)bufsize;
}
