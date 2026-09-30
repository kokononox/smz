#include "calibration_runtime.h"
#include <stdarg.h>
#include <stdio.h>
#include <string.h>
#include "arm_uart_mouse.h"
#include "calibration_store.h"
#include "guard_runtime.h"
#include "light_sensor.h"

#define LIGHT_SAMPLE_MS 5000u
#define SOUND_SAMPLE_MS 250u
#define SOUND_SILENCE_MS 3000u
#define SOUND_TARGET_MS 30000u
#define SOUND_MIN_SEPARATION 12u
#define SOUND_MINIMUM_MS 20u
#define LIGHT_PROFILE_COUNT 7u
#define LIGHT_FIT_GAP_TENTHS 3u
#define LIGHT_FIT_MIN_TENTHS 5u

typedef enum Phase { PHASE_READY,PHASE_LIGHT_SAMPLE,
    PHASE_SOUND_SILENCE,PHASE_SOUND_TARGET,PHASE_SOUND_COMPLETE } Phase;
typedef struct CalibrationState {
    CalibrationMode mode; Phase phase; uint8_t profile;
    uint32_t phase_started;
    uint16_t silence_peak,sound_peak;
    char event[192]; bool event_pending;
} CalibrationState;
static CalibrationState cal;
static void emit(const char *format,...){va_list a;va_start(a,format);vsnprintf(cal.event,sizeof(cal.event),format,a);va_end(a);cal.event_pending=true;}
static void apply_saved_light(void){for(uint8_t id=1;id<=7u;++id){uint32_t lo,hi;if(calibration_store_light_get(id,&lo,&hi))guard_runtime_set_profile_range(id,lo,hi);}}
void calibration_runtime_init(const AbvmVm *vm){memset(&cal,0,sizeof(cal));calibration_store_init(vm);apply_saved_light();}
bool calibration_runtime_active(void){return cal.mode!=CAL_MODE_NONE;}
CalibrationMode calibration_runtime_mode(void){return cal.mode;}
static void enter_light(void){memset(&cal,0,sizeof(cal));cal.mode=CAL_MODE_LIGHT;cal.phase=PHASE_READY;cal.profile=1u;emit("EVT|CAL|mode=ready|kind=light|stage=1|id=%s|seconds=5",guard_runtime_profile_name(1));}
static void enter_sound(void){memset(&cal,0,sizeof(cal));cal.mode=CAL_MODE_SOUND;cal.phase=PHASE_READY;cal.profile=1u;emit("EVT|SOUNDCAL|mode=ready|id=1|silence=3|sound=30");}
static void exit_mode(void){CalibrationMode old=cal.mode;cal.mode=CAL_MODE_NONE;cal.phase=PHASE_READY;emit("EVT|%s|mode=exited|revision=%lu",old==CAL_MODE_LIGHT?"CAL":"SOUNDCAL",(unsigned long)calibration_store_revision());}
bool calibration_runtime_blue_long(uint32_t now){(void)now;if(cal.mode==CAL_MODE_NONE){enter_light();return true;}if(cal.mode==CAL_MODE_LIGHT){exit_mode();return true;}return false;}
bool calibration_runtime_yellow_long(uint32_t now){(void)now;if(cal.mode==CAL_MODE_NONE){enter_sound();return true;}if(cal.mode==CAL_MODE_SOUND){exit_mode();return true;}return false;}
bool calibration_runtime_blue_short(uint32_t now){(void)now;if(cal.mode==CAL_MODE_LIGHT){if(cal.phase==PHASE_LIGHT_SAMPLE)return true;cal.profile=cal.profile>=7u?1u:(uint8_t)(cal.profile+1u);cal.phase=PHASE_READY;emit("EVT|CAL|mode=ready|kind=light|stage=%u|id=%s|seconds=5",cal.profile,guard_runtime_profile_name(cal.profile));return true;}if(cal.mode==CAL_MODE_SOUND){if(cal.phase==PHASE_SOUND_SILENCE||cal.phase==PHASE_SOUND_TARGET)return true;cal.profile=cal.profile==1u?2u:1u;cal.phase=PHASE_READY;emit("EVT|SOUNDCAL|mode=ready|id=%u|silence=3|sound=30",cal.profile);return true;}return false;}
bool calibration_runtime_yellow_short(uint32_t now){
    if(cal.mode==CAL_MODE_LIGHT){
        if(cal.phase==PHASE_READY){if(!light_sensor_calibration_start(LIGHT_SAMPLE_MS,now)){emit("ERR|CAL|START|kind=light");return true;}cal.phase=PHASE_LIGHT_SAMPLE;emit("EVT|CAL|mode=started|kind=light|stage=%u|id=%s|seconds=5",cal.profile,guard_runtime_profile_name(cal.profile));return true;}
        return true;
    }
    if(cal.mode==CAL_MODE_SOUND){if(cal.phase!=PHASE_READY&&cal.phase!=PHASE_SOUND_COMPLETE)return true;cal.silence_peak=cal.sound_peak=0u;cal.phase=PHASE_SOUND_SILENCE;cal.phase_started=now;if(!arm_uart_sound_calibration_start(now,SOUND_SAMPLE_MS)){cal.phase=PHASE_READY;emit("ERR|SOUNDCAL|START|id=%u",cal.profile);}else emit("EVT|SOUNDCAL|mode=silence|id=%u|seconds=3",cal.profile);return true;}
    return false;
}
static uint32_t maximum(uint32_t a,uint32_t b){return a>b?a:b;}
static uint32_t minimum(uint32_t a,uint32_t b){return a<b?a:b;}
static uint32_t distance(uint32_t a,uint32_t b){return a>b?a-b:b-a;}
static void range_from_center(uint32_t center,uint32_t tolerance,
                              uint32_t *low,uint32_t *high){
    *low=center>tolerance?center-tolerance:0u;
    *high=center+tolerance;
}
static bool fit_and_save_light(uint32_t center,uint32_t low,uint32_t high,
                               uint32_t samples){
    uint32_t lows[LIGHT_PROFILE_COUNT],highs[LIGHT_PROFILE_COUNT];
    uint32_t centers[LIGHT_PROFILE_COUNT],tolerances[LIGHT_PROFILE_COUNT];
    uint8_t selected=(uint8_t)(cal.profile-1u);
    uint8_t update_mask=(uint8_t)(1u<<selected),adjusted=0u;
    bool fitted=false;
    for(uint8_t i=0;i<LIGHT_PROFILE_COUNT;++i){
        if(!guard_runtime_get_profile_range((uint8_t)(i+1u),&lows[i],&highs[i])){
            emit("ERR|CAL|FIT|id=%s|reason=profile-missing",
                 guard_runtime_profile_name(cal.profile));return false;
        }
        centers[i]=lows[i]+(highs[i]-lows[i])/2u;
        tolerances[i]=maximum(centers[i]-lows[i],highs[i]-centers[i]);
    }
    lows[selected]=low;highs[selected]=high;centers[selected]=center;
    tolerances[selected]=maximum(center-low,high-center);
    if(tolerances[selected]<LIGHT_FIT_MIN_TENTHS){
        emit("ERR|CAL|FIT|id=%s|reason=candidate-minimum|gap=%u",
             guard_runtime_profile_name(cal.profile),LIGHT_FIT_GAP_TENTHS);
        return false;
    }
    for(uint8_t i=0;i<LIGHT_PROFILE_COUNT;++i){
        if(i==selected)continue;
        uint32_t apart=distance(centers[selected],centers[i]);
        uint32_t allowance=apart>LIGHT_FIT_GAP_TENTHS?
            apart-LIGHT_FIT_GAP_TENTHS:0u;
        uint32_t combined=tolerances[selected]+tolerances[i];
        if(combined<=allowance)continue;
        uint32_t excess=combined-allowance;
        uint32_t available=tolerances[selected]>LIGHT_FIT_MIN_TENTHS?
            tolerances[selected]-LIGHT_FIT_MIN_TENTHS:0u;
        uint32_t take=minimum(excess,available);
        tolerances[selected]-=take;excess-=take;
        if(take)fitted=true;
        available=tolerances[i]>LIGHT_FIT_MIN_TENTHS?
            tolerances[i]-LIGHT_FIT_MIN_TENTHS:0u;
        take=minimum(excess,available);
        tolerances[i]-=take;excess-=take;
        if(take){
            fitted=true;++adjusted;update_mask|=(uint8_t)(1u<<i);
            range_from_center(centers[i],tolerances[i],&lows[i],&highs[i]);
        }
        if(excess){
            emit("ERR|CAL|FIT|id=%s|with=%s|reason=centers-too-close|gap=%u",
                 guard_runtime_profile_name(cal.profile),
                 guard_runtime_profile_name((uint8_t)(i+1u)),
                 LIGHT_FIT_GAP_TENTHS);
            return false;
        }
    }
    range_from_center(center,tolerances[selected],&lows[selected],&highs[selected]);
    if(!calibration_store_light_update(update_mask,lows,highs)){
        emit("ERR|CAL|SAVE|kind=light|id=%s",
             guard_runtime_profile_name(cal.profile));return false;
    }
    for(uint8_t i=0;i<LIGHT_PROFILE_COUNT;++i)
        if(update_mask&(1u<<i))
            guard_runtime_set_profile_range((uint8_t)(i+1u),lows[i],highs[i]);
    cal.phase=PHASE_READY;
    emit("EVT|CAL|mode=saved|kind=light|stage=%u|id=%s|center=%lu|low=%lu|high=%lu|samples=%lu|fit=%u|adjusted=%u|revision=%lu",
         cal.profile,guard_runtime_profile_name(cal.profile),(unsigned long)center,
         (unsigned long)lows[selected],(unsigned long)highs[selected],
         (unsigned long)samples,fitted?1u:0u,adjusted,
         (unsigned long)calibration_store_revision());
    return true;
}
static void service_light(void){
    LightCalibrationResult r;
    if(cal.mode!=CAL_MODE_LIGHT||cal.phase!=PHASE_LIGHT_SAMPLE||
       !light_sensor_calibration_take(&r))return;
    if(!r.valid||r.samples<5u){
        cal.phase=PHASE_READY;
        emit("ERR|CAL|UNSTABLE|kind=light|stage=%u",cal.profile);return;
    }
    uint32_t center=r.average_lux*10u;
    uint32_t spread=(r.maximum_lux-r.minimum_lux)*10u;
    uint32_t tolerance=spread/2u+10u;
    if(tolerance<10u)tolerance=10u;
    uint32_t low=center>tolerance?center-tolerance:0u;
    uint32_t high=center+tolerance;
    if(!fit_and_save_light(center,low,high,r.samples))cal.phase=PHASE_READY;
}
static void request_sound(uint32_t now){if(!arm_uart_sound_calibration_start(now,SOUND_SAMPLE_MS))emit("ERR|SOUNDCAL|ARM-BUSY|id=%u",cal.profile);}
static void service_sound(uint32_t now){uint16_t avg,peak;if(cal.mode!=CAL_MODE_SOUND||!arm_uart_sound_calibration_take(&avg,&peak))return;if(cal.phase==PHASE_SOUND_SILENCE){if(avg>cal.silence_peak)cal.silence_peak=avg;if(peak>cal.silence_peak)cal.silence_peak=peak;if((int32_t)(now-(cal.phase_started+SOUND_SILENCE_MS))>=0){cal.phase=PHASE_SOUND_TARGET;cal.phase_started=now;emit("EVT|SOUNDCAL|mode=sound|id=%u|seconds=30|silence=%u",cal.profile,cal.silence_peak);}request_sound(now);return;}if(cal.phase==PHASE_SOUND_TARGET){if(avg>cal.sound_peak)cal.sound_peak=avg;if(peak>cal.sound_peak)cal.sound_peak=peak;if((int32_t)(now-(cal.phase_started+SOUND_TARGET_MS))<0){request_sound(now);return;}if(cal.sound_peak<=cal.silence_peak+SOUND_MIN_SEPARATION){cal.phase=PHASE_READY;emit("ERR|SOUNDCAL|NO-SEPARATION|id=%u|silence=%u|peak=%u",cal.profile,cal.silence_peak,cal.sound_peak);return;}uint16_t threshold=(uint16_t)((cal.silence_peak+cal.sound_peak)/2u);if(!calibration_store_sound_set(cal.profile,threshold,SOUND_MINIMUM_MS,cal.silence_peak,cal.sound_peak)){cal.phase=PHASE_READY;emit("ERR|SOUNDCAL|SAVE|id=%u",cal.profile);return;}cal.phase=PHASE_SOUND_COMPLETE;emit("EVT|SOUNDCAL|mode=saved|id=%u|threshold=%u|min=%u|silence=%u|peak=%u|revision=%lu",cal.profile,threshold,SOUND_MINIMUM_MS,cal.silence_peak,cal.sound_peak,(unsigned long)calibration_store_revision());}}
void calibration_runtime_service(uint32_t now){service_light();service_sound(now);}
bool calibration_runtime_take_event(char *out,uint32_t size){if(!cal.event_pending||!out||!size)return false;snprintf(out,size,"%s",cal.event);cal.event_pending=false;return true;}
