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

typedef enum Phase { PHASE_READY,PHASE_LIGHT_SAMPLE,PHASE_LIGHT_RESULT,
    PHASE_SOUND_SILENCE,PHASE_SOUND_TARGET,PHASE_SOUND_COMPLETE } Phase;
typedef struct CalibrationState {
    CalibrationMode mode; Phase phase; uint8_t profile;
    uint32_t phase_started; uint32_t light_low,light_high;
    uint16_t silence_peak,sound_peak;
    char event[192]; bool event_pending;
} CalibrationState;
static CalibrationState cal;
static void emit(const char *format,...){va_list a;va_start(a,format);vsnprintf(cal.event,sizeof(cal.event),format,a);va_end(a);cal.event_pending=true;}
static void apply_saved_light(void){for(uint8_t id=1;id<=6u;++id){uint32_t lo,hi;if(calibration_store_light_get(id,&lo,&hi))guard_runtime_set_profile_range(id,lo,hi);}}
void calibration_runtime_init(const AbvmVm *vm){memset(&cal,0,sizeof(cal));calibration_store_init(vm);apply_saved_light();}
bool calibration_runtime_active(void){return cal.mode!=CAL_MODE_NONE;}
CalibrationMode calibration_runtime_mode(void){return cal.mode;}
static void enter_light(void){memset(&cal,0,sizeof(cal));cal.mode=CAL_MODE_LIGHT;cal.phase=PHASE_READY;cal.profile=1u;emit("EVT|CAL|mode=ready|kind=light|stage=1|id=%s|seconds=5",guard_runtime_profile_name(1));}
static void enter_sound(void){memset(&cal,0,sizeof(cal));cal.mode=CAL_MODE_SOUND;cal.phase=PHASE_READY;cal.profile=1u;emit("EVT|SOUNDCAL|mode=ready|id=1|silence=3|sound=30");}
static void exit_mode(void){CalibrationMode old=cal.mode;cal.mode=CAL_MODE_NONE;cal.phase=PHASE_READY;emit("EVT|%s|mode=exited|revision=%lu",old==CAL_MODE_LIGHT?"CAL":"SOUNDCAL",(unsigned long)calibration_store_revision());}
bool calibration_runtime_blue_long(uint32_t now){(void)now;if(cal.mode==CAL_MODE_NONE){enter_light();return true;}if(cal.mode==CAL_MODE_LIGHT){exit_mode();return true;}return false;}
bool calibration_runtime_yellow_long(uint32_t now){(void)now;if(cal.mode==CAL_MODE_NONE){enter_sound();return true;}if(cal.mode==CAL_MODE_SOUND){exit_mode();return true;}return false;}
bool calibration_runtime_blue_short(uint32_t now){(void)now;if(cal.mode==CAL_MODE_LIGHT){if(cal.phase==PHASE_LIGHT_SAMPLE)return true;cal.profile=cal.profile>=6u?1u:(uint8_t)(cal.profile+1u);cal.phase=PHASE_READY;emit("EVT|CAL|mode=ready|kind=light|stage=%u|id=%s|seconds=5",cal.profile,guard_runtime_profile_name(cal.profile));return true;}if(cal.mode==CAL_MODE_SOUND){if(cal.phase==PHASE_SOUND_SILENCE||cal.phase==PHASE_SOUND_TARGET)return true;cal.profile=cal.profile==1u?2u:1u;cal.phase=PHASE_READY;emit("EVT|SOUNDCAL|mode=ready|id=%u|silence=3|sound=30",cal.profile);return true;}return false;}
bool calibration_runtime_yellow_short(uint32_t now){
    if(cal.mode==CAL_MODE_LIGHT){
        if(cal.phase==PHASE_READY){if(!light_sensor_calibration_start(LIGHT_SAMPLE_MS,now)){emit("ERR|CAL|START|kind=light");return true;}cal.phase=PHASE_LIGHT_SAMPLE;emit("EVT|CAL|mode=started|kind=light|stage=%u|id=%s|seconds=5",cal.profile,guard_runtime_profile_name(cal.profile));return true;}
        if(cal.phase==PHASE_LIGHT_RESULT){for(uint8_t id=1;id<=6u;++id){uint32_t lo,hi;if(id!=cal.profile&&calibration_store_light_get(id,&lo,&hi)&&!(cal.light_high<lo||cal.light_low>hi)){emit("ERR|CAL|OVERLAP|id=%s|with=%s",guard_runtime_profile_name(cal.profile),guard_runtime_profile_name(id));return true;}}
            if(!calibration_store_light_set(cal.profile,cal.light_low,cal.light_high)){emit("ERR|CAL|SAVE|kind=light");return true;}guard_runtime_set_profile_range(cal.profile,cal.light_low,cal.light_high);cal.phase=PHASE_READY;emit("EVT|CAL|mode=saved|kind=light|id=%s|low=%lu|high=%lu|revision=%lu",guard_runtime_profile_name(cal.profile),(unsigned long)cal.light_low,(unsigned long)cal.light_high,(unsigned long)calibration_store_revision());return true;}
        return true;
    }
    if(cal.mode==CAL_MODE_SOUND){if(cal.phase!=PHASE_READY&&cal.phase!=PHASE_SOUND_COMPLETE)return true;cal.silence_peak=cal.sound_peak=0u;cal.phase=PHASE_SOUND_SILENCE;cal.phase_started=now;if(!arm_uart_sound_calibration_start(now,SOUND_SAMPLE_MS)){cal.phase=PHASE_READY;emit("ERR|SOUNDCAL|START|id=%u",cal.profile);}else emit("EVT|SOUNDCAL|mode=silence|id=%u|seconds=3",cal.profile);return true;}
    return false;
}
static void service_light(void){LightCalibrationResult r;if(cal.mode!=CAL_MODE_LIGHT||cal.phase!=PHASE_LIGHT_SAMPLE||!light_sensor_calibration_take(&r))return;if(!r.valid||r.samples<5u){cal.phase=PHASE_READY;emit("ERR|CAL|UNSTABLE|kind=light|stage=%u",cal.profile);return;}uint32_t center=r.average_lux*10u;uint32_t spread=(r.maximum_lux-r.minimum_lux)*10u;uint32_t tolerance=spread/2u+10u;if(tolerance<10u)tolerance=10u;cal.light_low=center>tolerance?center-tolerance:0u;cal.light_high=center+tolerance;cal.phase=PHASE_LIGHT_RESULT;emit("EVT|CAL|mode=complete|kind=light|stage=%u|id=%s|center=%lu|low=%lu|high=%lu|samples=%lu|saved=0",cal.profile,guard_runtime_profile_name(cal.profile),(unsigned long)center,(unsigned long)cal.light_low,(unsigned long)cal.light_high,(unsigned long)r.samples);}
static void request_sound(uint32_t now){if(!arm_uart_sound_calibration_start(now,SOUND_SAMPLE_MS))emit("ERR|SOUNDCAL|ARM-BUSY|id=%u",cal.profile);}
static void service_sound(uint32_t now){uint16_t avg,peak;if(cal.mode!=CAL_MODE_SOUND||!arm_uart_sound_calibration_take(&avg,&peak))return;if(cal.phase==PHASE_SOUND_SILENCE){if(avg>cal.silence_peak)cal.silence_peak=avg;if(peak>cal.silence_peak)cal.silence_peak=peak;if((int32_t)(now-(cal.phase_started+SOUND_SILENCE_MS))>=0){cal.phase=PHASE_SOUND_TARGET;cal.phase_started=now;emit("EVT|SOUNDCAL|mode=sound|id=%u|seconds=30|silence=%u",cal.profile,cal.silence_peak);}request_sound(now);return;}if(cal.phase==PHASE_SOUND_TARGET){if(avg>cal.sound_peak)cal.sound_peak=avg;if(peak>cal.sound_peak)cal.sound_peak=peak;if((int32_t)(now-(cal.phase_started+SOUND_TARGET_MS))<0){request_sound(now);return;}if(cal.sound_peak<=cal.silence_peak+SOUND_MIN_SEPARATION){cal.phase=PHASE_READY;emit("ERR|SOUNDCAL|NO-SEPARATION|id=%u|silence=%u|peak=%u",cal.profile,cal.silence_peak,cal.sound_peak);return;}uint16_t threshold=(uint16_t)((cal.silence_peak+cal.sound_peak)/2u);if(!calibration_store_sound_set(cal.profile,threshold,SOUND_MINIMUM_MS,cal.silence_peak,cal.sound_peak)){cal.phase=PHASE_READY;emit("ERR|SOUNDCAL|SAVE|id=%u",cal.profile);return;}cal.phase=PHASE_SOUND_COMPLETE;emit("EVT|SOUNDCAL|mode=saved|id=%u|threshold=%u|min=%u|silence=%u|peak=%u|revision=%lu",cal.profile,threshold,SOUND_MINIMUM_MS,cal.silence_peak,cal.sound_peak,(unsigned long)calibration_store_revision());}}
void calibration_runtime_service(uint32_t now){service_light();service_sound(now);}
bool calibration_runtime_take_event(char *out,uint32_t size){if(!cal.event_pending||!out||!size)return false;snprintf(out,size,"%s",cal.event);cal.event_pending=false;return true;}
