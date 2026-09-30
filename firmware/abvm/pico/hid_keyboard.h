#ifndef AMS_ABVM_HID_KEYBOARD_H
#define AMS_ABVM_HID_KEYBOARD_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "abvm_vm.h"

typedef enum HidKeyboardSubmit {
    HID_KEYBOARD_UNSUPPORTED = 0,
    HID_KEYBOARD_ACCEPTED = 1,
    HID_KEYBOARD_BUSY = 2,
    HID_KEYBOARD_INVALID = 3,
} HidKeyboardSubmit;

void hid_keyboard_init(void);
HidKeyboardSubmit hid_keyboard_submit(const AbvmVm *vm, const AbvmEvent *event, uint32_t now);
HidKeyboardSubmit hid_keyboard_submit_live(const char *command, uint32_t now);
HidKeyboardSubmit hid_keyboard_submit_trigger(uint8_t vk, uint32_t hold_min,
                                               uint32_t hold_max, uint32_t now);
bool hid_keyboard_service(uint32_t now, uint8_t *completed_lane);
bool hid_keyboard_take_live_reply(char *reply, size_t capacity);
void hid_keyboard_release_all(void);
void hid_keyboard_discard_completion(void);
bool hid_keyboard_busy(void);

#endif
