#include "arm_uart_mouse.h"
#include <stdio.h>
#include <string.h>
#include "hardware/gpio.h"
#include "hardware/uart.h"

#define ARM_UART uart0
#define ARM_UART_BAUD 57600u
#define ARM_UART_TX_PIN 16u
#define ARM_UART_RX_PIN 17u
#define ARM_ACK_TIMEOUT_MS 1500u
#define ARM_LINE_MAX 96u
#define ARM_FRAME_MAX 64u
#define ARM_DELTA_LIMIT 700

typedef enum ArmState { ARM_IDLE, ARM_MOVE, ARM_HALT, ARM_FAULT } ArmState;
static ArmState state;
static char tx[ARM_FRAME_MAX];
static uint8_t tx_len, tx_pos;
static char rx[ARM_LINE_MAX];
static uint8_t rx_len, lane, completion_lane;
static bool completion_pending;
static uint32_t deadline, prng = 0x6d2b79f5u;
static const char *fault_text;

static bool reached(uint32_t now, uint32_t due) { return (int32_t)(now - due) >= 0; }
static uint32_t random_next(void) {
    uint32_t x = prng;
    x ^= x << 13; x ^= x >> 17; x ^= x << 5;
    return prng = x;
}
static uint32_t read_u32_le(const uint8_t *p) {
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) |
           ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}
static bool mouse_constant(const AbvmVm *vm, uint16_t wanted,
                           const uint8_t **payload, uint32_t *size) {
    if (!vm || !payload || !size || vm->status == ABVM_STATUS_FAULT) return false;
    uint32_t cursor = vm->header.constant_offset;
    uint32_t end = cursor + vm->header.constant_size;
    if (end < cursor || end > vm->image_size) return false;
    uint16_t index = 0;
    while (cursor < end) {
        if (cursor + 8u > end) return false;
        uint8_t kind = vm->image[cursor];
        uint32_t length = read_u32_le(vm->image + cursor + 4u);
        cursor += 8u;
        if (length > end - cursor) return false;
        if (index == wanted) {
            if (kind != ABVM_CONST_MOUSE) return false;
            *payload = vm->image + cursor; *size = length; return true;
        }
        uint32_t next = cursor + length;
        if (next > UINT32_MAX - 3u) return false;
        cursor = (next + 3u) & ~3u;
        ++index;
    }
    return false;
}
static bool json_int(const uint8_t *data, uint32_t size, const char *key, int32_t *out) {
    char pattern[24];
    int n = snprintf(pattern, sizeof(pattern), "\"%s\":", key);
    if (n <= 0 || (size_t)n >= sizeof(pattern)) return false;
    for (uint32_t i = 0; i + (uint32_t)n < size; ++i) {
        if (memcmp(data + i, pattern, (size_t)n)) continue;
        uint32_t p = i + (uint32_t)n; bool neg = false;
        if (p < size && data[p] == '-') { neg = true; ++p; }
        if (p >= size || data[p] < '0' || data[p] > '9') return false;
        int32_t value = 0;
        while (p < size && data[p] >= '0' && data[p] <= '9') {
            if (value > 100000) return false;
            value = value * 10 + (int32_t)(data[p++] - '0');
        }
        *out = neg ? -value : value; return true;
    }
    return false;
}
static bool queue_payload(const char *payload, uint32_t now, ArmState next) {
    uint8_t sum = 0;
    for (const char *p = payload; *p; ++p) sum = (uint8_t)(sum + (uint8_t)*p);
    int n = snprintf(tx, sizeof(tx), "#%02X|%s\n", sum, payload);
    if (n <= 0 || (size_t)n >= sizeof(tx)) return false;
    tx_len = (uint8_t)n; tx_pos = 0; rx_len = 0;
    deadline = now + ARM_ACK_TIMEOUT_MS; state = next; return true;
}
static void set_fault(const char *text) {
    fault_text = text; state = ARM_FAULT; tx_len = tx_pos = rx_len = 0;
}
void arm_uart_mouse_init(void) {
    uart_init(ARM_UART, ARM_UART_BAUD);
    gpio_set_function(ARM_UART_TX_PIN, GPIO_FUNC_UART);
    gpio_set_function(ARM_UART_RX_PIN, GPIO_FUNC_UART);
    uart_set_format(ARM_UART, 8, 1, UART_PARITY_NONE);
    uart_set_fifo_enabled(ARM_UART, true); state = ARM_IDLE;
}
ArmMouseSubmit arm_uart_mouse_submit(const AbvmVm *vm, const AbvmEvent *event, uint32_t now) {
    if (!event || event->opcode != ABVM_OP_RMOUSE) return ARM_MOUSE_UNSUPPORTED;
    if (state != ARM_IDLE || completion_pending) return ARM_MOUSE_BUSY;
    const uint8_t *payload; uint32_t size; int32_t w, h;
    if (!mouse_constant(vm, event->operand_a, &payload, &size) ||
        !json_int(payload, size, "w", &w) || !json_int(payload, size, "h", &h) || w <= 0 || h <= 0)
        return ARM_MOUSE_INVALID;
    if (w > ARM_DELTA_LIMIT) w = ARM_DELTA_LIMIT;
    if (h > ARM_DELTA_LIMIT) h = ARM_DELTA_LIMIT;
    int32_t dx = (int32_t)(random_next() % (uint32_t)(w * 2 + 1)) - w;
    int32_t dy = (int32_t)(random_next() % (uint32_t)(h * 2 + 1)) - h;
    if (!dx && !dy) dx = 1;
    char command[40];
    int n = snprintf(command, sizeof(command), "MMOVE|%ld,%ld,rel,2", (long)dx, (long)dy);
    if (n <= 0 || (size_t)n >= sizeof(command) || !queue_payload(command, now, ARM_MOVE))
        return ARM_MOUSE_INVALID;
    lane = event->lane; return ARM_MOUSE_ACCEPTED;
}
static void handle_line(void) {
    rx[rx_len] = 0;
    if (!strncmp(rx, "EVT|", 4)) return;
    if (state == ARM_MOVE && !strcmp(rx, "OK|MMOVE")) {
        completion_lane = lane; completion_pending = true; state = ARM_IDLE; return;
    }
    if (state == ARM_HALT && !strcmp(rx, "OK|HALT")) { state = ARM_IDLE; return; }
    if (!strncmp(rx, "ERR|", 4)) { set_fault("arm error"); return; }
    set_fault("arm malformed reply");
}
bool arm_uart_mouse_service(uint32_t now, uint8_t *completed_lane_out) {
    while (tx_pos < tx_len && uart_is_writable(ARM_UART)) uart_putc_raw(ARM_UART, tx[tx_pos++]);
    if (tx_pos == tx_len) tx_len = tx_pos = 0;
    while (uart_is_readable(ARM_UART)) {
        char c = (char)uart_getc(ARM_UART);
        if (c == '\r') continue;
        if (c == '\n') { if (rx_len) { handle_line(); rx_len = 0; } continue; }
        if (rx_len + 1u >= sizeof(rx)) { set_fault("arm rx overflow"); break; }
        rx[rx_len++] = c;
    }
    if ((state == ARM_MOVE || state == ARM_HALT) && reached(now, deadline)) set_fault("arm ack timeout");
    if (completion_pending && completed_lane_out) {
        *completed_lane_out = completion_lane; completion_pending = false; return true;
    }
    return false;
}
void arm_uart_mouse_release_all(uint32_t now) {
    if (state == ARM_FAULT) return;
    if (state == ARM_MOVE) { completion_lane = lane; completion_pending = true; }
    if (!queue_payload("HALT", now, ARM_HALT)) set_fault("arm halt frame");
}
bool arm_uart_mouse_busy(void) { return state != ARM_IDLE || completion_pending; }
bool arm_uart_mouse_releasing(void) { return state == ARM_HALT; }
bool arm_uart_mouse_faulted(void) { return state == ARM_FAULT; }
const char *arm_uart_mouse_fault(void) { return fault_text; }
