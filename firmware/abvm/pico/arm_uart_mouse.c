
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
#define ARM_FRAME_MAX 96u
#define ARM_RETRY_MAX 2u
#define ARM_LIVE_LANE 0xffu
#define ARM_INTERNAL_LANE 0xfeu
#define HUMAN_SCREEN_DEFAULT_W 1920
#define HUMAN_SCREEN_DEFAULT_H 1080
#define HUMAN_PATH_MAX_STEPS 96u
#define HUMAN_PATH_MIN_STEPS 16u

typedef enum ArmState { ARM_IDLE, ARM_PROBE, ARM_MOVE, ARM_SOUND_ARM, ARM_SOUND_CANCEL, ARM_SOUND_CAL, ARM_HALT, ARM_FAULT } ArmState;
typedef enum HumanPathPhase {
    HUMAN_PATH_IDLE = 0,
    HUMAN_PATH_BEFORE,
    HUMAN_PATH_MOVE,
    HUMAN_PATH_CORRECT,
    HUMAN_PATH_AFTER,
} HumanPathPhase;
typedef struct HumanPath {
    HumanPathPhase phase;
    uint8_t lane;
    uint16_t steps, step, correction_steps;
    int32_t start_x, start_y, target_x, target_y;
    int32_t leg_x, leg_y, control_x, control_y;
    int32_t last_x, last_y;
    uint32_t next_due;
    uint16_t step_delay_ms, after_ms;
    uint16_t mid_pause_ms, mid_pause_step;
} HumanPath;
static ArmState state;
static char tx[ARM_FRAME_MAX];
static char pending_payload[ARM_FRAME_MAX];
static uint8_t tx_len, tx_pos, retry_count;
static char rx[ARM_LINE_MAX];
static uint8_t rx_len, lane, completion_lane, deferred_lane;
static bool completion_pending, deferred_mouse_pending, halt_pending;
static char deferred_mouse[ARM_FRAME_MAX];
static bool live_reply_pending;
static char live_reply[ARM_LINE_MAX];
static uint32_t deadline, prng = 0x6d2b79f5u;
static char fault_text[ARM_LINE_MAX];
static char arm_version[24] = "unknown";
static bool arm_ready;
static bool sound_pending, sound_active, sound_event_pending, sound_event_detected;
static bool sound_result_latched;
static uint16_t sound_profile, sound_threshold, sound_minimum, sound_peak;
static bool sound_uses_calibration;
static uint16_t calibration_average, calibration_peak;
static bool calibration_result_pending;
static uint32_t sound_deadline;
static bool host_usb_seen;
static ArmHostUsbState host_usb_state;
static HumanPath human_path;
static int32_t human_screen_w = HUMAN_SCREEN_DEFAULT_W;
static int32_t human_screen_h = HUMAN_SCREEN_DEFAULT_H;
static int32_t human_virtual_x = HUMAN_SCREEN_DEFAULT_W / 2;
static int32_t human_virtual_y = HUMAN_SCREEN_DEFAULT_H / 2;
static int32_t human_soft_margin_x, human_soft_margin_y;
static bool human_display_configured, human_soft_boundary;
static uint16_t human_moves_since_idle;
static uint16_t human_next_idle;
static uint32_t human_last_speed = 1050u;
static int32_t human_last_curve = 30;
static int32_t human_last_side = 1;

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
static int32_t json_int_or(const uint8_t *data, uint32_t size,
                           const char *key, int32_t fallback) {
    int32_t value;
    return json_int(data, size, key, &value) ? value : fallback;
}
static int32_t clamp_i32(int32_t value, int32_t low, int32_t high) {
    if (value < low) return low;
    if (value > high) return high;
    return value;
}
static int32_t soft_steer_axis(int32_t current,int32_t proposed,
                               int32_t limit,int32_t margin) {
    proposed=clamp_i32(proposed,0,limit-1);
    if(!human_soft_boundary||margin<=0)return proposed;
    int32_t low=margin,high=limit-1-margin;
    if(low>=high)return limit/2;
    uint32_t jitter_max=(uint32_t)(margin/3);
    int32_t jitter=(int32_t)(jitter_max?
        random_next()%(jitter_max+1u):0u);
    if(proposed<low) {
        int32_t penetration=low-proposed;
        return clamp_i32(low+penetration+jitter,low,high);
    }
    if(proposed>high) {
        int32_t penetration=proposed-high;
        return clamp_i32(high-penetration-jitter,low,high);
    }
    if(current<low&&proposed<=current)
        return clamp_i32(low+(low-current)+jitter,low,high);
    if(current>high&&proposed>=current)
        return clamp_i32(high-(current-high)-jitter,low,high);
    return proposed;
}
static uint32_t random_range_u32(uint32_t low, uint32_t high) {
    if (high <= low) return low;
    return low + random_next() % (high - low + 1u);
}
static uint32_t random_triangular_u32(uint32_t maximum) {
    if (!maximum) return 0u;
    return ((random_next() % (maximum + 1u)) +
            (random_next() % (maximum + 1u))) / 2u;
}
static uint32_t integer_log2_u32(uint32_t value) {
    uint32_t result = 0u;
    while (value > 1u) { value >>= 1; ++result; }
    return result;
}
static bool json_hand_signature(const uint8_t *data, uint32_t size,
                                uint32_t *signature, uint16_t *tempo_ms) {
    int32_t compact_signature, compact_tempo;
    if (json_int(data,size,"handSignature",&compact_signature) &&
        json_int(data,size,"handTempoMs",&compact_tempo) &&
        compact_signature>=0 && compact_signature<=65535 &&
        compact_tempo>=2 && compact_tempo<=20) {
        *signature=(uint32_t)compact_signature;
        *tempo_ms=(uint16_t)compact_tempo;
        return true;
    }
    static const char marker[] = "\"handSample\":\"";
    const uint32_t marker_size = (uint32_t)(sizeof(marker) - 1u);
    for (uint32_t i = 0; i + marker_size < size; ++i) {
        if (memcmp(data + i, marker, marker_size)) continue;
        uint32_t p = i + marker_size, hash = 2166136261u;
        uint32_t total_dt = 0u, samples = 0u;
        uint8_t pipes = 0u;
        while (p < size && data[p] != '"') {
            uint8_t c = data[p++];
            hash = (hash ^ c) * 16777619u;
            if (c == '|') { ++pipes; continue; }
            if (pipes < 3u || c < '0' || c > '9') continue;
            uint32_t value = (uint32_t)(c - '0');
            while (p < size && data[p] >= '0' && data[p] <= '9')
                value = value * 10u + (uint32_t)(data[p++] - '0');
            if (p < size && data[p] == ',') {
                total_dt += value > 100u ? 100u : value;
                ++samples;
                while (p < size && data[p] != ';' && data[p] != '"') ++p;
            }
        }
        if (!samples) return false;
        *signature = hash;
        uint32_t average = total_dt / samples;
        *tempo_ms = (uint16_t)clamp_i32((int32_t)average, 2, 20);
        return true;
    }
    return false;
}
static uint32_t isqrt_u32(uint32_t value) {
    uint32_t result = 0u;
    uint32_t bit = 1u << 30;
    while (bit > value) bit >>= 2;
    while (bit) {
        if (value >= result + bit) {
            value -= result + bit;
            result = (result >> 1) + bit;
        } else result >>= 1;
        bit >>= 2;
    }
    return result;
}
static int32_t q16_bezier(int32_t end, int32_t control, uint32_t t) {
    uint32_t u = 65536u - t;
    int64_t value = (int64_t)2u * u * t * control +
                    (int64_t)t * t * end;
    return (int32_t)(value >> 32);
}
static uint32_t smooth_q16(uint16_t step, uint16_t steps) {
    uint32_t t = ((uint32_t)step << 16) / steps;
    uint64_t t2 = ((uint64_t)t * t) >> 16;
    return (uint32_t)((t2 * (196608u - 2u * t)) >> 16);
}
static void human_leg(int32_t end_x, int32_t end_y, uint16_t steps,
                      int32_t curve_pct) {
    int64_t distance2 = (int64_t)end_x * end_x + (int64_t)end_y * end_y;
    uint32_t distance = isqrt_u32(
        distance2 > UINT32_MAX ? UINT32_MAX : (uint32_t)distance2);
    int32_t height = (int32_t)((distance *
        (uint32_t)(12 + clamp_i32(curve_pct, 0, 200) * 3)) / 1000u);
    if (height < 2 && distance > 20u) height = 2;
    int32_t side;
    if ((random_next() % 100u) < 68u) side = human_last_side;
    else side = -human_last_side;
    human_last_side = side;
    int32_t perpendicular_x = distance ?
        (int32_t)((-(int64_t)end_y * height * side) / distance) : 0;
    int32_t perpendicular_y = distance ?
        (int32_t)(((int64_t)end_x * height * side) / distance) : 0;
    human_path.leg_x = end_x;
    human_path.leg_y = end_y;
    human_path.control_x = end_x / 2 + perpendicular_x;
    human_path.control_y = end_y / 2 + perpendicular_y;
    if(human_soft_boundary) {
        int32_t global_x=human_virtual_x+human_path.control_x;
        int32_t global_y=human_virtual_y+human_path.control_y;
        human_path.control_x=clamp_i32(global_x,human_soft_margin_x,
            human_screen_w-1-human_soft_margin_x)-human_virtual_x;
        human_path.control_y=clamp_i32(global_y,human_soft_margin_y,
            human_screen_h-1-human_soft_margin_y)-human_virtual_y;
    }
    human_path.last_x = human_path.last_y = 0;
    human_path.steps = steps;
    human_path.step = 0u;
}
static bool human_path_command(char *command, size_t capacity) {
    if (human_path.phase != HUMAN_PATH_MOVE &&
        human_path.phase != HUMAN_PATH_CORRECT) return false;
    if (human_path.step >= human_path.steps) return false;
    uint16_t next = (uint16_t)(human_path.step + 1u);
    uint32_t t = smooth_q16(next, human_path.steps);
    int32_t x = q16_bezier(human_path.leg_x, human_path.control_x, t);
    int32_t y = q16_bezier(human_path.leg_y, human_path.control_y, t);
    int32_t dx = x - human_path.last_x;
    int32_t dy = y - human_path.last_y;
    human_path.last_x = x; human_path.last_y = y; human_path.step = next;
    if (!dx && !dy && next < human_path.steps)
        return human_path_command(command, capacity);
    int n = snprintf(command, capacity, "MMOVE|%ld,%ld,rel,2",
                     (long)dx, (long)dy);
    return n > 0 && (size_t)n < capacity;
}
static bool human_path_begin(const uint8_t *payload, uint32_t size,
                             uint8_t path_lane, uint32_t now) {
    int32_t x=0, y=0, w=0, h=0;
    int32_t screen_w=clamp_i32(
        json_int_or(payload,size,"screenWidth",HUMAN_SCREEN_DEFAULT_W),640,7680);
    int32_t screen_h=clamp_i32(
        json_int_or(payload,size,"screenHeight",HUMAN_SCREEN_DEFAULT_H),480,4320);
    int32_t margin_pct=clamp_i32(
        json_int_or(payload,size,"softMarginPct",3),1,20);
    if(!human_display_configured) {
        human_virtual_x=screen_w/2;human_virtual_y=screen_h/2;
        human_display_configured=true;
    } else if(screen_w!=human_screen_w||screen_h!=human_screen_h) {
        human_virtual_x=(int32_t)((int64_t)human_virtual_x*screen_w/human_screen_w);
        human_virtual_y=(int32_t)((int64_t)human_virtual_y*screen_h/human_screen_h);
    }
    human_screen_w=screen_w;human_screen_h=screen_h;
    human_soft_boundary=json_int_or(payload,size,"softBoundary",0)==1;
    human_soft_margin_x=human_soft_boundary?
        (screen_w*margin_pct/100>8?screen_w*margin_pct/100:8):0;
    human_soft_margin_y=human_soft_boundary?
        (screen_h*margin_pct/100>8?screen_h*margin_pct/100:8):0;
    human_virtual_x=clamp_i32(human_virtual_x,0,screen_w-1);
    human_virtual_y=clamp_i32(human_virtual_y,0,screen_h-1);
    int32_t relative_mode=json_int_or(payload,size,"relativeMode",0);
    int32_t target_x,target_y;
    uint32_t target_width;
    if(relative_mode==1||relative_mode==2) {
        int32_t radius_min=clamp_i32(
            json_int_or(payload,size,"relativeMin",2),1,700);
        int32_t radius_max=clamp_i32(
            json_int_or(payload,size,"relativeMax",12),1,700);
        if(relative_mode==2) {
            int32_t micro=clamp_i32(
                json_int_or(payload,size,"handMicroPct",45),0,100);
            int32_t medium=clamp_i32(
                json_int_or(payload,size,"handMediumPct",40),0,100);
            int32_t roll=(int32_t)(random_next()%100u);
            if(roll<micro) {
                radius_min=2;radius_max=14;
            } else if(roll<micro+medium) {
                radius_min=20;radius_max=90;
            } else {
                int32_t personal=clamp_i32(
                    json_int_or(payload,size,"handBurstP50Px",300),120,700);
                radius_min=90;radius_max=personal;
            }
        }
        if(radius_max<radius_min){
            int32_t swap=radius_min;radius_min=radius_max;radius_max=swap;
        }
        int32_t rx=0,ry=0;uint32_t radial=0u;
        for(uint8_t attempt=0u;attempt<12u;++attempt){
            rx=(int32_t)random_range_u32(0u,(uint32_t)(radius_max*2))-radius_max;
            ry=(int32_t)random_range_u32(0u,(uint32_t)(radius_max*2))-radius_max;
            radial=isqrt_u32((uint32_t)((int64_t)rx*rx+(int64_t)ry*ry));
            if(radial>=(uint32_t)radius_min&&radial<=(uint32_t)radius_max)break;
        }
        if(radial<(uint32_t)radius_min||radial>(uint32_t)radius_max){
            rx=radius_min;ry=0;
        }
        target_x=soft_steer_axis(human_virtual_x,human_virtual_x+rx,
            screen_w,human_soft_margin_x);
        target_y=soft_steer_axis(human_virtual_y,human_virtual_y+ry,
            screen_h,human_soft_margin_y);
        target_width=(uint32_t)(radius_max-radius_min+1);
    } else {
        if (!json_int(payload,size,"x",&x) || !json_int(payload,size,"y",&y) ||
            !json_int(payload,size,"w",&w) || !json_int(payload,size,"h",&h) ||
            x < 0 || y < 0 || w <= 0 || h <= 0 ||
            x > screen_w - w || y > screen_h - h)
            return false;
        /* Two uniform samples create a center-weighted triangular distribution:
         * ordinary human aim lands away from hard region edges most of the time. */
        target_x=x+(int32_t)random_triangular_u32((uint32_t)w-1u);
        target_y=y+(int32_t)random_triangular_u32((uint32_t)h-1u);
        target_x=soft_steer_axis(human_virtual_x,target_x,
            screen_w,human_soft_margin_x);
        target_y=soft_steer_axis(human_virtual_y,target_y,
            screen_h,human_soft_margin_y);
        target_width=(uint32_t)(w<h?w:h);
    }
    int32_t dx = target_x - human_virtual_x;
    int32_t dy = target_y - human_virtual_y;
    uint32_t distance = isqrt_u32((uint32_t)((int64_t)dx * dx +
                                              (int64_t)dy * dy));
    if (!distance) { dx = 1; target_x++; distance = 1u; }

    int32_t curve_min = clamp_i32(
        json_int_or(payload,size,"curveMinPct",15), 0, 200);
    int32_t curve_max = clamp_i32(
        json_int_or(payload,size,"curveMaxPct",45), 0, 200);
    if (curve_max < curve_min) {
        int32_t swap = curve_min; curve_min = curve_max; curve_max = swap;
    }
    int32_t sampled_curve = curve_min + (int32_t)random_range_u32(
        0u, (uint32_t)(curve_max - curve_min));
    int32_t curve=(human_last_curve*2+sampled_curve)/3;
    human_last_curve=curve;
    uint32_t hand_signature=0u;uint16_t hand_tempo_ms=0u;
    if(json_hand_signature(payload,size,&hand_signature,&hand_tempo_ms)) {
        /* The recorded hand trace is consumed locally as a stable movement
         * signature. It nudges curve/rhythm without storing or replaying a
         * large point list, keeping the Native actor allocation-free. */
        prng^=hand_signature+(uint32_t)human_moves_since_idle*0x9e3779b9u;
        int32_t signed_bias=(int32_t)(
            (hand_signature^(hand_signature>>16))&15u)-7;
        curve=clamp_i32(curve+signed_bias,curve_min,curve_max);
    }
    if(json_int_or(payload,size,"handProfileV2",0)==1) {
        /* A low displacement/path ratio means the recorded hand reached its
         * target through a wider arc. Preserve that personal geometry without
         * replaying identifiable points or changing the authored destination. */
        int32_t efficiency=clamp_i32(
            json_int_or(payload,size,"handEfficiencyPct",75),5,100);
        int32_t personal_boost=(100-efficiency)/2;
        curve=clamp_i32(curve+personal_boost,curve_min,
            clamp_i32(curve_max+personal_boost,curve_max,200));
    }
    int32_t move_min = clamp_i32(
        json_int_or(payload,size,"moveTimeMin",0), 0, 30000);
    int32_t move_max = clamp_i32(
        json_int_or(payload,size,"moveTimeMax",0), 0, 30000);
    if (move_max < move_min) {
        int32_t swap = move_min; move_min = move_max; move_max = swap;
    }
    uint32_t duration;
    if (move_max > 0) duration = random_range_u32(
        (uint32_t)(move_min > 0 ? move_min : 1), (uint32_t)move_max);
    else {
        int32_t profile_speed_min=clamp_i32(
            json_int_or(payload,size,"handSpeedMin",700),150,3000);
        int32_t profile_speed_max=clamp_i32(
            json_int_or(payload,size,"handSpeedMax",1600),150,3000);
        if(profile_speed_max<profile_speed_min){
            int32_t swap=profile_speed_min;profile_speed_min=profile_speed_max;
            profile_speed_max=swap;
        }
        uint32_t sampled_speed = random_range_u32(
            (uint32_t)profile_speed_min,(uint32_t)profile_speed_max);
        uint32_t speed = (human_last_speed * 2u + sampled_speed) / 3u;
        human_last_speed=speed;
        duration = distance * 1000u / speed;
        /* Integer Fitts-style cost: small/far targets take longer to acquire,
         * while large nearby regions stay fluid. */
        if(target_width<12u)target_width=12u;
        uint32_t difficulty=integer_log2_u32(
            1u+(distance*64u)/target_width);
        duration+=difficulty*42u;
    }
    duration = (uint32_t)clamp_i32((int32_t)duration, 120, 3000);
    uint16_t steps = (uint16_t)clamp_i32(
        (int32_t)((distance + 3u) / 4u),
        HUMAN_PATH_MIN_STEPS, HUMAN_PATH_MAX_STEPS);

    memset(&human_path, 0, sizeof(human_path));
    human_path.phase = HUMAN_PATH_BEFORE;
    human_path.lane = path_lane;
    human_path.start_x = human_virtual_x;
    human_path.start_y = human_virtual_y;
    human_path.target_x = target_x;
    human_path.target_y = target_y;
    human_path.step_delay_ms = (uint16_t)clamp_i32(
        (int32_t)(duration / steps), 2, 40);
    if(hand_tempo_ms)
        human_path.step_delay_ms=(uint16_t)clamp_i32(
            (human_path.step_delay_ms*3+hand_tempo_ms)/4,2,40);
    int32_t before_min = clamp_i32(
        json_int_or(payload,size,"pauseBeforeMin",120), 0, 30000);
    int32_t before_max = clamp_i32(
        json_int_or(payload,size,"pauseBeforeMax",450), 0, 30000);
    if (before_max < before_min) {
        int32_t swap=before_min; before_min=before_max; before_max=swap;
    }
    int32_t after_min = clamp_i32(
        json_int_or(payload,size,"pauseAfterMin",150), 0, 30000);
    int32_t after_max = clamp_i32(
        json_int_or(payload,size,"pauseAfterMax",600), 0, 30000);
    if (after_max < after_min) {
        int32_t swap=after_min; after_min=after_max; after_max=swap;
    }
    human_path.next_due = now + random_range_u32(
        (uint32_t)before_min, (uint32_t)before_max);
    human_path.after_ms = (uint16_t)random_range_u32(
        (uint32_t)after_min, (uint32_t)after_max);
    int32_t idle_every_min=clamp_i32(
        json_int_or(payload,size,"idleEveryMin",5),1,1000);
    int32_t idle_every_max=clamp_i32(
        json_int_or(payload,size,"idleEveryMax",12),1,1000);
    if(idle_every_max<idle_every_min){
        int32_t swap=idle_every_min;idle_every_min=idle_every_max;
        idle_every_max=swap;
    }
    if(!human_next_idle)human_next_idle=(uint16_t)random_range_u32(
        (uint32_t)idle_every_min,(uint32_t)idle_every_max);
    if(++human_moves_since_idle>=human_next_idle){
        int32_t idle_min=clamp_i32(
            json_int_or(payload,size,"idlePauseMin",800),0,30000);
        int32_t idle_max=clamp_i32(
            json_int_or(payload,size,"idlePauseMax",3000),0,30000);
        if(idle_max<idle_min){int32_t swap=idle_min;idle_min=idle_max;idle_max=swap;}
        uint32_t total=(uint32_t)human_path.after_ms+
            random_range_u32((uint32_t)idle_min,(uint32_t)idle_max);
        human_path.after_ms=(uint16_t)(total>60000u?60000u:total);
        human_moves_since_idle=0u;
        human_next_idle=(uint16_t)random_range_u32(
            (uint32_t)idle_every_min,(uint32_t)idle_every_max);
    }

    int32_t mid_chance=clamp_i32(
        json_int_or(payload,size,"midPauseChance",12),0,100);
    if ((int32_t)(random_next()%100u) < mid_chance) {
        int32_t mid_min=clamp_i32(
            json_int_or(payload,size,"midPauseMin",100),0,30000);
        int32_t mid_max=clamp_i32(
            json_int_or(payload,size,"midPauseMax",500),0,30000);
        if(mid_max<mid_min){int32_t swap=mid_min;mid_min=mid_max;mid_max=swap;}
        human_path.mid_pause_ms=(uint16_t)random_range_u32(
            (uint32_t)mid_min,(uint32_t)mid_max);
        human_path.mid_pause_step=(uint16_t)random_range_u32(
            steps/3u,(steps*2u)/3u);
    }
    int32_t over_chance=clamp_i32(
        json_int_or(payload,size,"overshootChance",15),0,100);
    if(json_int_or(payload,size,"handProfileV2",0)==1) {
        int32_t personal_correction=clamp_i32(
            json_int_or(payload,size,"handCorrectionPct",0),0,100);
        if(personal_correction>over_chance)
            over_chance=personal_correction;
    }
    if(distance>=80u&&(int32_t)(random_next()%100u)<over_chance) {
        uint32_t extra=random_range_u32(3u,20u);
        human_path.target_x=target_x;
        human_path.target_y=target_y;
        human_path.correction_steps=(uint16_t)clamp_i32(
            (int32_t)(extra/2u),4,12);
        dx += (int32_t)((int64_t)dx*extra/distance);
        dy += (int32_t)((int64_t)dy*extra/distance);
    }
    if(human_soft_boundary) {
        dx=clamp_i32(human_virtual_x+dx,human_soft_margin_x,
            screen_w-1-human_soft_margin_x)-human_virtual_x;
        dy=clamp_i32(human_virtual_y+dy,human_soft_margin_y,
            screen_h-1-human_soft_margin_y)-human_virtual_y;
    }
    human_leg(dx,dy,steps,curve);
    return true;
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
    live_reply_pending=false;live_reply[0]=0;
    sound_result_latched=false;
    host_usb_seen=false;host_usb_state=ARM_HOST_USB_UNKNOWN;
    memset(&human_path,0,sizeof(human_path));
    human_screen_w=HUMAN_SCREEN_DEFAULT_W;
    human_screen_h=HUMAN_SCREEN_DEFAULT_H;
    human_virtual_x=human_screen_w/2;human_virtual_y=human_screen_h/2;
    human_display_configured=false;human_soft_boundary=false;
    human_soft_margin_x=human_soft_margin_y=0;
    human_moves_since_idle=human_next_idle=0u;
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
    const uint8_t *payload; uint32_t size;
    if (!abvm_constant(vm,event->operand_a,ABVM_CONST_MOUSE,&payload,&size)) return ARM_MOUSE_INVALID;
    if(human_path.phase!=HUMAN_PATH_IDLE)return ARM_MOUSE_BUSY;
    if(!human_path_begin(payload,size,event->lane,now))return ARM_MOUSE_INVALID;
    return ARM_MOUSE_ACCEPTED;
}
bool arm_uart_mouse_ambient_config(const AbvmVm *vm,uint16_t *constant_id,
    uint8_t *environment_mask,uint32_t *interval_min_ms,
    uint32_t *interval_max_ms) {
    const uint8_t *payload;uint32_t size;uint16_t id;
    if(!vm||!constant_id||!environment_mask||!interval_min_ms||
       !interval_max_ms||
       !abvm_find_constant(vm,ABVM_CONST_MOUSE,&id,&payload,&size)||
       json_int_or(payload,size,"ambientProfile",0)!=1)
        return false;
    int32_t mask=clamp_i32(
        json_int_or(payload,size,"ambientEnvironmentMask",0),0,255);
    int32_t low=clamp_i32(
        json_int_or(payload,size,"ambientIntervalMinMs",500),250,10000);
    int32_t high=clamp_i32(
        json_int_or(payload,size,"ambientIntervalMaxMs",1800),250,10000);
    if(!mask)return false;
    if(high<low){int32_t swap=low;low=high;high=swap;}
    *constant_id=id;*environment_mask=(uint8_t)mask;
    *interval_min_ms=(uint32_t)low;*interval_max_ms=(uint32_t)high;
    return true;
}
ArmMouseSubmit arm_uart_mouse_submit_ambient(const AbvmVm *vm,
    uint16_t constant_id,uint32_t now) {
    if(!vm||!arm_ready||state==ARM_FAULT||state==ARM_HALT||
       state==ARM_PROBE||state==ARM_SOUND_CAL||completion_pending||
       deferred_mouse_pending||halt_pending||
       human_path.phase!=HUMAN_PATH_IDLE||state!=ARM_IDLE)
        return ARM_MOUSE_BUSY;
    const uint8_t *payload;uint32_t size;
    if(!abvm_constant(vm,constant_id,ABVM_CONST_MOUSE,&payload,&size)||
       json_int_or(payload,size,"ambientProfile",0)!=1)
        return ARM_MOUSE_INVALID;
    return human_path_begin(payload,size,ARM_INTERNAL_LANE,now)?
        ARM_MOUSE_ACCEPTED:ARM_MOUSE_INVALID;
}
bool arm_uart_mouse_internal_completion(uint8_t completed_lane) {
    return completed_lane==ARM_INTERNAL_LANE;
}
static ArmMouseSubmit submit_direct(const char *command, uint32_t now,
                                    uint8_t direct_lane) {
    if (!command ||
        (strncmp(command, "MMOVE|", 6u) &&
         strncmp(command, "MCLICK|", 7u) &&
         strncmp(command, "MWHEEL|", 7u) &&
         strncmp(command, "MDOWN|", 6u) &&
         strncmp(command, "MUP|", 4u) &&
         strncmp(command, "KCOMBO|", 7u) &&
         strncmp(command, "KDOWN|", 6u) &&
         strncmp(command, "KUP|", 4u) &&
         strncmp(command, "KTEXT|", 6u)) ||
        strlen(command) >= sizeof(deferred_mouse))
        return ARM_MOUSE_INVALID;
    /* ARM 2.8 latches a detected ASND result and ACKs later MMOVEs without
     * moving until ASNDCANCEL/rearm.  Classroom live preview must be
     * independent of that completed runtime watch.  Cancel only the latched
     * result, then run the command through the existing bounded defer slot. */
    if (direct_lane == ARM_LIVE_LANE && sound_result_latched) {
        if (!arm_ready || completion_pending || state != ARM_IDLE ||
            deferred_mouse_pending || halt_pending)
            return ARM_MOUSE_BUSY;
        memcpy(deferred_mouse, command, strlen(command) + 1u);
        deferred_lane=direct_lane;deferred_mouse_pending=true;
        sound_result_latched=false;
        if (!queue_payload("ASNDCANCEL",now,ARM_SOUND_CANCEL)) {
            deferred_mouse_pending=false;
            return ARM_MOUSE_INVALID;
        }
        return ARM_MOUSE_ACCEPTED;
    }
    if (!arm_ready || state == ARM_FAULT || state == ARM_HALT ||
        state == ARM_PROBE || state == ARM_SOUND_CAL ||
        completion_pending || halt_pending)
        return ARM_MOUSE_BUSY;
    if (state == ARM_IDLE) {
        if (!queue_payload(command, now, ARM_MOVE)) return ARM_MOUSE_INVALID;
        lane = direct_lane;
        return ARM_MOUSE_ACCEPTED;
    }
    if (state != ARM_MOVE) return ARM_MOUSE_BUSY;
    /* Classroom Studio streams absolute path points faster than the arm's
     * acknowledgement cadence. Keep one bounded pending point and coalesce it
     * to the newest target instead of overflowing UART or blocking USB CDC. */
    memcpy(deferred_mouse, command, strlen(command) + 1u);
    deferred_lane = direct_lane;
    deferred_mouse_pending = true;
    return ARM_MOUSE_ACCEPTED;
}
ArmMouseSubmit arm_uart_mouse_submit_live(const char *command, uint32_t now) {
    return submit_direct(command,now,ARM_LIVE_LANE);
}
ArmMouseSubmit arm_uart_mouse_submit_internal(const char *command, uint32_t now) {
    return submit_direct(command,now,ARM_INTERNAL_LANE);
}
bool arm_uart_mouse_take_live_reply(char *reply, size_t capacity) {
    if (!live_reply_pending || !reply || capacity == 0u) return false;
    snprintf(reply, capacity, "%s", live_reply);
    live_reply_pending=false;live_reply[0]=0;
    return true;
}
ArmSoundSubmit arm_uart_sound_arm(const AbvmVm *vm, const AbvmEvent *event, uint32_t now) {
    if (!event || event->type != ABVM_EVENT_WATCH_ARMED || event->flags != 2u)
        return ARM_SOUND_UNSUPPORTED;
    if (!arm_ready || state==ARM_FAULT||state==ARM_HALT||sound_pending||sound_active) return ARM_SOUND_BUSY;
    const uint8_t *payload; uint32_t size;
    if (!abvm_constant(vm,event->constant_id,ABVM_CONST_SOUND,&payload,&size)||size!=8u)
        return ARM_SOUND_INVALID;
    sound_profile=read_u16_le(payload); sound_threshold=read_u16_le(payload+2u);
    sound_result_latched=false;
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
    sound_result_latched=false;
    sound_uses_calibration=false;sound_deadline=now+timeout_ms;
    sound_pending=true;
    if (state==ARM_IDLE&&!queue_sound(now)) {
        sound_pending=false; return false;
    }
    return true;
}
bool arm_uart_sound_restart(uint32_t now,uint16_t profile,
                            uint16_t threshold,uint16_t minimum_ms,
                            uint32_t timeout_ms) {
    if (!arm_ready || state!=ARM_IDLE || sound_pending || sound_active ||
        deferred_mouse_pending || halt_pending || !profile || !minimum_ms ||
        !timeout_ms) return false;
    sound_uses_calibration=false;
    if (!threshold) {
        if (!calibration_store_sound_get(profile,&threshold,&minimum_ms))
            return false;
        sound_uses_calibration=true;
    }
    if (!threshold || threshold>1023u) return false;
    sound_profile=profile;sound_threshold=threshold;sound_minimum=minimum_ms;
    sound_result_latched=false;
    sound_deadline=now+timeout_ms;sound_pending=true;
    if (!queue_sound(now)) {
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
    if (!strcmp(rx,"EVT|HOSTUSB|DOWN")) {
        host_usb_seen=true;host_usb_state=ARM_HOST_USB_DOWN;return;
    }
    if (!strcmp(rx,"EVT|HOSTUSB|SUSPEND")) {
        host_usb_seen=true;host_usb_state=ARM_HOST_USB_SUSPEND;return;
    }
    if (!strcmp(rx,"EVT|HOSTUSB|UP")) {
        host_usb_seen=true;host_usb_state=ARM_HOST_USB_UP;return;
    }
    if (!strncmp(rx,"EVT|ASND|DETECTED",17)) {
        sound_peak=parse_peak(rx); sound_event_detected=true;
        sound_event_pending=true; sound_active=false;
        sound_result_latched=true; return;
    }
    if (!strncmp(rx,"EVT|ASND|TIMEOUT",16)) {
        sound_peak=parse_peak(rx); sound_event_detected=false;
        sound_event_pending=true; sound_active=false;
        sound_result_latched=false; return;
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
    if (state==ARM_MOVE&&!strncmp(rx,"OK|",3)) {
        if (lane == ARM_LIVE_LANE || lane == ARM_INTERNAL_LANE) {
            /* Dense MMOVE is write-only at the PC bridge. Other live actions
             * wait for the arm acknowledgement and receive it through CDC. */
            if (lane==ARM_LIVE_LANE&&strncmp(pending_payload,"MMOVE|",6u)) {
                snprintf(live_reply,sizeof(live_reply),"%s",rx);
                live_reply_pending=true;
            }
        } else if (!strcmp(rx,"OK|MMOVE")&&
                   human_path.phase!=HUMAN_PATH_IDLE&&
                   lane==human_path.lane) {
            pending_payload[0]=0;state=ARM_IDLE;
            if(human_path.step>=human_path.steps) {
                if(human_path.phase==HUMAN_PATH_MOVE&&
                   human_path.correction_steps) {
                    int32_t current_x=human_path.start_x+human_path.leg_x;
                    int32_t current_y=human_path.start_y+human_path.leg_y;
                    int32_t correction_x=human_path.target_x-current_x;
                    int32_t correction_y=human_path.target_y-current_y;
                    uint16_t correction_steps=human_path.correction_steps;
                    human_path.phase=HUMAN_PATH_CORRECT;
                    human_path.correction_steps=0u;
                    human_leg(correction_x,correction_y,correction_steps,8);
                    human_path.next_due=now+random_range_u32(70u,160u);
                } else {
                    human_virtual_x=human_path.target_x;
                    human_virtual_y=human_path.target_y;
                    human_path.phase=HUMAN_PATH_AFTER;
                    human_path.next_due=now+human_path.after_ms;
                }
            } else {
                uint32_t pause=human_path.step_delay_ms;
                if(human_path.mid_pause_ms&&
                   human_path.step>=human_path.mid_pause_step) {
                    pause+=human_path.mid_pause_ms;
                    human_path.mid_pause_ms=0u;
                }
                human_path.next_due=now+pause;
            }
            return;
        } else if (!strcmp(rx,"OK|MMOVE")) {
            completion_lane=lane; completion_pending=true;
        } else { set_reply_fault(rx); return; }
        pending_payload[0]=0; state=ARM_IDLE; return;
    }
    if (state==ARM_SOUND_ARM&&!strcmp(rx,"OK|ASND")) {
        pending_payload[0]=0; state=ARM_IDLE; sound_active=true; return;
    }
    if (state==ARM_SOUND_CANCEL&&!strcmp(rx,"OK|ASNDCANCEL")) {
        pending_payload[0]=0;state=ARM_IDLE;return;
    }
    if (state==ARM_HALT&&!strcmp(rx,"OK|HALT")) {
        pending_payload[0]=0; state=ARM_IDLE; return;
    }
    if (state==ARM_IDLE&&!strcmp(rx,"OK|HALT")) return;
    if (state==ARM_HALT&&!strncmp(rx,"ERR|ABORTED|",12)) {
        pending_payload[0]=0; state=ARM_IDLE; return;
    }
    if ((!strcmp(rx,"ERR|CKSUM")||!strcmp(rx,"ERR|NOFRAME"))&&retry_pending(now)) return;
    if (!strncmp(rx,"ERR|",4)) {
        if (state==ARM_MOVE&&
           (lane==ARM_LIVE_LANE||lane==ARM_INTERNAL_LANE)) {
            if(lane==ARM_LIVE_LANE){
                snprintf(live_reply,sizeof(live_reply),"%s",rx);
                live_reply_pending=true;
            }
            pending_payload[0]=0;state=ARM_IDLE;return;
        }
        set_reply_fault(rx); return;
    }
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
    if(state==ARM_IDLE&&!sound_pending&&!deferred_mouse_pending&&
       human_path.phase!=HUMAN_PATH_IDLE&&reached(now,human_path.next_due)) {
        if(human_path.phase==HUMAN_PATH_BEFORE)
            human_path.phase=HUMAN_PATH_MOVE;
        if(human_path.phase==HUMAN_PATH_AFTER) {
            completion_lane=human_path.lane;completion_pending=true;
            human_path.phase=HUMAN_PATH_IDLE;
        } else {
            char command[40];
            if(!human_path_command(command,sizeof(command))||
               !queue_payload(command,now,ARM_MOVE))
                set_fault("human-mouse-frame");
            else lane=human_path.lane;
        }
    }
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
    if (state==ARM_MOVE && lane!=ARM_LIVE_LANE &&
       lane!=ARM_INTERNAL_LANE) {
        completion_lane=lane; completion_pending=true;
    }
    if(human_path.phase!=HUMAN_PATH_IDLE) {
        completion_lane=human_path.lane;completion_pending=true;
        human_path.phase=HUMAN_PATH_IDLE;
    }
    deferred_mouse_pending=false; sound_pending=sound_active=false;
    sound_result_latched=false;
    if (state==ARM_IDLE) {
        if (!queue_payload("HALT",now,ARM_HALT)) set_fault("halt-frame");
    } else halt_pending=true;
}
void arm_uart_mouse_discard_completion(void){completion_pending=false;}
bool arm_uart_mouse_busy(void) {
    return state!=ARM_IDLE||completion_pending||
           human_path.phase!=HUMAN_PATH_IDLE;
}
bool arm_uart_mouse_releasing(void) { return state==ARM_HALT; }
bool arm_uart_sound_active(void) { return sound_pending||sound_active||state==ARM_SOUND_ARM||state==ARM_SOUND_CAL; }
bool arm_uart_mouse_faulted(void) { return state==ARM_FAULT; }
const char *arm_uart_mouse_fault(void) { return fault_text[0] ? fault_text : "none"; }
bool arm_uart_mouse_ready(void) { return arm_ready && state != ARM_FAULT; }
const char *arm_uart_mouse_version(void) { return arm_version; }
bool arm_uart_host_usb_seen(void){return host_usb_seen;}
ArmHostUsbState arm_uart_host_usb_state(void){return host_usb_state;}
