#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <string.h>

#include "bsp/board.h"
#include "pico/stdlib.h"
#include "tusb.h"

static char line[32];
static size_t line_length;
static bool announced;

static void write_text(const char *text) {
    if (!tud_cdc_connected()) return;
    tud_cdc_write(text, (uint32_t)strlen(text));
    tud_cdc_write_flush();
}
static void service_cdc(void) {
    if (tud_cdc_connected() && !announced) {
        announced = true;
        write_text("READY|USB-COMPOSITE-SMOKE|cdc=on|hid=keyboard|actors=off\r\n");
    }
    if (!tud_cdc_connected()) announced = false;
    while (tud_cdc_available()) {
        int value = tud_cdc_read_char();
        if (value == '\r' || value == '\n') {
            if (line_length) {
                line[line_length] = 0;
                write_text(!strcmp(line, "PING")
                    ? "OK|PONG|native-usb-composite-smoke|cdc=on|hid=keyboard\r\n"
                    : "ERR|USB-COMPOSITE-SMOKE|command\r\n");
                line_length = 0;
            }
        } else if (value >= 32 && value <= 126) {
            if (line_length + 1u < sizeof(line)) line[line_length++] = (char)value;
            else line_length = 0;
        }
    }
}
int main(void) {
    board_init();
    tusb_init();
    while (true) {
        tud_task();
        service_cdc();
        sleep_ms(1);
    }
}
