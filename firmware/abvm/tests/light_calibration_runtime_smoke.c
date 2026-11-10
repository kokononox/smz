#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "calibration_runtime.h"
#include "calibration_store.h"
#include "light_sensor.h"
static uint32_t lo[9]={80,110,290,390,490,590,690,790,890};
static uint32_t hi[9]={120,150,310,410,510,610,710,810,910};
static uint32_t centers[9]={100,130,300,400,500,600,700,800,900};
static uint16_t saved_mask,margin=20;static bool ready,fail_save;
void calibration_store_init(const AbvmVm *vm){(void)vm;}
bool calibration_store_light_get(uint8_t id,uint32_t *l,uint32_t *h){(void)id;(void)l;(void)h;return false;}
uint16_t calibration_store_light_tolerance(void){return margin;}
bool calibration_store_light_center(uint8_t id,uint32_t *c){*c=centers[id-1];return true;}
bool calibration_store_light_sync(uint16_t m,const uint32_t l[9],const uint32_t h[9],const uint32_t c[9],uint16_t t){
 if(fail_save)return false;
 saved_mask=m;margin=t;
 for(int i=0;i<9;i++)if(m&(1u<<i)){lo[i]=l[i];hi[i]=h[i];centers[i]=c[i];}
 return true;
}
bool guard_runtime_get_profile_range(uint8_t id,uint32_t *l,uint32_t *h){*l=lo[id-1];*h=hi[id-1];return true;}
bool guard_runtime_set_profile_range(uint8_t id,uint32_t l,uint32_t h){lo[id-1]=l;hi[id-1]=h;return true;}
const char *guard_runtime_profile_name(uint8_t id){(void)id;return "test";}
uint32_t calibration_store_revision(void){return 42;}
bool light_sensor_calibration_start(uint32_t duration,uint32_t now){(void)duration;(void)now;ready=true;return true;}
bool light_sensor_calibration_take(LightCalibrationResult *r){if(!ready)return false;ready=false;memset(r,0,sizeof(*r));r->valid=true;r->samples=5;r->average_tenths=100;return true;}
bool arm_uart_sound_calibration_start(uint32_t n,uint16_t d){(void)n;(void)d;return false;}
bool arm_uart_sound_calibration_take(uint16_t *a,uint16_t *p){(void)a;(void)p;return false;}
bool calibration_store_sound_set(uint16_t a,uint16_t b,uint16_t c,uint16_t d,uint16_t e){(void)a;(void)b;(void)c;(void)d;(void)e;return false;}
int main(void){
 calibration_runtime_init(NULL);assert(calibration_runtime_blue_long(0));assert(calibration_runtime_yellow_short(1));
 calibration_runtime_service(5001);
 assert(lo[0]==80&&hi[0]==115&&lo[1]==116&&hi[1]==150&&centers[0]==100&&centers[1]==130);
 assert((saved_mask&3)==3&&margin==20);
 /* A failed flash write must not apply the candidate to the running guard. */
 fail_save=true;uint32_t before=hi[0];assert(calibration_runtime_yellow_short(6000));calibration_runtime_service(11000);assert(hi[0]==before);
 puts("physical calibration: centre +/- default, fit, atomic failure passed");return 0;
}
