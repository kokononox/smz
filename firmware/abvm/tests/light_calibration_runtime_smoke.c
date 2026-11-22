#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "calibration_runtime.h"
#include "calibration_store.h"
#include "light_sensor.h"
static uint32_t lo[9]={70,120,170,390,490,590,690,790,890};
static uint32_t hi[9]={130,180,230,410,510,610,710,810,910};
static uint32_t base_lo[9],base_hi[9];
static uint32_t centers[9]={100,150,200,400,500,600,700,800,900};
static uint16_t saved_mask,margin=30;static uint32_t sample_center=100;
static bool base_available,ready,fail_save,dual_sensor_enabled;
void calibration_store_init(const AbvmVm *vm){(void)vm;}
bool calibration_store_light_get(uint8_t id,uint32_t *l,uint32_t *h){
 if(!base_available||id<1u||id>9u||!l||!h)return false;
 *l=base_lo[id-1u];*h=base_hi[id-1u];return true;
}
uint16_t calibration_store_light_tolerance(void){return margin;}
bool calibration_store_light_center(uint8_t id,uint32_t *c){
 if(id<1u||id>9u||!c)return false;
 *c=centers[id-1u];return true;
}
bool calibration_store_light_sync(uint16_t m,const uint32_t l[9],const uint32_t h[9],const uint32_t c[9],uint16_t t){
 if(fail_save)return false;
 saved_mask=m;margin=t;base_available=true;
 for(int i=0;i<9;i++)if(m&(1u<<i)){base_lo[i]=l[i];base_hi[i]=h[i];centers[i]=c[i];}
 return true;
}
bool guard_runtime_get_profile_range(uint8_t id,uint32_t *l,uint32_t *h){
 if(id<1u||id>9u)return false;
 *l=lo[id-1u];*h=hi[id-1u];return true;
}
bool guard_runtime_set_profile_range(uint8_t id,uint32_t l,uint32_t h){
 if(id<1u||id>9u)return false;
 lo[id-1u]=l;hi[id-1u]=h;return true;
}
bool guard_runtime_dual_sensor_enabled(void){return dual_sensor_enabled;}
const char *guard_runtime_profile_name(uint8_t id){(void)id;return "test";}
uint32_t calibration_store_revision(void){return 42;}
bool light_sensor_calibration_start(uint32_t duration,uint32_t now){(void)duration;(void)now;ready=true;return true;}
bool light_sensor_calibration_take(LightCalibrationResult *r){if(!ready)return false;ready=false;memset(r,0,sizeof(*r));r->valid=true;r->samples=5;r->average_tenths=sample_center;return true;}
bool arm_uart_sound_calibration_start(uint32_t n,uint16_t d){(void)n;(void)d;return false;}
bool arm_uart_sound_calibration_take(uint16_t *a,uint16_t *p){(void)a;(void)p;return false;}
bool calibration_store_sound_set(uint16_t a,uint16_t b,uint16_t c,uint16_t d,uint16_t e){(void)a;(void)b;(void)c;(void)d;(void)e;return false;}
static void assert_shared_fit(void){
 assert(lo[0]==70&&hi[0]==125);
 assert(lo[1]==126&&hi[1]==175);
 assert(lo[2]==176&&hi[2]==230);
}
int main(void){
 dual_sensor_enabled=true;
 calibration_runtime_init(NULL);
 assert(calibration_runtime_blue_long(0));
 char event[256];assert(calibration_runtime_take_event(event,sizeof(event)));
 assert(strcmp(event,"ERR|CAL|DUAL-REQUIRES-STUDIO")==0);
 assert(!ready);
 dual_sensor_enabled=false;
 calibration_runtime_init(NULL);assert(calibration_runtime_blue_long(0));
 assert(calibration_runtime_yellow_short(1));calibration_runtime_service(5001);
 assert_shared_fit();assert((saved_mask&0x1ffu)==0x1ffu&&margin==30);
 /* Persistent inputs stay untrimmed; the runtime holds the fitted intervals. */
 assert(base_lo[0]==70&&base_hi[0]==130);
 assert(base_lo[1]==120&&base_hi[1]==180);
 assert(calibration_runtime_take_event(event,sizeof(event)));
 assert(strstr(event,"changed=1,2,3")!=NULL);

 /* A failed flash write must not alter the active fitted ranges. */
 fail_save=true;uint32_t before=hi[0];
 assert(calibration_runtime_yellow_short(6000));calibration_runtime_service(11000);
 assert(hi[0]==before);fail_save=false;

 /* Recalibrating B and C in turn gives the same balanced fit, not cumulative trimming. */
 assert(calibration_runtime_blue_short(12000));sample_center=150;
 assert(calibration_runtime_yellow_short(12001));calibration_runtime_service(17001);
 assert_shared_fit();
 assert(calibration_runtime_blue_short(18000));sample_center=200;
 assert(calibration_runtime_yellow_short(18001));calibration_runtime_service(23001);
 assert_shared_fit();
 /* Non-overlapping profiles retain the configured full +/-2.0-lux range. */
 assert(calibration_runtime_blue_short(24000));margin=20;sample_center=400;
 assert(calibration_runtime_yellow_short(24001));calibration_runtime_service(29001);
 assert(lo[3]==380&&hi[3]==420&&base_lo[3]==380&&base_hi[3]==420&&margin==20);

 /* A reboot reconstructs the same fit from the saved nominal ranges. */
 calibration_runtime_init(NULL);assert_shared_fit();
 /* Importing the fitted board ranges and sending them back must preserve the
  * original nominal ranges instead of cementing the trimmed values. */
 uint32_t sync_low[9],sync_high[9],sync_center[9];
 assert(calibration_runtime_light_snapshot(sync_low,sync_high,sync_center));
 assert(calibration_runtime_light_sync(sync_low,sync_high,sync_center,margin));
 assert_shared_fit();assert(base_lo[0]==70&&base_hi[0]==130);
 puts("physical calibration: shared midpoint ranges, stable centres, refit, persistence passed");
 return 0;
}
