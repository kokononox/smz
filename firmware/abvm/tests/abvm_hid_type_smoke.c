#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>

#include "abvm_vm.h"
#include "hid_keyboard.h"
#include "tusb.h"

static unsigned press_reports;
static unsigned release_reports;
static uint32_t report_now;
static uint32_t pressed_at;
static uint32_t minimum_hold = UINT32_MAX;
static bool report_pressed;
static bool repeated_press_without_release;
static bool saw_keypad_4;

bool tud_mounted(void) { return true; }
bool tud_hid_ready(void) { return true; }
bool tud_hid_keyboard_report(uint8_t report_id, uint8_t modifiers,
                             const uint8_t keycodes[6]) {
    (void)report_id;
    bool pressed = modifiers != 0;
    for (unsigned i = 0; i < 6; ++i) {
        pressed = pressed || keycodes[i] != 0;
        saw_keypad_4 = saw_keypad_4 || keycodes[i] == HID_KEY_KEYPAD_1 + 3u;
    }
    if (pressed) {
        ++press_reports;
        if (report_pressed) repeated_press_without_release = true;
        report_pressed = true;
        pressed_at = report_now;
    } else {
        ++release_reports;
        if (report_pressed) {
            uint32_t held = report_now - pressed_at;
            if (held < minimum_hold) minimum_hold = held;
        }
        report_pressed = false;
    }
    return true;
}

static uint8_t *read_file(const char *path, size_t *size) {
    FILE *file = fopen(path, "rb");
    if (!file) return NULL;
    fseek(file, 0, SEEK_END);
    long length = ftell(file);
    rewind(file);
    if (length <= 0) { fclose(file); return NULL; }
    uint8_t *data = malloc((size_t)length);
    if (!data || fread(data, 1, (size_t)length, file) != (size_t)length) {
        free(data); fclose(file); return NULL;
    }
    fclose(file); *size = (size_t)length; return data;
}

int main(int argc, char **argv) {
    if (argc != 2) return 2;
    size_t size = 0;
    uint8_t *program = read_file(argv[1], &size);
    AbvmVm vm;
    if (!program || !abvm_init(&vm, program, size) ||
        !abvm_start_route(&vm, 10u, 0u)) return 3;
    hid_keyboard_init();
    bool complete = false;
    for (uint32_t now = 0; now < 20000u && !complete; ++now) {
        report_now = now;
        uint8_t lane;
        if (hid_keyboard_service(now, &lane) &&
            !abvm_complete_action(&vm, lane, now)) return 4;
        AbvmEvent event = abvm_tick(&vm, now);
        if (event.type == ABVM_EVENT_RELEASE_ALL) hid_keyboard_release_all();
        else if (event.type == ABVM_EVENT_ACTION) {
            if (hid_keyboard_submit(&vm, &event, now) != HID_KEYBOARD_ACCEPTED) return 5;
        } else if (event.type == ABVM_EVENT_ROUTE_COMPLETE) complete = true;
        else if (event.type == ABVM_EVENT_FAULT) return 6;
    }
    free(program);
    if (!complete || press_reports < 18u || release_reports < press_reports ||
        !saw_keypad_4 ||
        repeated_press_without_release || minimum_hold < 20u) return 7;
    puts("ABVM native nonblocking Type actor smoke passed");
    return 0;
}
