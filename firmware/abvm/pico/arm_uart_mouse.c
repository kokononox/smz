#include "arm_uart_mouse.h"
#include <stdio.h>
#include <stdlib.h>
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

typedef enum ArmState { ARM_IDLE, ARM_MOVE, ARM_SOUND_ARM, ARM_HALT, ARM_FAULT } ArmState;
static ArmState state;
static char tx[ARM_FRAME_MAX];
static uint8_t tx_len, tx_pos;
static char rx[ARM_LINE_MAX];
static uint8_t rx_len, lane, completion_lane;
static bool completion_pending;
static uint32_t deadline, prng = 0x6d2b79f5u;
static const char *fault_text;
static bool sound_pending, sound_active, sound_event_pending, sound_event_detected;
static uint16_t sound_profile, sound_threshold, sound_minimum, sound_peak;
static uint32_t sound_deadline;

static bool reached(uint32_t now, uint32_t due) { return (int32_t)(now - due) >= 0; }
static uint32_t random_next(void) {
    uint32_t x = prng;
    x ^= x << 13; x ^= x >> 17; x ^= x << 5;
    return prng = x;
}
static uint16_t read_u16_le(const uint8_t *p) {
    return (uint16_t)(p[0] | ((uint16_t)p[1] << 8));
}
static uint32_t read_u32_le(const uint8_t *p) {
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) |
           ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
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
    tx_len = (uint8_t)n; tx_pos = 0;
    deadline = now + ARM_ACK_TIMEOUT_MS; state = next; return true;
}
static void set_fault(const char *text) {
    fault_text = text; state = ARM_FAULT; tx_len = tx_pos = rx_len = 0;
    sound_pending = sound_active = false;
}
static bool queue_sound(uint32_t now) {
    if (reached(now, sound_deadline)) {
        sound_pending = false; sound_event_pending = true;
        sound_event_detected = false; sound_peak = 0; return true;
    }
    uint32_t timeout = sound_deadline - now;
    char command[48];
    int n = snprintf(command, sizeof(command), "ASND|%u,%u,%lu",
                     sound_threshold, sound_minimum, (unsigned long)timeout);
    if (n <= 0 || (size_t)n >= sizeof(command) ||
        !queue_payload(command, now, ARM_SOUND_ARM)) return false;
    sound_pending = false; return true;
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
    if (!abvm_constant(vm,event->operand_a,ABVM_CONST_MOUSE,&payload,&size)) return ARM_MOUSE_INVALID;
    if (!json_int(payload,size,"w",&w) || !json_int(payload,size,"h",&h) ||
        w <= 0 || h <= 0) return ARM_MOUSE_INVALID;
    if (w > ARM_DELTA_LIMIT) w = ARM_DELTA_LIMIT;
    if (h > ARM_DELTA_LIMIT) h = ARM_DELTA_LIMIT;
    int32_t dx=(int32_t)(random_next()%(uint32_t)(w*2+1))-w;
    int32_t dy=(int32_t)(random_next()%(uint32_t)(h*2+1))-h;
    if (!dx&&!dy) dx=1;
    char command[40];
    int n=snprintf(command,sizeof(command),"MMOVE|%ld,%ld,rel,2",(long)dx,(long)dy);
    if (n<=0||(size_t)n>=sizeof(command)||!queue_payload(command,now,ARM_MOVE)) return ARM_MOUSE_INVALID;
    lane=event->lane; return ARM_MOUSE_ACCEPTED;
}
ArmSoundSubmit arm_uart_sound_arm(const AbvmVm *vm, const AbvmEvent *event, uint32_t now) {
    if (!event || event->type != ABVM_EVENT_WATCH_ARMED || event->flags != 2u)
        return ARM_SOUND_UNSUPPORTED;
    if (state==ARM_FAULT||state==ARM_HALT||sound_pending||sound_active) return ARM_SOUND_BUSY;
    const uint8_t *payload; uint32_t size;
    if (!abvm_constant(vm,event->constant_id,ABVM_CONST_SOUND,&payload,&size)||size!=8u)
        return ARM_SOUND_INVALID;
    sound_profile=read_u16_le(payload); sound_threshold=read_u16_le(payload+2u);
    sound_minimum=(uint16_t)read_u32_le(payload+4u);
    if (!sound_profile||!sound_threshold||sound_threshold>1023u||!sound_minimum)
        return ARM_SOUND_INVALID;
    sound_deadline=now+(event->operand_b?event->operand_b:1u);
    sound_pending=true;
    if (state==ARM_IDLE&&!queue_sound(now)) return ARM_SOUND_INVALID;
    return ARM_SOUND_ACCEPTED;
}
static uint16_t parse_peak(const char *line) {
    const char *p=strstr(line,"|peak=");
    if (!p) return 0;
    unsigned long value=strtoul(p+6,NULL,10);
    return value>65535u?65535u:(uint16_t)value;
}
static void handle_line(void) {
    rx[rx_len]=0;
    if (!strncmp(rx,"EVT|ASND|DETECTED",17)) {
        sound_peak=parse_peak(rx); sound_event_detected=true;
        sound_event_pending=true; sound_active=false; return;
    }
    if (!strncmp(rx,"EVT|ASND|TIMEOUT",16)) {
        sound_peak=parse_peak(rx); sound_event_detected=false;
        sound_event_pending=true; sound_active=false; return;
    }
    if (!strncmp(rx,"EVT|",4)) return;
    if (state==ARM_MOVE&&!strcmp(rx,"OK|MMOVE")) {
        completion_lane=lane; completion_pending=true; state=ARM_IDLE; return;
    }
    if (state==ARM_SOUND_ARM&&!strcmp(rx,"OK|ASND")) {
        state=ARM_IDLE; sound_active=true; return;
    }
    if (state==ARM_HALT&&!strcmp(rx,"OK|HALT")) { state=ARM_IDLE; return; }
    if (!strncmp(rx,"ERR|",4)) { set_fault("arm error"); return; }
    set_fault("arm malformed reply");
}
bool arm_uart_mouse_service(uint32_t now, uint8_t *completed_lane_out) {
    while (tx_pos<tx_len&&uart_is_writable(ARM_UART)) uart_putc_raw(ARM_UART,tx[tx_pos++]);
    if (tx_pos==tx_len) tx_len=tx_pos=0;
    while (uart_is_readable(ARM_UART)) {
        char c=(char)uart_getc(ARM_UART);
        if (c=='\r') continue;
        if (c=='\n') { if (rx_len) { handle_line(); rx_len=0; } continue; }
        if (rx_len+1u>=sizeof(rx)) { set_fault("arm rx overflow"); break; }
        rx[rx_len++]=c;
    }
    if (state!=ARM_IDLE&&state!=ARM_FAULT&&reached(now,deadline)) set_fault("arm ack timeout");
    if (state==ARM_IDLE&&sound_pending&&!queue_sound(now)) set_fault("arm sound frame");
    if (sound_active&&reached(now,sound_deadline+ARM_ACK_TIMEOUT_MS)) {
        sound_active=false; sound_event_detected=false; sound_event_pending=true; sound_peak=0;
    }
    if (completion_pending&&completed_lane_out) {
        *completed_lane_out=completion_lane; completion_pending=false; return true;
    }
    return false;
}
bool arm_uart_sound_take(uint16_t *profile, bool *detected, uint16_t *peak) {
    if (!sound_event_pending||!profile||!detected||!peak) return false;
    *profile=sound_profile; *detected=sound_event_detected; *peak=sound_peak;
    sound_event_pending=false; return true;
}
void arm_uart_mouse_release_all(uint32_t now) {
    if (state==ARM_FAULT) return;
    if (state==ARM_MOVE) { completion_lane=lane; completion_pending=true; }
    sound_pending=sound_active=false;
    if (!queue_payload("HALT",now,ARM_HALT)) set_fault("arm halt frame");
}
bool arm_uart_mouse_busy(void) { return state!=ARM_IDLE||completion_pending; }
bool arm_uart_mouse_releasing(void) { return state==ARM_HALT; }
bool arm_uart_sound_active(void) { return sound_pending||sound_active||state==ARM_SOUND_ARM; }
bool arm_uart_mouse_faulted(void) { return state==ARM_FAULT; }
const char *arm_uart_mouse_fault(void) { return fault_text; }
