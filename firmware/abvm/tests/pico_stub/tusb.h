#ifndef TUSB_H
#define TUSB_H
#include <stdbool.h>
#include <stdint.h>
#define KEYBOARD_MODIFIER_LEFTCTRL 1u
#define KEYBOARD_MODIFIER_LEFTSHIFT 2u
#define KEYBOARD_MODIFIER_LEFTALT 4u
#define KEYBOARD_MODIFIER_LEFTGUI 8u
#define HID_KEY_A 4u
#define HID_KEY_1 30u
#define HID_KEY_2 31u
#define HID_KEY_3 32u
#define HID_KEY_4 33u
#define HID_KEY_5 34u
#define HID_KEY_6 35u
#define HID_KEY_7 36u
#define HID_KEY_8 37u
#define HID_KEY_9 38u
#define HID_KEY_0 39u
#define HID_KEY_ENTER 40u
#define HID_KEY_ESCAPE 41u
#define HID_KEY_BACKSPACE 42u
#define HID_KEY_TAB 43u
#define HID_KEY_SPACE 44u
#define HID_KEY_MINUS 45u
#define HID_KEY_EQUAL 46u
#define HID_KEY_BRACKET_LEFT 47u
#define HID_KEY_BRACKET_RIGHT 48u
#define HID_KEY_BACKSLASH 49u
#define HID_KEY_SEMICOLON 51u
#define HID_KEY_APOSTROPHE 52u
#define HID_KEY_GRAVE 53u
#define HID_KEY_COMMA 54u
#define HID_KEY_PERIOD 55u
#define HID_KEY_SLASH 56u
#define HID_KEY_F1 58u
#define HID_KEY_ARROW_RIGHT 79u
#define HID_KEY_ARROW_LEFT 80u
#define HID_KEY_ARROW_DOWN 81u
#define HID_KEY_ARROW_UP 82u
bool tud_mounted(void);
bool tud_hid_ready(void);
bool tud_hid_keyboard_report(uint8_t report_id, uint8_t modifiers, const uint8_t keycodes[6]);
#endif
