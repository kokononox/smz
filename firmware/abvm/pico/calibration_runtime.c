#include "calibration_runtime.h"
#include <stdarg.h>
#include <stdio.h>
#include <string.h>
#include "arm_uart_mouse.h"
#include "calibration_store.h"
#include "guard_runtime.h"
#include "light_sensor.h"
#include "light_range_fit.h"

#define LIGHT_SAMPLE_MS 5000u
#define SOUND_SAMPLE_MS 250u
#define SOUND_SILENCE_MS 3000u
#define SOUND_TARGET_MS 30000u
#define SOUND_MIN_SEPARATION 12u
#define SOUND_MINIMUM_MS 20u
#define LIGHT_PROFILE_COUNT 9u
#define SOUND_PROFILE_COUNT 3u
/*
 * Persistent profile IDs stay ABI-stable. The physical calibration selector
 * has its own user-facing order so the four classifiers after Game are:
 * Targeted New, Targeted Repeat, Whisper New, Whisper Repeat.
 */
static const uint8_t LIGHT_CALIBRATION_ORDER[LIGHT_PROFILE_COUNT] = {
    1u, 2u, 3u, 4u, 5u, 6u, 9u, 7u, 8u
};

typedef enum Phase { PHASE_READY,PHASE_LIGHT_SAMPLE,
    PHASE_SOUND_SILENCE,PHASE_SOUND_TARGET,PHASE_SOUND_COMPLETE } Phase;
typedef struct CalibrationState {
    CalibrationMode mode; Phase phase; uint8_t profile; uint8_t light_slot;
    uint32_t phase_started;
    uint16_t silence_peak,sound_peak;
    char event[192]; bool event_pending;
} CalibrationState;
static CalibrationState cal;
static void emit(const char *format,...){va_list a;va_start(a,format);vsnprintf(cal.event,sizeof(cal.event),format,a);va_end(a);cal.event_pending=true;}
static void apply_saved_light(void){for(uint8_t id=1;id<=LIGHT_PROFILE_COUNT;++id){uint32_t lo,hi;if(calibration_store_light_get(id,&lo,&hi))guard_runtime_set_profile_range(id,lo,hi);}}
void calibration_runtime_init(const AbvmVm *vm){memset(&cal,0,sizeof(cal));calibration_store_init(vm);apply_saved_light();}
bool calibration_runtime_active(void){return cal.mode!=CAL_MODE_NONE;}
CalibrationMode calibration_runtime_mode(void){return cal.mode;}
static uint8_t light_stage(void){return (uint8_t)(cal.light_slot+1u);}
static void enter_light(void){memset(&cal,0,sizeof(cal));cal.mode=CAL_MODE_LIGHT;cal.phase=PHASE_READY;cal.light_slot=0u;cal.profile=LIGHT_CALIBRATION_ORDER[0];emit("EVT|CAL|mode=ready|kind=light|stage=1|count=%u|profile=%u|id=%s|seconds=5",LIGHT_PROFILE_COUNT,cal.profile,guard_runtime_profile_name(cal.profile));}
static void enter_sound(void){memset(&cal,0,sizeof(cal));cal.mode=CAL_MODE_SOUND;cal.phase=PHASE_READY;cal.profile=1u;emit("EVT|SOUNDCAL|mode=ready|id=1|silence=3|sound=30");}
static void exit_mode(void){CalibrationMode old=cal.mode;cal.mode=CAL_MODE_NONE;cal.phase=PHASE_READY;emit("EVT|%s|mode=exited|revision=%lu",old==CAL_MODE_LIGHT?"CAL":"SOUNDCAL",(unsigned long)calibration_store_revision());}
bool calibration_runtime_blue_long(uint32_t now){(void)now;if(cal.mode==CAL_MODE_NONE){enter_light();return true;}if(cal.mode==CAL_MODE_LIGHT){exit_mode();return true;}return false;}
bool calibration_runtime_yellow_long(uint32_t now){(void)now;if(cal.mode==CAL_MODE_NONE){enter_sound();return true;}if(cal.mode==CAL_MODE_SOUND){exit_mode();return true;}return false;}
bool calibration_runtime_blue_short(uint32_t now){(void)now;if(cal.mode==CAL_MODE_LIGHT){if(cal.phase==PHASE_LIGHT_SAMPLE)return true;cal.light_slot=(uint8_t)((cal.light_slot+1u)%LIGHT_PROFILE_COUNT);cal.profile=LIGHT_CALIBRATION_ORDER[cal.light_slot];cal.phase=PHASE_READY;emit("EVT|CAL|mode=ready|kind=light|stage=%u|count=%u|profile=%u|id=%s|seconds=5",light_stage(),LIGHT_PROFILE_COUNT,cal.profile,guard_runtime_profile_name(cal.profile));return true;}if(cal.mode==CAL_MODE_SOUND){if(cal.phase==PHASE_SOUND_SILENCE||cal.phase==PHASE_SOUND_TARGET)return true;cal.profile=cal.profile>=SOUND_PROFILE_COUNT?1u:(uint8_t)(cal.profile+1u);cal.phase=PHASE_READY;emit("EVT|SOUNDCAL|mode=ready|id=%u|silence=3|sound=30",cal.profile);return true;}return false;}
bool calibration_runtime_yellow_short(uint32_t now){
    if(cal.mode==CAL_MODE_LIGHT){
        if(cal.phase==PHASE_READY){if(!light_sensor_calibration_start(LIGHT_SAMPLE_MS,now)){emit("ERR|CAL|START|kind=light");return true;}cal.phase=PHASE_LIGHT_SAMPLE;emit("EVT|CAL|mode=started|kind=light|stage=%u|count=%u|profile=%u|id=%s|seconds=5",light_stage(),LIGHT_PROFILE_COUNT,cal.profile,guard_runtime_profile_name(cal.profile));return true;}
        return true;
    }
    if(cal.mode==CAL_MODE_SOUND){if(cal.phase!=PHASE_READY&&cal.phase!=PHASE_SOUND_COMPLETE)return true;cal.silence_peak=cal.sound_peak=0u;cal.phase=PHASE_SOUND_SILENCE;cal.phase_started=now;if(!arm_uart_sound_calibration_start(now,SOUND_SAMPLE_MS)){cal.phase=PHASE_READY;emit("ERR|SOUNDCAL|START|id=%u",cal.profile);}else emit("EVT|SOUNDCAL|mode=silence|id=%u|seconds=3",cal.profile);return true;}
    return false;
}
bool calibration_runtime_light_snapshot(uint32_t lows[9],uint32_t highs[9],uint32_t centers[9]){
    for(uint8_t i=0;i<9u;i++){
        if(!guard_runtime_get_profile_range((uint8_t)(i+1u),&lows[i],&highs[i]))return false;
        if(!calibration_store_light_center((uint8_t)(i+1u),&centers[i]))
            centers[i]=lows[i]+(highs[i]-lows[i])/2u;
    }
    return true;
}
bool calibration_runtime_light_sync(uint32_t lows[9],uint32_t highs[9],uint32_t centers[9],uint16_t tolerance){
    uint16_t changed=0;uint32_t old_low[9],old_high[9],old_center[9];
    if(!calibration_runtime_light_snapshot(old_low,old_high,old_center))return false;
    if(!light_ranges_fit(lows,highs,centers,&changed))return false;
    if(!calibration_store_light_sync(0x1ffu,lows,highs,centers,tolerance))return false;
    for(uint8_t i=0;i<9u;i++)guard_runtime_set_profile_range((uint8_t)(i+1u),lows[i],highs[i]);
    return true;
}
static bool fit_and_save_light(uint32_t center,uint32_t low,uint32_t high,uint32_t samples){
    uint32_t lows[9],highs[9],centers[9];uint16_t changed=0;
    uint8_t selected=(uint8_t)(cal.profile-1u);
    if(!calibration_runtime_light_snapshot(lows,highs,centers)){
        emit("ERR|CAL|FIT|id=%s|reason=profile-missing",guard_runtime_profile_name(cal.profile));return false;
    }
    lows[selected]=low;highs[selected]=high;centers[selected]=center;
    if(!light_ranges_fit(lows,highs,centers,&changed)){
        emit("ERR|CAL|FIT|id=%s|reason=centers-too-close",guard_runtime_profile_name(cal.profile));return false;
    }
    uint16_t update_mask=(uint16_t)(changed|(1u<<selected));
    if(!calibration_store_light_sync(update_mask,lows,highs,centers,calibration_store_light_tolerance())){
        emit("ERR|CAL|SAVE|kind=light|id=%s",guard_runtime_profile_name(cal.profile));return false;
    }
    uint8_t adjusted=0;
    for(uint8_t i=0;i<9u;i++)if(update_mask&(1u<<i)){
        guard_runtime_set_profile_range((uint8_t)(i+1u),lows[i],highs[i]);
        if(i!=selected&&(changed&(1u<<i)))++adjusted;
    }
    cal.phase=PHASE_READY;
    emit("EVT|CAL|mode=saved|kind=light|stage=%u|count=%u|profile=%u|id=%s|center=%lu|low=%lu|high=%lu|samples=%lu|fit=%u|adjusted=%u|revision=%lu",
         light_stage(),LIGHT_PROFILE_COUNT,cal.profile,guard_runtime_profile_name(cal.profile),
         (unsigned long)center,(unsigned long)lows[selected],(unsigned long)highs[selected],
         (unsigned long)samples,changed?1u:0u,adjusted,(unsigned long)calibration_store_revision());
    return true;
}
static void service_light(void){
    LightCalibrationResult r;
    if(cal.mode!=CAL_MODE_LIGHT||cal.phase!=PHASE_LIGHT_SAMPLE||
       !light_sensor_calibration_take(&r))return;
    if(!r.valid||r.samples<5u){
        cal.phase=PHASE_READY;
        emit("ERR|CAL|UNSTABLE|kind=light|stage=%u|count=%u|profile=%u",
             light_stage(),LIGHT_PROFILE_COUNT,cal.profile);return;
    }
    uint32_t center=r.average_tenths;
    uint32_t tolerance=calibration_store_light_tolerance();
    uint32_t low=center>tolerance?center-tolerance:0u;
    uint32_t high=center+tolerance;
    if(high>1000000u){cal.phase=PHASE_READY;emit("ERR|CAL|FIT|reason=out-of-range");return;}
    if(!fit_and_save_light(center,low,high,r.samples))cal.phase=PHASE_READY;
}
static void request_sound(uint32_t now){if(!arm_uart_sound_calibration_start(now,SOUND_SAMPLE_MS))emit("ERR|SOUNDCAL|ARM-BUSY|id=%u",cal.profile);}
static void service_sound(uint32_t now){uint16_t avg,peak;if(cal.mode!=CAL_MODE_SOUND||!arm_uart_sound_calibration_take(&avg,&peak))return;if(cal.phase==PHASE_SOUND_SILENCE){if(avg>cal.silence_peak)cal.silence_peak=avg;if(peak>cal.silence_peak)cal.silence_peak=peak;if((int32_t)(now-(cal.phase_started+SOUND_SILENCE_MS))>=0){cal.phase=PHASE_SOUND_TARGET;cal.phase_started=now;emit("EVT|SOUNDCAL|mode=sound|id=%u|seconds=30|silence=%u",cal.profile,cal.silence_peak);}request_sound(now);return;}if(cal.phase==PHASE_SOUND_TARGET){if(avg>cal.sound_peak)cal.sound_peak=avg;if(peak>cal.sound_peak)cal.sound_peak=peak;if((int32_t)(now-(cal.phase_started+SOUND_TARGET_MS))<0){request_sound(now);return;}if(cal.sound_peak<=cal.silence_peak+SOUND_MIN_SEPARATION){cal.phase=PHASE_READY;emit("ERR|SOUNDCAL|NO-SEPARATION|id=%u|silence=%u|peak=%u",cal.profile,cal.silence_peak,cal.sound_peak);return;}uint16_t threshold=(uint16_t)((cal.silence_peak+cal.sound_peak)/2u);if(!calibration_store_sound_set(cal.profile,threshold,SOUND_MINIMUM_MS,cal.silence_peak,cal.sound_peak)){cal.phase=PHASE_READY;emit("ERR|SOUNDCAL|SAVE|id=%u",cal.profile);return;}cal.phase=PHASE_SOUND_COMPLETE;emit("EVT|SOUNDCAL|mode=saved|id=%u|threshold=%u|min=%u|silence=%u|peak=%u|revision=%lu",cal.profile,threshold,SOUND_MINIMUM_MS,cal.silence_peak,cal.sound_peak,(unsigned long)calibration_store_revision());}}
void calibration_runtime_service(uint32_t now){service_light();service_sound(now);}
bool calibration_runtime_take_event(char *out,uint32_t size){if(!cal.event_pending||!out||!size)return false;snprintf(out,size,"%s",cal.event);cal.event_pending=false;return true;}
