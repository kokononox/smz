#include "hid_keyboard.h"

#include <string.h>

#include "tusb.h"

#define REPORT_ID_KEYBOARD 0u
#define MAX_KEYS 6u

typedef enum ActorPhase {
    ACTOR_IDLE = 0,
    ACTOR_SEND_PRESS,
    ACTOR_WAIT_HOLD,
    ACTOR_SEND_RESTORE,
    ACTOR_SEND_PERSISTENT,
} ActorPhase;

typedef struct KeyboardActor {
    uint8_t persistent_modifiers;
    uint8_t persistent_keys[MAX_KEYS];
    uint8_t report_modifiers;
    uint8_t report_keys[MAX_KEYS];
    uint8_t lane;
    uint8_t phase;
    uint32_t due;
    bool release_pending;
    bool completion_pending;
    uint8_t completion_lane;
} KeyboardActor;

static KeyboardActor actor;

static bool time_reached(uint32_t now, uint32_t due) {
    return (int32_t)(now - due) >= 0;
}

static bool add_key(uint8_t keys[MAX_KEYS], uint8_t key) {
    if (!key) return true;
    for (uint8_t i = 0; i < MAX_KEYS; ++i) {
        if (keys[i] == key) return true;
        if (!keys[i]) {
            keys[i] = key;
            return true;
        }
    }
    return false;
}

static bool vk_to_hid(uint8_t vk, uint8_t *modifier, uint8_t *keycode) {
    *modifier = 0;
    *keycode = 0;
    if (vk >= 'A' && vk <= 'Z') {
        *keycode = (uint8_t)(HID_KEY_A + vk - 'A');
        return true;
    }
    if (vk >= '0' && vk <= '9') {
        *keycode = vk == '0' ? HID_KEY_0 : (uint8_t)(HID_KEY_1 + vk - '1');
        return true;
    }
    if (vk >= 112u && vk <= 123u) {
        *keycode = (uint8_t)(HID_KEY_F1 + vk - 112u);
        return true;
    }
    switch (vk) {
        case 8: *keycode = HID_KEY_BACKSPACE; return true;
        case 9: *keycode = HID_KEY_TAB; return true;
        case 13: *keycode = HID_KEY_ENTER; return true;
        case 27: *keycode = HID_KEY_ESCAPE; return true;
        case 32: *keycode = HID_KEY_SPACE; return true;
        case 37: *keycode = HID_KEY_ARROW_LEFT; return true;
        case 38: *keycode = HID_KEY_ARROW_UP; return true;
        case 39: *keycode = HID_KEY_ARROW_RIGHT; return true;
        case 40: *keycode = HID_KEY_ARROW_DOWN; return true;
        case 91: *modifier = KEYBOARD_MODIFIER_LEFTGUI; return true;
        case 160: *modifier = KEYBOARD_MODIFIER_LEFTSHIFT; return true;
        case 162: *modifier = KEYBOARD_MODIFIER_LEFTCTRL; return true;
        case 164: *modifier = KEYBOARD_MODIFIER_LEFTALT; return true;
        default: return false;
    }
}

static bool send_report(uint8_t modifiers, const uint8_t keys[MAX_KEYS]) {
    if (!tud_mounted() || !tud_hid_ready()) return false;
    return tud_hid_keyboard_report(REPORT_ID_KEYBOARD, modifiers, keys);
}

static bool apply_vk(uint8_t vk, bool down) {
    uint8_t modifier, key;
    if (!vk_to_hid(vk, &modifier, &key)) return false;
    if (modifier) {
        if (down) actor.persistent_modifiers |= modifier;
        else actor.persistent_modifiers &= (uint8_t)~modifier;
        return true;
    }
    if (down) return add_key(actor.persistent_keys, key);
    for (uint8_t i = 0; i < MAX_KEYS; ++i)
        if (actor.persistent_keys[i] == key) actor.persistent_keys[i] = 0;
    return true;
}

void hid_keyboard_init(void) {
    memset(&actor, 0, sizeof(actor));
}

HidKeyboardSubmit hid_keyboard_submit(const AbvmEvent *event, uint32_t now) {
    if (actor.phase != ACTOR_IDLE) return HID_KEYBOARD_BUSY;
    if (event->opcode != ABVM_OP_KEY && event->opcode != ABVM_OP_KDOWN &&
        event->opcode != ABVM_OP_KUP)
        return HID_KEYBOARD_UNSUPPORTED;

    actor.lane = event->lane;
    if (event->opcode == ABVM_OP_KDOWN || event->opcode == ABVM_OP_KUP) {
        if (!apply_vk((uint8_t)event->operand_a,
                      event->opcode == ABVM_OP_KDOWN))
            return HID_KEYBOARD_INVALID;
        actor.phase = ACTOR_SEND_PERSISTENT;
        return HID_KEYBOARD_ACCEPTED;
    }

    actor.report_modifiers = actor.persistent_modifiers;
    memcpy(actor.report_keys, actor.persistent_keys, sizeof(actor.report_keys));
    uint32_t packed = event->operand_b;
    unsigned width = 0;
    while (width < 4u && ((packed >> (width * 8u)) & 0xffu)) ++width;
    if (!width) return HID_KEYBOARD_INVALID;
    for (unsigned i = 0; i < width; ++i) {
        uint8_t modifier, key;
        uint8_t vk = (uint8_t)(packed >> (i * 8u));
        if (!vk_to_hid(vk, &modifier, &key)) return HID_KEYBOARD_INVALID;
        actor.report_modifiers |= modifier;
        if (!add_key(actor.report_keys, key)) return HID_KEYBOARD_INVALID;
    }
    uint32_t lo = event->operand_c;
    uint32_t hi = event->operand_d < lo ? lo : event->operand_d;
    actor.due = now + lo + (hi - lo) / 2u;
    actor.phase = ACTOR_SEND_PRESS;
    return HID_KEYBOARD_ACCEPTED;
}

bool hid_keyboard_service(uint32_t now, uint8_t *completed_lane) {
    if (actor.release_pending) {
        uint8_t empty[MAX_KEYS] = {0};
        if (send_report(0, empty)) actor.release_pending = false;
    }
    if (!actor.release_pending && actor.completion_pending) {
        *completed_lane = actor.completion_lane;
        actor.completion_pending = false;
        return true;
    }
    switch (actor.phase) {
        case ACTOR_SEND_PRESS:
            if (send_report(actor.report_modifiers, actor.report_keys))
                actor.phase = ACTOR_WAIT_HOLD;
            break;
        case ACTOR_WAIT_HOLD:
            if (time_reached(now, actor.due)) actor.phase = ACTOR_SEND_RESTORE;
            break;
        case ACTOR_SEND_RESTORE:
            if (send_report(actor.persistent_modifiers, actor.persistent_keys)) {
                *completed_lane = actor.lane;
                actor.phase = ACTOR_IDLE;
                return true;
            }
            break;
        case ACTOR_SEND_PERSISTENT:
            if (send_report(actor.persistent_modifiers, actor.persistent_keys)) {
                *completed_lane = actor.lane;
                actor.phase = ACTOR_IDLE;
                return true;
            }
            break;
        default:
            break;
    }
    return false;
}

void hid_keyboard_release_all(void) {
    if (actor.phase != ACTOR_IDLE) {
        actor.completion_pending = true;
        actor.completion_lane = actor.lane;
    }
    memset(actor.persistent_keys, 0, sizeof(actor.persistent_keys));
    memset(actor.report_keys, 0, sizeof(actor.report_keys));
    actor.persistent_modifiers = 0;
    actor.report_modifiers = 0;
    actor.phase = ACTOR_IDLE;
    actor.release_pending = true;
}

bool hid_keyboard_busy(void) {
    return actor.phase != ACTOR_IDLE;
}
