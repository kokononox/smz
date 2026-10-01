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
/* Lower answering phrase distinguishes a repeated person from a new one. */
static const BuzzerTone whisper_repeat_notice[] = {{1175,100,35},{988,100,35},{1175,190,0}};

/* Distinct 3–4 note identities for the eight light environments. The operator
 * may assign any motif to any profile in Classroom Studio. */
static const BuzzerTone light_calibration_motifs[8][4] = {
    {{262,100,45},{392,100,45},{523,180,0},{0,0,0}},
    {{659,110,40},{523,110,40},{392,220,0},{0,0,0}},
    {{330,90,35},{415,90,35},{494,90,35},{659,190,0}},
    {{294,160,70},{440,100,50},{294,230,0},{0,0,0}},
    {{392,90,30},{494,90,30},{587,90,30},{784,190,0}},
    {{880,85,30},{740,85,30},{880,85,30},{740,180,0}},
    {{1047,80,25},{1319,80,25},{1568,80,25},{2093,180,0}},
    {{1175,90,30},{988,90,30},{1175,90,30},{1568,190,0}},
};
static const uint8_t light_calibration_motif_counts[] = {3,3,4,3,4,4,4,4};
static const BuzzerTone calibration_enter_prefix[] = {{523,100,0},{659,120,0},{784,180,0}};
static const BuzzerTone calibration_exit[] = {{784,100,0},{659,120,0},{523,220,0}};
static const BuzzerTone calibration_error[] = {{220,140,80},{220,260,0}};
static const BuzzerTone calibration_success[] = {{880,160,60},{1175,220,60},{1568,360,0}};
static const BuzzerTone calibration_complete[] = {{262,90,35},{294,90,35},{330,90,35},{349,90,35},{392,90,35},{440,90,0}};
static const BuzzerTone calibration_record_light[] = {{660,65,0}};
static const BuzzerTone calibration_record_sound[] = {{523,90,0}};
static const BuzzerTone calibration_sound_target[] = {{988,120,0}};
static const BuzzerTone transition_1[] = {{550,50,10},{660,50,10},{770,80,0}};
static const BuzzerTone transition_2[] = {{660,50,10},{770,50,10},{880,80,0}};
static const BuzzerTone transition_3[] = {{770,50,10},{880,50,10},{990,80,0}};
static const BuzzerTone transition_4[] = {{880,50,10},{990,50,10},{1100,80,0}};
static const BuzzerTone transition_5[] = {{990,50,10},{1100,50,10},{1210,80,0}};
static const BuzzerTone transition_6[] = {{1100,50,10},{1210,50,10},{1320,80,0}};
static const BuzzerTone watchdog_cycle[] = {{620,180,20},{1000,180,20},{1380,180,20},{1000,180,0}};
static BuzzerTone dynamic_tones[16];
static BuzzerTone generated_tones[4];
static BuzzerTone calibration_custom[8];
static uint8_t calibration_custom_count, calibration_custom_volume=100u, calibration_custom_envelope;
static BuzzerTone system_cue_tones[23][8];
static uint8_t system_cue_counts[23],system_cue_volumes[23],system_cue_envelopes[23];
static bool watchdog_custom;

static const BuzzerPattern patterns[] = {
    [BUZZER_CUE_START] = {guard_start, ARRAY_COUNT(guard_start), 2u},
    [BUZZER_CUE_STOP] = {guard_stop, ARRAY_COUNT(guard_stop), 3u},
    [BUZZER_CUE_PAUSE] = {guard_pause, ARRAY_COUNT(guard_pause), 3u},
    [BUZZER_CUE_RESUME] = {guard_resume, ARRAY_COUNT(guard_resume), 3u},
    [BUZZER_CUE_TIMEOUT] = {preset_warning, ARRAY_COUNT(preset_warning), 4u},
    [BUZZER_CUE_ERROR] = {preset_warning, ARRAY_COUNT(preset_warning), 7u},
    [BUZZER_CUE_WHISPER] = {whisper_notice, ARRAY_COUNT(whisper_notice), 5u},
    [BUZZER_CUE_WHISPER_REPEAT] = {whisper_repeat_notice, ARRAY_COUNT(whisper_repeat_notice), 5u},
    [BUZZER_CUE_CALIBRATION_OK] = {calibration_success, ARRAY_COUNT(calibration_success), 4u},
};

static const BuzzerTone *tones;
static uint8_t tone_count, tone_index, priority;
static uint slice;
static uint32_t deadline, sweep_started, sweep_next_update;
static uint16_t sweep_start_hz, sweep_end_hz, sweep_duration_ms;
static bool active, gap_phase, sweep_active, watchdog_alarm, watchdog_rising;
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
static bool begin_system(uint8_t cue_id,uint8_t cue_priority,uint32_t now){
    if(cue_id<1u||cue_id>23u||!system_cue_counts[cue_id-1u])return false;
    begin_styled(system_cue_tones[cue_id-1u],system_cue_counts[cue_id-1u],cue_priority,
        system_cue_volumes[cue_id-1u],system_cue_envelopes[cue_id-1u],now);
    return true;
}
static const uint16_t generated_notes[] = {
    262,294,330,349,392,440,494,523,587,659,698,784,880,988,1047,1175,
    1319,1397,1568,1760,1976,2093
};
static void generated_calibration_motif(uint8_t cue,uint8_t *count) {
    uint8_t n=(uint8_t)(cue-32u),shape=(uint8_t)(n%8u);
    uint8_t root=(uint8_t)((n*3u+n/8u)%15u);
    static const int8_t offsets[8][4]={
        {0,2,4,-1},{5,3,0,-1},{0,4,2,-1},{2,2,2,-1},
        {0,3,6,3},{0,5,-1,-1},{0,2,5,-1},{3,5,7,-1}
    };
    static const uint16_t durations[8][4]={
        {90,90,170,0},{90,90,180,0},{95,120,190,0},{70,70,150,0},
        {80,90,80,180},{120,210,0,0},{75,75,190,0},{65,65,150,0}
    };
    static const uint16_t gaps[8][4]={
        {30,30,0,0},{30,30,0,0},{35,45,0,0},{45,45,0,0},
        {25,25,25,0},{55,0,0,0},{25,25,0,0},{20,20,0,0}
    };
    uint8_t total=0u;
    for(uint8_t i=0;i<4u&&offsets[shape][i]>=0;++i){
        uint8_t note=(uint8_t)(root+(uint8_t)offsets[shape][i]);
        if(note>=ARRAY_COUNT(generated_notes))note=ARRAY_COUNT(generated_notes)-1u;
        generated_tones[total++]=(BuzzerTone){generated_notes[note],durations[shape][i],gaps[shape][i]};
    }
    *count=total;
}
static const BuzzerTone *system_calibration_motif(uint8_t cue,uint8_t *count){
    switch(cue){
        case 9u:*count=ARRAY_COUNT(guard_start);return guard_start;
        case 10u:*count=ARRAY_COUNT(guard_stop);return guard_stop;
        case 11u:*count=ARRAY_COUNT(guard_pause);return guard_pause;
        case 12u:*count=ARRAY_COUNT(guard_resume);return guard_resume;
        case 13u:case 14u:*count=ARRAY_COUNT(preset_warning);return preset_warning;
        case 15u:*count=ARRAY_COUNT(whisper_notice);return whisper_notice;
        case 16u:*count=ARRAY_COUNT(whisper_repeat_notice);return whisper_repeat_notice;
        case 17u:*count=ARRAY_COUNT(calibration_enter_prefix);return calibration_enter_prefix;
        case 18u:*count=ARRAY_COUNT(calibration_exit);return calibration_exit;
        case 19u:*count=ARRAY_COUNT(calibration_error);return calibration_error;
        case 20u:*count=ARRAY_COUNT(calibration_success);return calibration_success;
        case 21u:*count=ARRAY_COUNT(calibration_complete);return calibration_complete;
        case 22u:*count=ARRAY_COUNT(calibration_record_light);return calibration_record_light;
        case 23u:*count=ARRAY_COUNT(calibration_record_sound);return calibration_record_sound;
        case 24u:*count=ARRAY_COUNT(calibration_sound_target);return calibration_sound_target;
        case 25u:*count=ARRAY_COUNT(transition_1);return transition_1;
        case 26u:*count=ARRAY_COUNT(transition_2);return transition_2;
        case 27u:*count=ARRAY_COUNT(transition_3);return transition_3;
        case 28u:*count=ARRAY_COUNT(transition_4);return transition_4;
        case 29u:*count=ARRAY_COUNT(transition_5);return transition_5;
        case 30u:*count=ARRAY_COUNT(transition_6);return transition_6;
        case 31u:*count=ARRAY_COUNT(watchdog_cycle);return watchdog_cycle;
        default:*count=0u;return NULL;
    }
}
static uint16_t selection_note(uint8_t selection, bool sound) {
    if (sound) return selection == 3u ? 1047u : selection == 2u ? 880u : 660u;
    if (selection < 1u || selection > 100u) selection = 1u;
    if(selection>8u&&selection<=31u){uint8_t count;const BuzzerTone *motif=system_calibration_motif(selection,&count);return motif[0].hz;}
    if(selection>31u){uint8_t count;generated_calibration_motif(selection,&count);return generated_tones[0].hz;}
    return light_calibration_motifs[selection - 1u][0].hz;
}
static const BuzzerTone *light_calibration_motif(uint8_t cue,uint8_t *count) {
    if(cue==0u&&calibration_custom_count){*count=calibration_custom_count;return calibration_custom;}
    if(cue>8u&&cue<=31u)return system_calibration_motif(cue,count);
    if(cue>31u&&cue<=100u){generated_calibration_motif(cue,count);return generated_tones;}
    if(cue<1u||cue>8u)cue=1u;
    if(count)*count=light_calibration_motif_counts[cue-1u];
    return light_calibration_motifs[cue-1u];
}
void buzzer_set_calibration_custom(const BuzzerTone *custom,uint8_t count,
                                   uint8_t volume,uint8_t envelope){
    calibration_custom_count=count>8u?8u:count;
    for(uint8_t i=0;i<calibration_custom_count;++i)calibration_custom[i]=custom[i];
    calibration_custom_volume=volume?volume:100u;
    calibration_custom_envelope=envelope<=3u?envelope:0u;
}
void buzzer_set_system_cue(uint8_t cue_id,const BuzzerTone *custom,uint8_t count,
                           uint8_t volume,uint8_t envelope){
    if(cue_id<1u||cue_id>23u)return;
    uint8_t slot=(uint8_t)(cue_id-1u);
    system_cue_counts[slot]=count>8u?8u:count;
    for(uint8_t i=0;i<system_cue_counts[slot];++i)system_cue_tones[slot][i]=custom[i];
    system_cue_volumes[slot]=volume?volume:100u;
    system_cue_envelopes[slot]=envelope<=3u?envelope:0u;
}

void buzzer_init(void) {
    gpio_set_function(BUZZER_PIN, GPIO_FUNC_PWM); slice=pwm_gpio_to_slice_num(BUZZER_PIN);
    pwm_set_enabled(slice,true); tone_off(); active=false; priority=0u;
}
void buzzer_play(BuzzerCue cue,uint32_t now) {
    if ((unsigned)cue >= sizeof(patterns)/sizeof(patterns[0])) return;
    static const uint8_t system_ids[]={1u,2u,3u,4u,5u,6u,7u,8u,12u};
    const BuzzerPattern *p=&patterns[cue];
    if(system_ids[cue]&&begin_system(system_ids[cue],p->priority,now))return;
    begin(p->tones,p->count,p->priority,now);
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
    if(begin_system((uint8_t)(16u+profile_id),4u,now))return;
    sweep_start_hz=(uint16_t)(440u+(uint16_t)profile_id*110u);
    sweep_end_hz=(uint16_t)(sweep_start_hz+220u);
    sweep_duration_ms=150u;sweep_started=now;sweep_next_update=now;
    priority=4u;active=true;gap_phase=false;sweep_active=true;
    tone_on(sweep_start_hz);deadline=now+sweep_duration_ms;
}
static void start_watchdog_sweep(uint32_t now) {
    sweep_start_hz=watchdog_rising?620u:1380u;
    sweep_end_hz=watchdog_rising?1380u:620u;
    sweep_duration_ms=650u;sweep_started=now;sweep_next_update=now;
    priority=8u;active=true;gap_phase=false;sweep_active=true;
    tone_on(sweep_start_hz);deadline=now+sweep_duration_ms;
}
void buzzer_watchdog_alarm_start(uint32_t now) {
    watchdog_alarm=true;watchdog_rising=true;
    watchdog_custom=begin_system(23u,8u,now);
    if(!watchdog_custom)start_watchdog_sweep(now);
}
void buzzer_watchdog_alarm_stop(void) {
    watchdog_alarm=false;
    if(active&&priority==8u) {
        active=false;sweep_active=false;priority=0u;tone_off();
    }
}
bool buzzer_watchdog_alarm_active(void){return watchdog_alarm;}
void buzzer_calibration_enter(uint8_t selection,bool sound,uint32_t now) {
    const BuzzerTone *prefix=calibration_enter_prefix;
    uint8_t prefix_count=ARRAY_COUNT(calibration_enter_prefix),prefix_volume=100u,prefix_envelope=0u;
    if(system_cue_counts[8]){
        prefix=system_cue_tones[8];prefix_count=system_cue_counts[8];
        prefix_volume=system_cue_volumes[8];prefix_envelope=system_cue_envelopes[8];
    }
    for(uint8_t i=0;i<prefix_count;++i)dynamic_tones[i]=prefix[i];
    if(sound) {
        dynamic_tones[prefix_count]=(BuzzerTone){selection_note(selection,true),220u,0u};
        begin_styled(dynamic_tones,(uint8_t)(prefix_count+1u),5u,prefix_volume,prefix_envelope,now);
        return;
    }
    uint8_t count;const BuzzerTone *motif=light_calibration_motif(selection,&count);
    for(uint8_t i=0;i<count;++i)dynamic_tones[prefix_count+i]=motif[i];
    if(selection==0u)begin_styled(dynamic_tones,(uint8_t)(prefix_count+count),5u,
        calibration_custom_volume,calibration_custom_envelope,now);
    else begin_styled(dynamic_tones,(uint8_t)(prefix_count+count),5u,prefix_volume,prefix_envelope,now);
}
void buzzer_calibration_position(uint8_t selection,bool sound,uint32_t now) {
    if(sound) {
        dynamic_tones[0]=(BuzzerTone){selection_note(selection,true),220u,0u};
        begin(dynamic_tones,1u,5u,now);return;
    }
    uint8_t count;const BuzzerTone *motif=light_calibration_motif(selection,&count);
    if(selection==0u)begin_styled(motif,count,5u,calibration_custom_volume,calibration_custom_envelope,now);
    else begin(motif,count,5u,now);
}
void buzzer_calibration_record_start(bool sound,uint32_t now) {
    if(begin_system(sound?15u:14u,5u,now))return;
    dynamic_tones[0]=(BuzzerTone){sound?523u:660u,sound?90u:65u,0u}; begin(dynamic_tones,1u,5u,now);
}
void buzzer_calibration_sound_target(uint32_t now) {
    if(begin_system(16u,5u,now))return;
    dynamic_tones[0]=(BuzzerTone){988u,120u,0u}; begin(dynamic_tones,1u,5u,now);
}
void buzzer_calibration_stage_complete(uint8_t stage,uint32_t now) {
    uint8_t count;const BuzzerTone *motif=light_calibration_motif(stage,&count);
    begin(motif,count,5u,now);
}
void buzzer_calibration_save_success(uint32_t now) { if(!begin_system(12u,6u,now))begin(calibration_success,ARRAY_COUNT(calibration_success),6u,now); }
void buzzer_calibration_save_error(uint32_t now) { if(!begin_system(11u,7u,now))begin(calibration_error,ARRAY_COUNT(calibration_error),7u,now); }
void buzzer_calibration_complete(uint32_t now) { if(!begin_system(13u,6u,now))begin(calibration_complete,ARRAY_COUNT(calibration_complete),6u,now); }
void buzzer_calibration_exit(uint32_t now) { if(!begin_system(10u,6u,now))begin(calibration_exit,ARRAY_COUNT(calibration_exit),6u,now); }
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
        if(watchdog_alarm&&!watchdog_custom) {
            watchdog_rising=!watchdog_rising;
            start_watchdog_sweep(now);
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
    gap_phase=false;if(++tone_index>=tone_count){
        if(watchdog_alarm&&watchdog_custom){begin_system(23u,8u,now);return;}
        active=false;priority=0u;tone_off();return;
    }
    start_current_tone(now);
}
void buzzer_silence(void){watchdog_alarm=false;active=false;sweep_active=false;priority=0u;tone_off();}
bool buzzer_active(void){return active;}
