#ifndef AMS_ABVM_HID_KEYBOARD_H
#define AMS_ABVM_HID_KEYBOARD_H

#include <stdbool.h>
#include <stdint.h>

#include "abvm_vm.h"

typedef enum HidKeyboardSubmit {
    HID_KEYBOARD_UNSUPPORTED = 0,
    HID_KEYBOARD_ACCEPTED = 1,
    HID_KEYBOARD_BUSY = 2,
    HID_KEYBOARD_INVALID = 3,
} HidKeyboardSubmit;

void hid_keyboard_init(void);
HidKeyboardSubmit hid_keyboard_submit(const AbvmEvent *event, uint32_t now);
bool hid_keyboard_service(uint32_t now, uint8_t *completed_lane);
void hid_keyboard_release_all(void);
bool hid_keyboard_busy(void);

#endif
