#include "buzzer.h"

#include <stddef.h>
#include "hardware/clocks.h"
#include "hardware/gpio.h"
#include "hardware/pwm.h"

#define BUZZER_PIN 6u
#define BUZZER_DUTY_WRAP 3999u
#define TRANSITION_UPDATE_MS 4u
#define ENVELOPE_UPDATE_MS 4u
#define ARRAY_COUNT(a) ((uint8_t)(sizeof(a) / sizeof((a)[0])))

typedef struct BuzzerTone { uint16_t hz, duration_ms, gap_ms; } BuzzerTone;
typedef struct BuzzerPattern { const BuzzerTone *tones; uint8_t count, priority; } BuzzerPattern;

/* Exact legacy Guard patterns. A zero-frequency legacy delay is represented by
 * the preceding tone's gap so the fixed-state player remains nonblocking. */
static const BuzzerTone guard_start[] = {{784,160,0},{988,160,0},{1175,200,80},{1175,280,0}};
static const BuzzerTone guard_stop[] = {{392,180,0},{330,160,0},{262,260,60},{196,260,0}};
static const BuzzerTone guard_pause[] = {{523,180,100},{523,180,100},{523,340,0}};
static const BuzzerTone guard_resume[] = {{659,150,0},{784,150,0},{988,150,0},{784,150,0},{988,300,0}};

/* Original warning preset retained for Timeout/Error. */
static const BuzzerTone preset_warning[] = {{700,180,90},{700,180,90},{700,300,0}};
/* Distinct high two-note acknowledgement shared by sound/light Whisper. */
static const BuzzerTone whisper_notice[] = {{1397,110,45},{1760,190,0}};

/* Exact legacy physical-calibration feedback. */
static const uint16_t calibration_notes[] = {262,294,330,349,392,440,494};
static const BuzzerTone calibration_enter_prefix[] = {{523,100,0},{659,120,0},{784,180,0}};
static const BuzzerTone calibration_exit[] = {{784,100,0},{659,120,0},{523,220,0}};
static const BuzzerTone calibration_error[] = {{220,140,80},{220,260,0}};
static const BuzzerTone calibration_success[] = {{880,160,60},{1175,220,60},{1568,360,0}};
static const BuzzerTone calibration_complete[] = {{262,90,35},{294,90,35},{330,90,35},{349,90,35},{392,90,35},{440,90,0}};
static BuzzerTone dynamic_tones[5];

static const BuzzerPattern patterns[] = {
    [BUZZER_CUE_START] = {guard_start, ARRAY_COUNT(guard_start), 2u},
    [BUZZER_CUE_STOP] = {guard_stop, ARRAY_COUNT(guard_stop), 3u},
    [BUZZER_CUE_PAUSE] = {guard_pause, ARRAY_COUNT(guard_pause), 3u},
    [BUZZER_CUE_RESUME] = {guard_resume, ARRAY_COUNT(guard_resume), 3u},
    [BUZZER_CUE_TIMEOUT] = {preset_warning, ARRAY_COUNT(preset_warning), 4u},
    [BUZZER_CUE_ERROR] = {preset_warning, ARRAY_COUNT(preset_warning), 7u},
    [BUZZER_CUE_WHISPER] = {whisper_notice, ARRAY_COUNT(whisper_notice), 5u},
    [BUZZER_CUE_CALIBRATION_OK] = {calibration_success, ARRAY_COUNT(calibration_success), 4u},
};

static const BuzzerTone *tones;
static uint8_t tone_count, tone_index, priority;
static uint slice;
static uint32_t deadline, sweep_started, sweep_next_update;
static uint16_t sweep_start_hz, sweep_end_hz, sweep_duration_ms;
static bool active, gap_phase, sweep_active;
static uint8_t tone_volume=100u, tone_envelope;
static uint16_t current_duration_ms;
static uint32_t tone_started, envelope_next_update;

static bool reached(uint32_t now, uint32_t due) { return (int32_t)(now - due) >= 0; }
static void tone_off(void) { pwm_set_gpio_level(BUZZER_PIN, 0u); }
static void tone_on_level(uint16_t hz,uint8_t volume) {
    if (!hz) { tone_off(); return; }
    uint32_t clock = clock_get_hz(clk_sys);
    float divider = (float)clock / ((float)hz * (float)(BUZZER_DUTY_WRAP + 1u));
    if (divider < 1.0f) divider = 1.0f;
    if (divider > 255.0f) divider = 255.0f;
    pwm_set_clkdiv(slice, divider); pwm_set_wrap(slice, BUZZER_DUTY_WRAP);
    if(volume>100u)volume=100u;
    pwm_set_gpio_level(BUZZER_PIN,
        ((uint32_t)(BUZZER_DUTY_WRAP + 1u) * volume) / 200u);
}
static void tone_on(uint16_t hz) { tone_on_level(hz,100u); }
static uint8_t envelope_level(uint32_t now) {
    if(tone_envelope==0u||current_duration_ms<12u)return tone_volume;
    uint32_t elapsed=now-tone_started;
    if(elapsed>current_duration_ms)elapsed=current_duration_ms;
    uint32_t edge=current_duration_ms/3u;
    if(edge>80u)edge=80u;
    if(edge<4u)edge=4u;
    uint32_t level=tone_volume;
    if((tone_envelope==1u||tone_envelope==2u)&&elapsed<edge)
        level=((uint32_t)tone_volume*elapsed)/edge;
    if((tone_envelope==1u||tone_envelope==3u)&&
       elapsed>=(uint32_t)current_duration_ms-edge)
        level=((uint32_t)tone_volume*(current_duration_ms-elapsed))/edge;
    return (uint8_t)level;
}
static void start_current_tone(uint32_t now) {
    tone_started=now;current_duration_ms=tones[tone_index].duration_ms;
    envelope_next_update=now+ENVELOPE_UPDATE_MS;
    tone_on_level(tones[tone_index].hz,envelope_level(now));
    deadline=now+current_duration_ms;
}
static void begin_styled(const BuzzerTone *next, uint8_t count,
                         uint8_t next_priority,uint8_t volume,
                         uint8_t envelope,uint32_t now) {
    if (!next || !count || (active && next_priority < priority)) return;
    tones=next; tone_count=count; tone_index=0u; priority=next_priority;
    tone_volume=volume?volume:100u;if(tone_volume>100u)tone_volume=100u;
    tone_envelope=envelope<=3u?envelope:0u;
    active=true;gap_phase=false;sweep_active=false;start_current_tone(now);
}
static void begin(const BuzzerTone *next, uint8_t count, uint8_t next_priority, uint32_t now) {
    begin_styled(next,count,next_priority,100u,0u,now);
}
static uint16_t selection_note(uint8_t selection, bool sound) {
    if (sound) return selection == 2u ? 880u : 660u;
    if (selection < 1u || selection > ARRAY_COUNT(calibration_notes)) selection = 1u;
    return calibration_notes[selection - 1u];
}

void buzzer_init(void) {
    gpio_set_function(BUZZER_PIN, GPIO_FUNC_PWM); slice=pwm_gpio_to_slice_num(BUZZER_PIN);
    pwm_set_enabled(slice,true); tone_off(); active=false; priority=0u;
}
void buzzer_play(BuzzerCue cue,uint32_t now) {
    if ((unsigned)cue >= sizeof(patterns)/sizeof(patterns[0])) return;
    const BuzzerPattern *p=&patterns[cue]; begin(p->tones,p->count,p->priority,now);
}
void buzzer_play_tone(uint16_t hz,uint16_t duration_ms,uint32_t now) {
    buzzer_play_tone_ex(hz,duration_ms,100u,0u,now);
}
void buzzer_play_tone_ex(uint16_t hz,uint16_t duration_ms,uint8_t volume,
                         uint8_t envelope,uint32_t now) {
    if(hz<30u||hz>20000u||!duration_ms)return;
    dynamic_tones[0]=(BuzzerTone){hz,duration_ms,0u};
    begin_styled(dynamic_tones,1u,5u,volume,envelope,now);
}
void buzzer_guard_transition(uint8_t profile_id,uint32_t now) {
    if(profile_id<1u||profile_id>6u)return;
    if(active&&priority>4u)return;
    sweep_start_hz=(uint16_t)(440u+(uint16_t)profile_id*110u);
    sweep_end_hz=(uint16_t)(sweep_start_hz+220u);
    sweep_duration_ms=150u;sweep_started=now;sweep_next_update=now;
    priority=4u;active=true;gap_phase=false;sweep_active=true;
    tone_on(sweep_start_hz);deadline=now+sweep_duration_ms;
}
void buzzer_calibration_enter(uint8_t selection,bool sound,uint32_t now) {
    for(uint8_t i=0;i<ARRAY_COUNT(calibration_enter_prefix);++i) dynamic_tones[i]=calibration_enter_prefix[i];
    dynamic_tones[3]=(BuzzerTone){selection_note(selection,sound),220u,0u};
    begin(dynamic_tones,4u,5u,now);
}
void buzzer_calibration_position(uint8_t selection,bool sound,uint32_t now) {
    dynamic_tones[0]=(BuzzerTone){selection_note(selection,sound),220u,0u}; begin(dynamic_tones,1u,5u,now);
}
void buzzer_calibration_record_start(bool sound,uint32_t now) {
    dynamic_tones[0]=(BuzzerTone){sound?523u:660u,sound?90u:65u,0u}; begin(dynamic_tones,1u,5u,now);
}
void buzzer_calibration_sound_target(uint32_t now) {
    dynamic_tones[0]=(BuzzerTone){988u,120u,0u}; begin(dynamic_tones,1u,5u,now);
}
void buzzer_calibration_stage_complete(uint8_t stage,uint32_t now) {
    uint16_t note=selection_note(stage,false);
    dynamic_tones[0]=(BuzzerTone){note,110u,60u}; dynamic_tones[1]=(BuzzerTone){note,190u,0u};
    begin(dynamic_tones,2u,5u,now);
}
void buzzer_calibration_save_success(uint32_t now) { begin(calibration_success,ARRAY_COUNT(calibration_success),6u,now); }
void buzzer_calibration_save_error(uint32_t now) { begin(calibration_error,ARRAY_COUNT(calibration_error),7u,now); }
void buzzer_calibration_complete(uint32_t now) { begin(calibration_complete,ARRAY_COUNT(calibration_complete),6u,now); }
void buzzer_calibration_exit(uint32_t now) { begin(calibration_exit,ARRAY_COUNT(calibration_exit),6u,now); }
void buzzer_service(uint32_t now) {
    if(!active)return;
    if(sweep_active){
        if(!reached(now,deadline)){
            if(reached(now,sweep_next_update)){
                uint32_t elapsed=now-sweep_started;
                uint16_t hz=(uint16_t)(sweep_start_hz+
                    ((uint32_t)(sweep_end_hz-sweep_start_hz)*elapsed)/sweep_duration_ms);
                tone_on(hz);sweep_next_update=now+TRANSITION_UPDATE_MS;
            }
            return;
        }
        sweep_active=false;active=false;priority=0u;tone_off();return;
    }
    if(!gap_phase&&tone_envelope&&reached(now,envelope_next_update)&&
       !reached(now,deadline)){
        tone_on_level(tones[tone_index].hz,envelope_level(now));
        envelope_next_update=now+ENVELOPE_UPDATE_MS;
    }
    if(!reached(now,deadline))return;
    if(!gap_phase){tone_off();uint16_t gap=tones[tone_index].gap_ms;if(gap){gap_phase=true;deadline=now+gap;return;}}
    gap_phase=false;if(++tone_index>=tone_count){active=false;priority=0u;tone_off();return;}
    start_current_tone(now);
}
void buzzer_silence(void){active=false;sweep_active=false;priority=0u;tone_off();}
bool buzzer_active(void){return active;}
