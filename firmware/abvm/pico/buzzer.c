#include "buzzer.h"

#include <stddef.h>
#include "hardware/clocks.h"
#include "hardware/gpio.h"
#include "hardware/pwm.h"

#define BUZZER_PIN 6u
#define BUZZER_DUTY_WRAP 3999u
#define BUZZER_UPDATE_MS 4u
#define BUZZER_ATTACK_MS 18u
#define BUZZER_RELEASE_MS 24u

typedef struct BuzzerTone {
    uint16_t start_hz;
    uint16_t end_hz;
    uint16_t duration_ms;
    uint16_t gap_ms;
} BuzzerTone;

typedef struct BuzzerPattern {
    const BuzzerTone *tones;
    uint8_t count;
    uint8_t priority;
} BuzzerPattern;

/* Glides use one continuous segment; alert patterns intentionally retain gaps. */
static const BuzzerTone start_tones[] = {{659,1175,180,0}};
static const BuzzerTone stop_tones[] = {{988,440,190,0}};
static const BuzzerTone pause_tones[] = {{659,440,140,0}};
static const BuzzerTone resume_tones[] = {{523,988,170,0}};
static const BuzzerTone catch_tones[] = {{988,2093,260,0}};
static const BuzzerTone timeout_tones[] = {{440,330,150,45},{392,294,170,0}};
static const BuzzerTone error_tones[] = {{220,165,160,45},{220,165,160,45},{196,131,240,0}};
static const BuzzerTone calibration_tones[] = {{523,1319,260,0}};
static BuzzerTone stage_tones[1];
static BuzzerTone smooth_tones[1];

static const BuzzerPattern patterns[] = {
    [BUZZER_CUE_START] = {start_tones, 1u, 2u},
    [BUZZER_CUE_STOP] = {stop_tones, 1u, 3u},
    [BUZZER_CUE_PAUSE] = {pause_tones, 1u, 3u},
    [BUZZER_CUE_RESUME] = {resume_tones, 1u, 3u},
    [BUZZER_CUE_CATCH] = {catch_tones, 1u, 5u},
    [BUZZER_CUE_TIMEOUT] = {timeout_tones, 2u, 4u},
    [BUZZER_CUE_ERROR] = {error_tones, 3u, 7u},
    [BUZZER_CUE_CALIBRATION_OK] = {calibration_tones, 1u, 4u},
};

static const BuzzerTone *tones;
static uint8_t tone_count, tone_index, priority;
static uint slice;
static uint32_t segment_started, deadline, next_update;
static bool active, gap_phase;

static bool reached(uint32_t now, uint32_t due) {
    return (int32_t)(now - due) >= 0;
}

static void tone_off(void) {
    pwm_set_gpio_level(BUZZER_PIN, 0u);
}

static void tone_apply(uint16_t hz, uint16_t level) {
    if (!hz || !level) { tone_off(); return; }
    uint32_t clock = clock_get_hz(clk_sys);
    float divider = (float)clock / ((float)hz * (float)(BUZZER_DUTY_WRAP + 1u));
    if (divider < 1.0f) divider = 1.0f;
    if (divider > 255.0f) divider = 255.0f;
    pwm_set_clkdiv(slice, divider);
    pwm_set_wrap(slice, BUZZER_DUTY_WRAP);
    pwm_set_gpio_level(BUZZER_PIN, level);
}

static void render_segment(uint32_t now) {
    const BuzzerTone *tone = &tones[tone_index];
    uint32_t elapsed = now - segment_started;
    if (elapsed > tone->duration_ms) elapsed = tone->duration_ms;
    int32_t span = (int32_t)tone->end_hz - (int32_t)tone->start_hz;
    uint16_t hz = (uint16_t)((int32_t)tone->start_hz +
        (span * (int32_t)elapsed) / (int32_t)tone->duration_ms);
    uint32_t envelope = 1000u;
    if (elapsed < BUZZER_ATTACK_MS)
        envelope = elapsed * 1000u / BUZZER_ATTACK_MS;
    uint32_t remaining = tone->duration_ms - elapsed;
    if (remaining < BUZZER_RELEASE_MS) {
        uint32_t release = remaining * 1000u / BUZZER_RELEASE_MS;
        if (release < envelope) envelope = release;
    }
    uint16_t level = (uint16_t)(((BUZZER_DUTY_WRAP + 1u) / 2u) * envelope / 1000u);
    tone_apply(hz, level);
}

static void start_segment(uint32_t now) {
    segment_started = now;
    deadline = now + tones[tone_index].duration_ms;
    next_update = now;
    gap_phase = false;
    render_segment(now);
}

static void begin(const BuzzerTone *next, uint8_t count, uint8_t next_priority,
                  uint32_t now) {
    if (!next || !count || (active && next_priority < priority)) return;
    tones = next; tone_count = count; tone_index = 0u;
    priority = next_priority; active = true;
    start_segment(now);
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
    uint16_t base = (uint16_t)(440u + (uint16_t)stage * 110u);
    stage_tones[0] = (BuzzerTone){base, (uint16_t)(base + 220u), 150u, 0u};
    begin(stage_tones, 1u, 2u, now);
}

void buzzer_play_smooth(uint16_t start_hz, uint16_t end_hz,
                        uint16_t duration_ms, uint32_t now) {
    if (start_hz < 40u || end_hz < 40u || start_hz > 5000u ||
        end_hz > 5000u || duration_ms < 40u || duration_ms > 10000u) return;
    smooth_tones[0] = (BuzzerTone){start_hz,end_hz,duration_ms,0u};
    begin(smooth_tones, 1u, 1u, now);
}

void buzzer_service(uint32_t now) {
    if (!active) return;
    if (!gap_phase && reached(now, next_update) && !reached(now, deadline)) {
        render_segment(now);
        next_update = now + BUZZER_UPDATE_MS;
    }
    if (!reached(now, deadline)) return;
    if (!gap_phase) {
        tone_off();
        uint16_t gap = tones[tone_index].gap_ms;
        if (gap) { gap_phase = true; deadline = now + gap; return; }
    }
    gap_phase = false;
    if (++tone_index >= tone_count) {
        active = false; priority = 0u; tone_off(); return;
    }
    start_segment(now);
}

void buzzer_silence(void) {
    active = false; priority = 0u; tone_off();
}

bool buzzer_active(void) { return active; }
