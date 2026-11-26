#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "calibration_runtime.h"
#include "calibration_store.h"
#include "guard_runtime.h"
#include "light_sensor.h"
static uint32_t lo[9]={70,120,170,390,490,590,690,790,890};
static uint32_t hi[9]={130,180,230,410,510,610,710,810,910};
static uint32_t base_lo[9],base_hi[9];
static uint32_t centers[9]={100,150,200,400,500,600,700,800,900};
static uint16_t saved_mask,margin=30;static uint32_t sample_center=100;
static bool base_available,ready,fail_save,dual_sensor_enabled;
static bool pair_result;
static uint32_t pair_sample_mean=2050u;
static int32_t pair_sample_difference=110;
static uint32_t pair_mean[9]={2000,4000,6000,8000,10000,12000,14000,16000,18000};
static uint32_t pair_mean_tolerance[9]={40,40,40,40,40,40,40,40,40};
static int32_t pair_difference[9]={100,200,300,400,500,600,700,800,900};
static uint32_t pair_difference_tolerance[9]={20,20,20,20,20,20,20,20,20};
static uint16_t pair_saved_mask,pair_tolerance_saved_mask,pair_calibrated_mask;
static uint32_t pair_saved_mean[9];
static uint32_t pair_saved_mean_tolerance[9];
static int32_t pair_saved_difference[9];
static uint32_t pair_saved_difference_tolerance[9];
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
bool calibration_store_dual_light_get(uint8_t id,uint32_t *m,int32_t *d){
 if(id<1u||id>9u||!m||!d||!(pair_saved_mask&(1u<<(id-1u))))return false;
 *m=pair_saved_mean[id-1u];*d=pair_saved_difference[id-1u];return true;
}
bool calibration_store_dual_light_tolerance_get(uint8_t id,uint32_t *mt,uint32_t *dt){
 if(id<1u||id>9u||!mt||!dt||!(pair_tolerance_saved_mask&(1u<<(id-1u))))return false;
 *mt=pair_saved_mean_tolerance[id-1u];*dt=pair_saved_difference_tolerance[id-1u];return true;
}
bool calibration_store_dual_light_sync(uint16_t mask,const uint32_t m[9],
 const uint32_t mt[9],const int32_t d[9],const uint32_t dt[9]){
 if(fail_save)return false;
 for(int i=0;i<9;i++)if(mask&(1u<<i)){
  pair_saved_mean[i]=m[i];pair_saved_mean_tolerance[i]=mt[i];
  pair_saved_difference[i]=d[i];pair_saved_difference_tolerance[i]=dt[i];
 }
 pair_saved_mask|=mask;pair_tolerance_saved_mask|=mask;return true;
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
uint16_t guard_runtime_dual_calibration_mask(void){
 return dual_sensor_enabled?pair_calibrated_mask:0u;
}
bool guard_runtime_profile_pair_calibrated(uint8_t id){
 return dual_sensor_enabled&&id>=1u&&id<=9u&&
        (pair_calibrated_mask&(uint16_t)(1u<<(id-1u)))!=0u;
}
bool guard_runtime_get_profile_pair_calibration(uint8_t id,uint32_t *m,uint32_t *mt,
                                                int32_t *d,uint32_t *dt){
 if(!dual_sensor_enabled||id<1u||id>9u||!m||!mt||!d||!dt)return false;
 *m=pair_mean[id-1u];*mt=pair_mean_tolerance[id-1u];
 *d=pair_difference[id-1u];*dt=pair_difference_tolerance[id-1u];return true;
}
bool guard_runtime_validate_profile_pair_centers_mask(uint16_t mask,
 const uint32_t means[9],const int32_t differences[9]){
 return guard_runtime_validate_profile_pair_calibrations_mask(mask,means,
  pair_mean_tolerance,differences,pair_difference_tolerance);
}
bool guard_runtime_validate_profile_pair_calibrations_mask(uint16_t mask,
 const uint32_t means[9],const uint32_t mean_tolerances[9],
 const int32_t differences[9],const uint32_t difference_tolerances[9]){
 if(!dual_sensor_enabled||!means||!mean_tolerances||!differences||
    !difference_tolerances||(mask&~0x1ffu))return false;
 for(int i=0;i<9;i++)if(mask&(uint16_t)(1u<<i)){
  if(means[i]>20000000u||differences[i]<-10000000||differences[i]>10000000)
   return false;
  for(int j=i+1;j<9;j++)if(mask&(uint16_t)(1u<<j)){
   uint32_t dm=means[i]>means[j]?means[i]-means[j]:means[j]-means[i];
   int64_t raw=(int64_t)differences[i]-differences[j];
   uint32_t dd=(uint32_t)(raw<0?-raw:raw);
   if(dm<=mean_tolerances[i]+mean_tolerances[j]+20u&&
      dd<=difference_tolerances[i]+difference_tolerances[j]+10u)
    return false;
  }
 }
 return true;
}
bool guard_runtime_validate_profile_pair_centers(const uint32_t means[9],
                                                  const int32_t differences[9]){
 return guard_runtime_validate_profile_pair_centers_mask(0x1ffu,means,differences);
}
bool guard_runtime_set_profile_pair_centers_mask(uint16_t update_mask,
 const uint32_t means[9],const int32_t differences[9]){
 return guard_runtime_set_profile_pair_calibrations_mask(update_mask,means,
  pair_mean_tolerance,differences,pair_difference_tolerance);
}
bool guard_runtime_set_profile_pair_calibrations_mask(uint16_t update_mask,
 const uint32_t means[9],const uint32_t mean_tolerances[9],
 const int32_t differences[9],const uint32_t difference_tolerances[9]){
 if(!dual_sensor_enabled||!update_mask||(update_mask&~0x1ffu)||!means||
    !mean_tolerances||!differences||!difference_tolerances)
  return false;
 uint32_t merged_means[9],merged_mean_tolerances[9];
 int32_t merged_differences[9];uint32_t merged_difference_tolerances[9];
 memcpy(merged_means,pair_mean,sizeof(merged_means));
 memcpy(merged_mean_tolerances,pair_mean_tolerance,sizeof(merged_mean_tolerances));
 memcpy(merged_differences,pair_difference,sizeof(merged_differences));
 memcpy(merged_difference_tolerances,pair_difference_tolerance,sizeof(merged_difference_tolerances));
 for(int i=0;i<9;i++)if(update_mask&(uint16_t)(1u<<i)){
  merged_means[i]=means[i];merged_mean_tolerances[i]=mean_tolerances[i];
  merged_differences[i]=differences[i];
  merged_difference_tolerances[i]=difference_tolerances[i];
 }
 uint16_t mask=(uint16_t)(pair_calibrated_mask|update_mask);
 if(!guard_runtime_validate_profile_pair_calibrations_mask(mask,merged_means,
   merged_mean_tolerances,merged_differences,merged_difference_tolerances))return false;
 memcpy(pair_mean,merged_means,sizeof(pair_mean));
 memcpy(pair_mean_tolerance,merged_mean_tolerances,sizeof(pair_mean_tolerance));
 memcpy(pair_difference,merged_differences,sizeof(pair_difference));
 memcpy(pair_difference_tolerance,merged_difference_tolerances,
        sizeof(pair_difference_tolerance));
 pair_calibrated_mask=mask;
 return true;
}
bool guard_runtime_set_profile_pair_centers_batch(const uint32_t means[9],
                                                   const int32_t differences[9]){
 return guard_runtime_set_profile_pair_centers_mask(0x1ffu,means,differences);
}
bool guard_runtime_validate_profile_pair_center(uint8_t id,uint32_t mean,
                                                 int32_t difference){
 if(!dual_sensor_enabled||id<1u||id>9u)return false;
 uint32_t means[9];int32_t differences[9];
 memcpy(means,pair_mean,sizeof(means));
 memcpy(differences,pair_difference,sizeof(differences));
 means[id-1u]=mean;differences[id-1u]=difference;
 return guard_runtime_validate_profile_pair_centers_mask(
  (uint16_t)(pair_calibrated_mask|(uint16_t)(1u<<(id-1u))),means,differences);
}
bool guard_runtime_set_profile_pair_centers(uint8_t id,uint32_t mean,
                                             int32_t difference){
 if(!guard_runtime_validate_profile_pair_center(id,mean,difference))return false;
 uint32_t means[9];int32_t differences[9];
 memcpy(means,pair_mean,sizeof(means));
 memcpy(differences,pair_difference,sizeof(differences));
 means[id-1u]=mean;differences[id-1u]=difference;
 return guard_runtime_set_profile_pair_centers_mask(
  (uint16_t)(1u<<(id-1u)),means,differences);
}
const char *guard_runtime_profile_name(uint8_t id){(void)id;return "test";}
uint32_t calibration_store_revision(void){return 42;}
bool light_sensor_calibration_start(uint32_t duration,uint32_t now){(void)duration;(void)now;ready=true;return true;}
bool light_sensor_pair_calibration_start(uint32_t duration,uint32_t now){(void)duration;(void)now;ready=true;pair_result=true;return true;}
bool light_sensor_calibration_take(LightCalibrationResult *r){if(!ready)return false;ready=false;memset(r,0,sizeof(*r));r->valid=true;r->samples=5;r->average_tenths=sample_center;r->dual_sensor=pair_result;if(pair_result){r->mean_center_half_tenths=pair_sample_mean;r->difference_center_tenths=pair_sample_difference;r->average_primary_tenths=1200u;r->average_secondary_tenths=1300u;}pair_result=false;return true;}
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
 assert(strstr(event,"EVT|CAL|mode=ready|kind=light|dual=1")!=NULL);
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

 /* Paired physical calibration stores both feature centers for one profile
  * while preserving the descriptor's two-axis tolerance ellipse. */
 dual_sensor_enabled=true;pair_mean[0]=2000u;pair_difference[0]=100;
 pair_calibrated_mask=1u;
 calibration_runtime_init(NULL);assert(calibration_runtime_blue_long(30000u));
 assert(calibration_runtime_take_event(event,sizeof(event))&&strstr(event,"dual=1"));
 assert(calibration_runtime_yellow_short(30001u));
 assert(calibration_runtime_take_event(event,sizeof(event))&&
        strstr(event,"mode=started|kind=light|dual=1"));
 calibration_runtime_service(35001u);
 assert(calibration_runtime_take_event(event,sizeof(event))&&
        strstr(event,"mode=saved|kind=light|dual=1"));
 assert(strstr(event,"lux1=120.0|lux2=130.0")&&
        strstr(event,"contrast-tenths=110"));
 assert(pair_mean[0]==pair_sample_mean&&
        pair_difference[0]==pair_sample_difference);
 uint32_t restored_mean;int32_t restored_difference;
 assert(calibration_store_dual_light_get(1,&restored_mean,&restored_difference));
 assert(restored_mean==pair_sample_mean&&
        restored_difference==pair_sample_difference);
 uint32_t restored_mean_tolerance,restored_difference_tolerance;
 assert(calibration_store_dual_light_tolerance_get(
        1,&restored_mean_tolerance,&restored_difference_tolerance));
 assert(restored_mean_tolerance==pair_mean_tolerance[0]&&
        restored_difference_tolerance==pair_difference_tolerance[0]);

 /* Unsafe overlap with profile 1 is rejected and does not persist profile 2. */
 assert(calibration_runtime_blue_short(36000u)); /* profile 2 */
 pair_sample_mean=pair_mean[0];pair_sample_difference=pair_difference[0];
 assert(calibration_runtime_yellow_short(36001u));
 calibration_runtime_service(41001u);
 assert(calibration_runtime_take_event(event,sizeof(event))&&
        strstr(event,"DUAL-FIT"));
 assert(!(pair_saved_mask&(1u<<1))&&pair_mean[1]==4000u);

 /* A failed flash transaction restores the in-memory center. */
 pair_sample_mean=4100u;pair_sample_difference=210;fail_save=true;
 assert(calibration_runtime_yellow_short(42000u));
 calibration_runtime_service(47000u);
 assert(calibration_runtime_take_event(event,sizeof(event))&&
        strstr(event,"ERR|CAL|SAVE"));
 assert(pair_mean[1]==4000u&&pair_difference[1]==200);
 fail_save=false;
 /* A freshly exported partial descriptor can start with no calibrated rows;
  * one physical button sample must be enough to activate its first profile. */
 pair_calibrated_mask=0u;pair_saved_mask=0u;pair_tolerance_saved_mask=0u;
 pair_sample_mean=2050u;pair_sample_difference=110;
 calibration_runtime_init(NULL);
 assert(guard_runtime_dual_calibration_mask()==0u);
 assert(calibration_runtime_blue_long(48000u));
 assert(calibration_runtime_yellow_short(48001u));
 calibration_runtime_service(53001u);
 assert(calibration_runtime_take_event(event,sizeof(event))&&
        strstr(event,"mode=saved|kind=light|dual=1"));
 assert(pair_calibrated_mask==1u&&pair_saved_mask==1u);
 /* Studio's per-profile sync persists tolerance overrides alongside centers;
  * a reboot restores both values into the active classifier. */
 pair_saved_mean_tolerance[0]=80u;
 pair_saved_difference_tolerance[0]=40u;
 pair_tolerance_saved_mask|=1u;
 calibration_runtime_init(NULL);
 assert(pair_mean_tolerance[0]==80u&&pair_difference_tolerance[0]==40u);
 puts("physical calibration: single ranges, dual paired centers, separation and rollback passed");
 return 0;
}
