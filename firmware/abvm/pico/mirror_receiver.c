#include "mirror_receiver.h"

#include <stdbool.h>
#include <string.h>

#include "hardware/gpio.h"
#include "hardware/uart.h"
#include "hid_keyboard.h"
#include "mirror_frame.h"
#include "pico/stdlib.h"
#include "tusb.h"

#define MIRROR_UART uart1
#define MIRROR_BAUD 1000000u
#define MIRROR_RX_PIN 9u
#define MIRROR_TIMEOUT_US 350000u
#define MIRROR_REPORT_ID_MOUSE 2u

#define EVENT_MOUSE_MOVE 0x02u
#define EVENT_MOUSE_BUTTON 0x03u
#define EVENT_MOUSE_WHEEL 0x04u
#define EVENT_HEARTBEAT 0x05u
#define EVENT_STATE_SNAPSHOT 0x07u

typedef struct MirrorReceiverState {
    MirrorFrameParser parser;
    uint8_t modifiers;
    uint8_t keys[6];
    uint8_t mouse_buttons;
    int32_t pending_dx;
    int32_t pending_dy;
    int32_t pending_vertical_wheel;
    int32_t pending_horizontal_wheel;
    uint32_t last_valid_event_us;
    bool initialized;
    bool active;
    bool keyboard_dirty;
    bool mouse_dirty;
    bool timed_out;
    bool cleanup_keyboard_pending;
    bool cleanup_mouse_pending;
} MirrorReceiverState;

static MirrorReceiverState receiver;

static uint16_t get_u16_le(const uint8_t *p) {
    return (uint16_t)((uint16_t)p[0] | ((uint16_t)p[1] << 8));
}

static int32_t clamp_add(int32_t current, int32_t amount, int32_t limit) {
    const int64_t sum = (int64_t)current + amount;
    if (sum > limit) return limit;
    if (sum < -limit) return -limit;
    return (int32_t)sum;
}

static bool state_is_released(void) {
    if (receiver.modifiers != 0u || receiver.mouse_buttons != 0u ||
        receiver.pending_dx != 0 || receiver.pending_dy != 0 ||
        receiver.pending_vertical_wheel != 0 ||
        receiver.pending_horizontal_wheel != 0) return false;
    for (size_t i = 0u; i < sizeof(receiver.keys); ++i) {
        if (receiver.keys[i] != 0u) return false;
    }
    return true;
}

static void release_received_state(void) {
    receiver.modifiers = 0u;
    memset(receiver.keys, 0, sizeof(receiver.keys));
    receiver.mouse_buttons = 0u;
    receiver.pending_dx = 0;
    receiver.pending_dy = 0;
    receiver.pending_vertical_wheel = 0;
    receiver.pending_horizontal_wheel = 0;
    receiver.keyboard_dirty = true;
    receiver.mouse_dirty = true;
}

static void process_event(const MirrorFrame *frame) {
    const uint8_t *payload = frame->payload;
    switch (payload[0]) {
        case EVENT_MOUSE_MOVE: {
            const int16_t dx = (int16_t)get_u16_le(&payload[1]);
            const int16_t dy = (int16_t)get_u16_le(&payload[3]);
            receiver.pending_dx = clamp_add(receiver.pending_dx, dx, 32767);
            receiver.pending_dy = clamp_add(receiver.pending_dy, dy, 32767);
            receiver.mouse_dirty = true;
            break;
        }
        case EVENT_MOUSE_BUTTON: {
            const uint8_t mask = (uint8_t)(1u << (payload[1] - 1u));
            const uint8_t next = payload[2] != 0u
                ? (uint8_t)(receiver.mouse_buttons | mask)
                : (uint8_t)(receiver.mouse_buttons & (uint8_t)~mask);
            if (next != receiver.mouse_buttons) {
                receiver.mouse_buttons = next;
                receiver.mouse_dirty = true;
            }
            break;
        }
        case EVENT_MOUSE_WHEEL: {
            const int16_t delta = (int16_t)get_u16_le(&payload[1]);
            if (payload[3] == 0u) {
                receiver.pending_vertical_wheel =
                    clamp_add(receiver.pending_vertical_wheel, delta, 12000);
            } else {
                receiver.pending_horizontal_wheel =
                    clamp_add(receiver.pending_horizontal_wheel, delta, 12000);
            }
            receiver.mouse_dirty = true;
            break;
        }
        case EVENT_STATE_SNAPSHOT: {
            const uint8_t buttons = (uint8_t)(payload[8] & 0x1Fu);
            if (receiver.modifiers != payload[1] ||
                memcmp(receiver.keys, &payload[2], sizeof(receiver.keys)) != 0) {
                receiver.modifiers = payload[1];
                memcpy(receiver.keys, &payload[2], sizeof(receiver.keys));
                receiver.keyboard_dirty = true;
            }
            if (receiver.mouse_buttons != buttons) {
                receiver.mouse_buttons = buttons;
                receiver.mouse_dirty = true;
            }
            break;
        }
        case EVENT_HEARTBEAT:
        default:
            break;
    }
}

void mirror_receiver_init(void) {
    memset(&receiver, 0, sizeof(receiver));
    uart_init(MIRROR_UART, MIRROR_BAUD);
    gpio_set_function(MIRROR_RX_PIN, GPIO_FUNC_UART);
    uart_set_hw_flow(MIRROR_UART, false, false);
    uart_set_format(MIRROR_UART, 8u, 1u, UART_PARITY_NONE);
    uart_set_fifo_enabled(MIRROR_UART, true);
    receiver.initialized = true;
}

void mirror_receiver_start(void) {
    if (!receiver.initialized) return;
    mirror_frame_parser_reset(&receiver.parser);
    release_received_state();
    receiver.active = true;
    receiver.timed_out = false;
    receiver.last_valid_event_us = time_us_32();
    receiver.cleanup_keyboard_pending = false;
    receiver.cleanup_mouse_pending = false;
    while (uart_is_readable(MIRROR_UART)) (void)uart_getc(MIRROR_UART);
}

void mirror_receiver_stop(void) {
    if (!receiver.initialized) return;
    receiver.active = false;
    mirror_frame_parser_reset(&receiver.parser);
    release_received_state();
    receiver.cleanup_keyboard_pending = true;
    receiver.cleanup_mouse_pending = true;
    receiver.timed_out = true;
}

bool mirror_receiver_active(void) {
    return receiver.active;
}

bool mirror_receiver_cleanup_pending(void) {
    return receiver.cleanup_keyboard_pending ||
           receiver.cleanup_mouse_pending;
}

static int8_t take_mouse_chunk(int32_t *pending) {
    int32_t chunk = *pending;
    if (chunk > 127) chunk = 127;
    if (chunk < -127) chunk = -127;
    *pending -= chunk;
    return (int8_t)chunk;
}

static int8_t take_wheel_step(int32_t *pending) {
    int32_t steps = *pending / 120;
    if (steps > 127) steps = 127;
    if (steps < -127) steps = -127;
    *pending -= steps * 120;
    return (int8_t)steps;
}

static bool send_mouse_report(int8_t dx, int8_t dy, int8_t vertical,
                              int8_t horizontal) {
    if (!tud_mounted() || !tud_hid_ready()) return false;
    return tud_hid_mouse_report(MIRROR_REPORT_ID_MOUSE, receiver.mouse_buttons,
                                dx, dy, vertical, horizontal);
}

void mirror_receiver_service(void) {
    if (!receiver.initialized ||
        (!receiver.active && !mirror_receiver_cleanup_pending())) return;

    if (!receiver.active && mirror_receiver_cleanup_pending() &&
        !tud_mounted()) {
        /* A USB disconnect resets the host's remembered HID state. */
        receiver.cleanup_keyboard_pending = false;
        receiver.cleanup_mouse_pending = false;
        receiver.keyboard_dirty = false;
        receiver.mouse_dirty = false;
        receiver.pending_dx = 0;
        receiver.pending_dy = 0;
        receiver.pending_vertical_wheel = 0;
        receiver.pending_horizontal_wheel = 0;
        return;
    }

    if (receiver.active) {
        MirrorFrame frame;
        while (uart_is_readable(MIRROR_UART)) {
            if (!mirror_frame_parser_feed(&receiver.parser,
                                          (uint8_t)uart_getc(MIRROR_UART),
                                          &frame)) continue;
            if (!mirror_frame_valid_event(&frame)) continue;
            receiver.last_valid_event_us = time_us_32();
            receiver.timed_out = false;
            process_event(&frame);
        }
        const uint32_t now = time_us_32();
        if (!receiver.timed_out &&
            (uint32_t)(now - receiver.last_valid_event_us) >
                MIRROR_TIMEOUT_US) {
            release_received_state();
            receiver.timed_out = true;
        }
    }

    if (receiver.keyboard_dirty) {
        if (hid_keyboard_send_mirror_report(receiver.modifiers,
                                            receiver.keys)) {
            receiver.keyboard_dirty = false;
            if (!receiver.active) receiver.cleanup_keyboard_pending = false;
        }
    }

    if (receiver.mouse_dirty || receiver.pending_dx != 0 ||
        receiver.pending_dy != 0 ||
        receiver.pending_vertical_wheel >= 120 ||
        receiver.pending_vertical_wheel <= -120 ||
        receiver.pending_horizontal_wheel >= 120 ||
        receiver.pending_horizontal_wheel <= -120) {
        const int8_t dx = take_mouse_chunk(&receiver.pending_dx);
        const int8_t dy = take_mouse_chunk(&receiver.pending_dy);
        const int8_t vertical = take_wheel_step(&receiver.pending_vertical_wheel);
        const int8_t horizontal = take_wheel_step(&receiver.pending_horizontal_wheel);
        if (send_mouse_report(dx, dy, vertical, horizontal)) {
            receiver.mouse_dirty = receiver.pending_dx != 0 ||
                                  receiver.pending_dy != 0 ||
                                  receiver.pending_vertical_wheel != 0 ||
                                  receiver.pending_horizontal_wheel != 0;
            if (!receiver.active && !receiver.mouse_dirty) {
                receiver.cleanup_mouse_pending = false;
            }
        } else {
            receiver.pending_dx += dx;
            receiver.pending_dy += dy;
            receiver.pending_vertical_wheel += (int32_t)vertical * 120;
            receiver.pending_horizontal_wheel += (int32_t)horizontal * 120;
        }
    }

    if (!receiver.active && mirror_receiver_cleanup_pending() &&
        state_is_released()) {
        /* Keep trying only for the reports that did not make it onto USB yet. */
        if (!receiver.keyboard_dirty) receiver.cleanup_keyboard_pending = false;
        if (!receiver.mouse_dirty) receiver.cleanup_mouse_pending = false;
    }
}