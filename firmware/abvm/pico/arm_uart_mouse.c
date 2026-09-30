#include "arm_uart_mouse.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "hardware/gpio.h"
#include "hardware/uart.h"
#include "calibration_store.h"

#define ARM_UART uart0
#define ARM_UART_BAUD 57600u
#define ARM_UART_TX_PIN 16u
#define ARM_UART_RX_PIN 17u
#define ARM_ACK_TIMEOUT_MS 1500u
#define ARM_LINE_MAX 96u
#define ARM_FRAME_MAX 64u
#define ARM_DELTA_LIMIT 700
#define ARM_RETRY_MAX 2u

typedef enum ArmState { ARM_IDLE, ARM_PROBE, ARM_MOVE, ARM_SOUND_ARM, ARM_SOUND_CAL, ARM_HALT, ARM_FAULT } ArmState;
static ArmState state;
static char tx[ARM_FRAME_MAX];
static char pending_payload[ARM_FRAME_MAX];
static uint8_t tx_len, tx_pos, retry_count;
static char rx[ARM_LINE_MAX];
static uint8_t rx_len, lane, completion_lane, deferred_lane;
static bool completion_pending, deferred_mouse_pending, halt_pending;
static char deferred_mouse[40];
static uint32_t deadline, prng = 0x6d2b79f5u;
static char fault_text[ARM_LINE_MAX];
static char arm_version[24] = "unknown";
static bool arm_ready;
static bool sound_pending, sound_active, sound_event_pending, sound_event_detected;
static uint16_t sound_profile, sound_threshold, sound_minimum, sound_peak;
static bool sound_uses_calibration;
static uint16_t calibration_average, calibration_peak;
static bool calibration_result_pending;
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
static bool queue_frame(const char *payload, uint32_t now, ArmState next, bool fresh) {
    uint8_t sum = 0;
    if (fresh) {
        size_t length = strlen(payload);
        if (length >= sizeof(pending_payload)) return false;
        memcpy(pending_payload, payload, length + 1u);
        retry_count = 0u;
    }
    for (const char *p = payload; *p; ++p) sum = (uint8_t)(sum + (uint8_t)*p);
    int n = snprintf(tx, sizeof(tx), "#%02X|%s\n", sum, payload);
    if (n <= 0 || (size_t)n >= sizeof(tx)) return false;
    tx_len = (uint8_t)n; tx_pos = 0;
    deadline = now + ARM_ACK_TIMEOUT_MS; state = next; return true;
}
static bool queue_payload(const char *payload, uint32_t now, ArmState next) {
    return queue_frame(payload, now, next, true);
}
static bool retry_pending(uint32_t now) {
    if (!pending_payload[0] || retry_count >= ARM_RETRY_MAX) return false;
    ++retry_count;
    return queue_frame(pending_payload, now, state, false);
}
static void set_fault(const char *text) {
    snprintf(fault_text, sizeof(fault_text), "%s", text ? text : "unknown");
    state = ARM_FAULT; tx_len = tx_pos = rx_len = 0;
    sound_pending = sound_active = false;
}
static void set_reply_fault(const char *line) {
    snprintf(fault_text, sizeof(fault_text), "reply=%.*s", (int)(sizeof(fault_text) - 7u), line);
    state = ARM_FAULT; tx_len = tx_pos = rx_len = 0;
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
    arm_ready = false; fault_text[0] = 0; pending_payload[0] = 0;
}
bool arm_uart_mouse_probe(uint32_t now) {
    if (state != ARM_IDLE) return false;
    while (uart_is_readable(ARM_UART)) (void)uart_getc(ARM_UART);
    rx_len = 0u;
    return queue_payload("HVER", now, ARM_PROBE);
}
ArmMouseSubmit arm_uart_mouse_submit(const AbvmVm *vm, const AbvmEvent *event, uint32_t now) {
    if (!event || event->opcode != ABVM_OP_RMOUSE) return ARM_MOUSE_UNSUPPORTED;
    /* One mouse command may wait behind the in-flight UART frame.  This is
     * required when the parallel sound lane is still arming ASND while the
     * motion lane advances.  Keep the queue bounded to one command and never
     * admit work during probe, calibration, halt, or fault handling. */
    if (!arm_ready || state == ARM_FAULT || state == ARM_HALT ||
        state == ARM_PROBE || state == ARM_SOUND_CAL || completion_pending ||
        deferred_mouse_pending || halt_pending)
        return ARM_MOUSE_BUSY;
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
    if (n<=0||(size_t)n>=sizeof(command)) return ARM_MOUSE_INVALID;
    if (state==ARM_IDLE) {
        if (!queue_payload(command,now,ARM_MOVE)) return ARM_MOUSE_INVALID;
        lane=event->lane; return ARM_MOUSE_ACCEPTED;
    }
    if (state==ARM_FAULT||state==ARM_HALT||state==ARM_PROBE||
        completion_pending||deferred_mouse_pending||halt_pending)
        return ARM_MOUSE_BUSY;
    memcpy(deferred_mouse,command,(size_t)n+1u);
    deferred_lane=event->lane; deferred_mouse_pending=true;
    return ARM_MOUSE_ACCEPTED;
}
ArmSoundSubmit arm_uart_sound_arm(const AbvmVm *vm, const AbvmEvent *event, uint32_t now) {
    if (!event || event->type != ABVM_EVENT_WATCH_ARMED || event->flags != 2u)
        return ARM_SOUND_UNSUPPORTED;
    if (!arm_ready || state==ARM_FAULT||state==ARM_HALT||sound_pending||sound_active) return ARM_SOUND_BUSY;
    const uint8_t *payload; uint32_t size;
    if (!abvm_constant(vm,event->constant_id,ABVM_CONST_SOUND,&payload,&size)||size!=8u)
        return ARM_SOUND_INVALID;
    sound_profile=read_u16_le(payload); sound_threshold=read_u16_le(payload+2u);
    sound_minimum=(uint16_t)read_u32_le(payload+4u);
    sound_uses_calibration=false;
    if (!sound_threshold) {
        if (!calibration_store_sound_get(sound_profile,&sound_threshold,&sound_minimum))
            return ARM_SOUND_INVALID;
        sound_uses_calibration=true;
    }
    if (!sound_profile||!sound_threshold||sound_threshold>1023u||!sound_minimum)
        return ARM_SOUND_INVALID;
    sound_deadline=now+(event->operand_b?event->operand_b:1u);
    sound_pending=true;
    if (state==ARM_IDLE&&!queue_sound(now)) return ARM_SOUND_INVALID;
    return ARM_SOUND_ACCEPTED;
}
bool arm_uart_sound_test_start(uint32_t now,uint16_t threshold,
                               uint16_t minimum_ms,uint32_t timeout_ms) {
    if (!arm_ready || state==ARM_FAULT || state==ARM_HALT ||
        state==ARM_PROBE || state==ARM_SOUND_CAL || sound_pending ||
        sound_active || !threshold || threshold>1023u || !minimum_ms ||
        !timeout_ms) return false;
    sound_profile=0u;sound_threshold=threshold;sound_minimum=minimum_ms;
    sound_uses_calibration=false;sound_deadline=now+timeout_ms;
    sound_pending=true;
    if (state==ARM_IDLE&&!queue_sound(now)) {
        sound_pending=false; return false;
    }
    return true;
}
uint16_t arm_uart_sound_threshold(void){return sound_threshold;}
uint16_t arm_uart_sound_minimum(void){return sound_minimum;}
bool arm_uart_sound_uses_calibration(void){return sound_uses_calibration;}
bool arm_uart_sound_calibration_start(uint32_t now, uint16_t duration_ms) {
    if (!arm_ready || state!=ARM_IDLE || sound_pending || sound_active ||
        deferred_mouse_pending || halt_pending || duration_ms<10u || duration_ms>1000u) return false;
    char command[24]; int n=snprintf(command,sizeof(command),"SCAL|%u",duration_ms);
    return n>0&&(size_t)n<sizeof(command)&&queue_payload(command,now+duration_ms,ARM_SOUND_CAL);
}
bool arm_uart_sound_calibration_take(uint16_t *average,uint16_t *peak) {
    if(!calibration_result_pending||!average||!peak)return false;
    *average=calibration_average;*peak=calibration_peak;calibration_result_pending=false;return true;
}
static uint16_t parse_value(const char *line,const char *key) {
    const char *p=strstr(line,key);if(!p)return 0u;unsigned long v=strtoul(p+strlen(key),NULL,10);return v>65535u?65535u:(uint16_t)v;
}
static uint16_t parse_peak(const char *line) {
    const char *p=strstr(line,"|peak=");
    if (!p) return 0;
    unsigned long value=strtoul(p+6,NULL,10);
    return value>65535u?65535u:(uint16_t)value;
}
static bool queue_deferred_mouse(uint32_t now) {
    if (!deferred_mouse_pending) return true;
    if (!queue_payload(deferred_mouse,now,ARM_MOVE)) return false;
    lane=deferred_lane; deferred_mouse_pending=false; return true;
}
static void handle_line(uint32_t now) {
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
    if (state==ARM_PROBE&&!strncmp(rx,"OK|HVER|",8)) {
        const char *value=rx+8; const char *separator=strchr(value,'|');
        size_t length=separator?(size_t)(separator-value):strlen(value);
        if (length>=sizeof(arm_version)) length=sizeof(arm_version)-1u;
        memcpy(arm_version,value,length); arm_version[length]=0;
        if (!strstr(rx,"|REL=1")||!strstr(rx,"|ASND=1")) {
            set_reply_fault("ERR|INCOMPATIBLE|HVER"); return;
        }
        arm_ready=true; pending_payload[0]=0; state=ARM_IDLE; return;
    }
    if (state==ARM_SOUND_CAL&&!strncmp(rx,"OK|SCAL|",8)) {
        calibration_average=parse_value(rx,"|avg=");calibration_peak=parse_value(rx,"|max=");
        calibration_result_pending=true;pending_payload[0]=0;state=ARM_IDLE;return;
    }
    if (state==ARM_MOVE&&!strcmp(rx,"OK|MMOVE")) {
        completion_lane=lane; completion_pending=true; pending_payload[0]=0; state=ARM_IDLE; return;
    }
    if (state==ARM_SOUND_ARM&&!strcmp(rx,"OK|ASND")) {
        pending_payload[0]=0; state=ARM_IDLE; sound_active=true; return;
    }
    if (state==ARM_HALT&&!strcmp(rx,"OK|HALT")) {
        pending_payload[0]=0; state=ARM_IDLE; return;
    }
    if (state==ARM_IDLE&&!strcmp(rx,"OK|HALT")) return;
    if (state==ARM_HALT&&!strncmp(rx,"ERR|ABORTED|",12)) {
        pending_payload[0]=0; state=ARM_IDLE; return;
    }
    if ((!strcmp(rx,"ERR|CKSUM")||!strcmp(rx,"ERR|NOFRAME"))&&retry_pending(now)) return;
    if (!strncmp(rx,"ERR|",4)) { set_reply_fault(rx); return; }
    set_reply_fault(rx);
}

bool arm_uart_mouse_service(uint32_t now, uint8_t *completed_lane_out) {
    while (tx_pos<tx_len&&uart_is_writable(ARM_UART)) uart_putc_raw(ARM_UART,tx[tx_pos++]);
    if (tx_pos==tx_len) tx_len=tx_pos=0;
    while (uart_is_readable(ARM_UART)) {
        char c=(char)uart_getc(ARM_UART);
        if (c=='\r') continue;
        if (c=='\n') { if (rx_len) { handle_line(now); rx_len=0; } continue; }
        if (rx_len+1u>=sizeof(rx)) { set_fault("arm rx overflow"); break; }
        rx[rx_len++]=c;
    }
    if (state!=ARM_IDLE&&state!=ARM_FAULT&&reached(now,deadline)) {
        if (!retry_pending(now)) set_fault("ack-timeout");
    }
    if (state==ARM_IDLE&&halt_pending) {
        halt_pending=false; sound_pending=sound_active=false;
        if (!queue_payload("HALT",now,ARM_HALT)) set_fault("halt-frame");
    }
    if (state==ARM_IDLE&&sound_pending&&!queue_sound(now)) set_fault("sound-frame");
    if (state==ARM_IDLE&&!sound_pending&&deferred_mouse_pending&&
        !queue_deferred_mouse(now)) set_fault("deferred-mouse-frame");
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
    if (state==ARM_FAULT || state==ARM_HALT) return;
    if (state==ARM_PROBE) return;
    if (state==ARM_MOVE) { completion_lane=lane; completion_pending=true; }
    deferred_mouse_pending=false; sound_pending=sound_active=false;
    if (state==ARM_IDLE) {
        if (!queue_payload("HALT",now,ARM_HALT)) set_fault("halt-frame");
    } else halt_pending=true;
}
bool arm_uart_mouse_busy(void) { return state!=ARM_IDLE||completion_pending; }
bool arm_uart_mouse_releasing(void) { return state==ARM_HALT; }
bool arm_uart_sound_active(void) { return sound_pending||sound_active||state==ARM_SOUND_ARM||state==ARM_SOUND_CAL; }
bool arm_uart_mouse_faulted(void) { return state==ARM_FAULT; }
const char *arm_uart_mouse_fault(void) { return fault_text[0] ? fault_text : "none"; }
bool arm_uart_mouse_ready(void) { return arm_ready && state != ARM_FAULT; }
const char *arm_uart_mouse_version(void) { return arm_version; }
