#include "buzzer.h"

#include <stddef.h>
#include "hardware/clocks.h"
#include "hardware/gpio.h"
#include "hardware/pwm.h"

#define BUZZER_PIN 6u
#define BUZZER_DUTY_WRAP 3999u

typedef struct BuzzerTone {
    uint16_t hz;
    uint16_t duration_ms;
    uint16_t gap_ms;
} BuzzerTone;

typedef struct BuzzerPattern {
    const BuzzerTone *tones;
    uint8_t count;
    uint8_t priority;
} BuzzerPattern;

/* Exact presets from the original GP6 buzzer contract. */
static const BuzzerTone preset_short[] = {{1000,180,0}};
static const BuzzerTone preset_double[] = {{1000,140,100},{1000,140,0}};
static const BuzzerTone preset_warning[] = {{700,180,90},{700,180,90},{700,300,0}};
static const BuzzerTone preset_success[] = {{900,120,70},{1300,220,0}};

static const BuzzerPattern patterns[] = {
    [BUZZER_CUE_START] = {preset_success, 2u, 2u},
    [BUZZER_CUE_STOP] = {preset_short, 1u, 3u},
    [BUZZER_CUE_PAUSE] = {preset_short, 1u, 3u},
    [BUZZER_CUE_RESUME] = {preset_double, 2u, 3u},
    [BUZZER_CUE_CATCH] = {preset_success, 2u, 5u},
    [BUZZER_CUE_TIMEOUT] = {preset_warning, 3u, 4u},
    [BUZZER_CUE_ERROR] = {preset_warning, 3u, 7u},
    [BUZZER_CUE_CALIBRATION_OK] = {preset_success, 2u, 4u},
};

static const BuzzerTone *tones;
static uint8_t tone_count, tone_index, priority;
static uint slice;
static uint32_t deadline;
static bool active, gap_phase;

static bool reached(uint32_t now, uint32_t due) {
    return (int32_t)(now - due) >= 0;
}

static void tone_off(void) {
    pwm_set_gpio_level(BUZZER_PIN, 0u);
}

static void tone_on(uint16_t hz) {
    if (!hz) { tone_off(); return; }
    uint32_t clock = clock_get_hz(clk_sys);
    float divider = (float)clock / ((float)hz * (float)(BUZZER_DUTY_WRAP + 1u));
    if (divider < 1.0f) divider = 1.0f;
    if (divider > 255.0f) divider = 255.0f;
    pwm_set_clkdiv(slice, divider);
    pwm_set_wrap(slice, BUZZER_DUTY_WRAP);
    pwm_set_gpio_level(BUZZER_PIN, (BUZZER_DUTY_WRAP + 1u) / 2u);
}

static void begin(const BuzzerTone *next, uint8_t count, uint8_t next_priority,
                  uint32_t now) {
    if (!next || !count || (active && next_priority < priority)) return;
    tones = next; tone_count = count; tone_index = 0u;
    priority = next_priority; active = true; gap_phase = false;
    tone_on(tones[0].hz);
    deadline = now + tones[0].duration_ms;
}

void buzzer_init(void) {
    gpio_set_function(BUZZER_PIN, GPIO_FUNC_PWM);
    slice = pwm_gpio_to_slice_num(BUZZER_PIN);
    pwm_set_enabled(slice, true);
    tone_off(); active = false; priority = 0u;
}

void buzzer_play(BuzzerCue cue, uint32_t now) {
    if ((unsigned)cue >= sizeof(patterns) / sizeof(patterns[0])) return;
    const BuzzerPattern *pattern = &patterns[cue];
    begin(pattern->tones, pattern->count, pattern->priority, now);
}

void buzzer_play_stage(uint8_t stage, uint32_t now) {
    if (stage < 1u || stage > 6u) return;
    begin(preset_short, 1u, 2u, now);
}

void buzzer_service(uint32_t now) {
    if (!active || !reached(now, deadline)) return;
    if (!gap_phase) {
        tone_off();
        uint16_t gap = tones[tone_index].gap_ms;
        if (gap) { gap_phase = true; deadline = now + gap; return; }
    }
    gap_phase = false;
    if (++tone_index >= tone_count) {
        active = false; priority = 0u; tone_off(); return;
    }
    tone_on(tones[tone_index].hz);
    deadline = now + tones[tone_index].duration_ms;
}

void buzzer_silence(void) {
    active = false; priority = 0u; tone_off();
}

bool buzzer_active(void) { return active; }
