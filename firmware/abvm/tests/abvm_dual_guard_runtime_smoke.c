#include "abvm_vm.h"
#include "guard_runtime.h"
#include "light_sensor.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static bool pair_ready;
static LightSensorSnapshot pair_snapshot;
static int require(int condition,const char *message) {
    if(!condition)fprintf(stderr,"ABVM dual Guard failure: %s\n",message);
    return condition;
}
bool light_sensor_latest(uint32_t *lux_tenths,uint32_t *age_ms,uint32_t now) {
    (void)now;
    if(!pair_ready||!lux_tenths||!age_ms)return false;
    *lux_tenths=pair_snapshot.primary_lux_tenths;
    *age_ms=pair_snapshot.primary_age_ms;
    return pair_snapshot.primary_valid;
}
bool light_sensor_latest_pair(LightSensorSnapshot *snapshot,uint32_t now) {
    (void)now;
    if(!snapshot||!pair_ready)return false;
    *snapshot=pair_snapshot;
    return true;
}

int main(int argc,char **argv) {
    if(argc!=2)return 2;
    FILE *file=fopen(argv[1],"rb");
    if(!file)return 2;
    fseek(file,0,SEEK_END);
    long length=ftell(file);
    rewind(file);
    uint8_t *image=malloc((size_t)length);
    if(!image||fread(image,1,(size_t)length,file)!=(size_t)length)return 2;
    fclose(file);

    AbvmVm vm;
    pair_ready=true;
    pair_snapshot=(LightSensorSnapshot){
        .primary_valid=true,.secondary_present=true,.secondary_valid=true,
        .primary_lux_tenths=950u,.primary_age_ms=0u,
        .secondary_lux_tenths=1050u,.secondary_age_ms=0u
    };
    if(!require(abvm_init(&vm,image,(size_t)length),"ABP image")||
       !require(guard_runtime_init(&vm),"v6 descriptor")||
       !require(guard_runtime_available(),"runtime available")||
       !require(guard_runtime_dual_sensor_enabled(),"dual mode")||
       !require(guard_runtime_dual_sensor_ready(0u),"fresh pair")||
       !require(!guard_runtime_profile_matches(1u,1000u),
                "no legacy L1 fallback")||
       !require(guard_runtime_profile_matches_pair(1u,950u,1050u),
                "mean and signed difference match")||
       !require(!guard_runtime_profile_matches_pair(1u,1000u,1000u),
                "signed difference distinguishes same mean")||
       !require(!guard_runtime_get_profile_range(1u,
                 &pair_snapshot.primary_lux_tenths,
                 &pair_snapshot.secondary_lux_tenths),
                "single-sensor calibration disabled")||
       !require(!guard_runtime_set_profile_range(1u,900u,1100u),
                "single-sensor range writes disabled"))return 1;

    if(!require(guard_runtime_start(0u),"start with fresh pair"))return 1;
    guard_runtime_observe(&vm,1000u,0u);
    if(!require(guard_runtime_active_profile()==0u,
                "single-sensor observe cannot classify dual profile"))return 1;
    guard_runtime_observe_pair(&vm,950u,1050u,0u);
    GuardRuntimeEvent event;
    if(!require(guard_runtime_take_event(&event)&&
                event.type==GUARD_EVENT_STATE,"paired candidate"))return 1;
    guard_runtime_observe_pair(&vm,950u,1050u,100u);
    if(!require(guard_runtime_take_event(&event)&&
                event.type==GUARD_EVENT_ROUTE&&event.route_id==1u&&
                guard_runtime_active_profile()==1u,
                "stable paired profile routes"))return 1;

    /* M stays within the hysteresis ellipse while D drifts outside the
     * calibrated ellipse; the current state is retained but no new route fires. */
    guard_runtime_observe_pair(&vm,983u,1087u,150u);
    if(!require(guard_runtime_active_profile()==1u&&
                !guard_runtime_take_event(&event),
                "two-axis hysteresis"))return 1;

    guard_runtime_observe_pair(&vm,1000u,1000u,200u);
    if(!require(guard_runtime_active_profile()==0u,
                "ambiguous or unmatched pair fails closed"))return 1;

    guard_runtime_stop();
    pair_snapshot.primary_age_ms=0u;
    pair_snapshot.secondary_age_ms=251u;
    if(!require(!guard_runtime_start(1000u),
                "skewed pair cannot start Guard"))return 1;
    pair_snapshot.secondary_age_ms=0u;
    if(!require(guard_runtime_start(1000u),"fresh pair restarts Guard"))return 1;
    pair_ready=false;
    guard_runtime_service(&vm,2601u);
    if(!require(!guard_runtime_running()&&
                guard_runtime_take_event(&event)&&
                event.type==GUARD_EVENT_FAULT&&
                event.reason&&strcmp(event.reason,"sensor-pair-timeout")==0,
                "missing pair stops Guard"))return 1;

    free(image);
    puts("dual Guard: v6 parsing, paired classification, hysteresis, stale fail-closed passed");
    return 0;
}