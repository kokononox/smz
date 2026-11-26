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
    uint32_t mean_center=0u,mean_tolerance=0u,difference_tolerance=0u;
    int32_t difference_center=0;
    if(!require(abvm_init(&vm,image,(size_t)length),"ABP image")||
       !require(guard_runtime_init(&vm),"v6 descriptor")||
       !require(guard_runtime_available(),"runtime available")||
       !require(guard_runtime_dual_sensor_enabled(),"dual mode")||
       !require(guard_runtime_dual_sensor_ready(0u),"fresh pair")||
       !require(guard_runtime_get_profile_pair_calibration(1u,
                 &mean_center,&mean_tolerance,
                 &difference_center,&difference_tolerance),
                "paired calibration metadata")||
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
    if(!require(mean_center>0u&&mean_tolerance>0u&&
                difference_tolerance>0u,"paired tolerance metadata"))return 1;
    uint32_t means[9],mean_tolerances[9],difference_tolerances[9];
    int32_t differences[9];
    for(uint8_t id=1u;id<=9u;++id)
        if(!require(guard_runtime_get_profile_pair_calibration(id,
                &means[id-1u],&mean_tolerances[id-1u],
                &differences[id-1u],&difference_tolerances[id-1u]),
                "tolerance update snapshot"))return 1;
    uint32_t original_mean_tolerance=mean_tolerances[0];
    uint32_t original_difference_tolerance=difference_tolerances[0];
    mean_tolerances[0]=80u;
    difference_tolerances[0]=40u;
    if(!require(guard_runtime_validate_profile_pair_calibrations_mask(
                guard_runtime_dual_calibration_mask(),means,mean_tolerances,
                differences,difference_tolerances),
                "wider tolerance update keeps clearance")||
       !require(guard_runtime_set_profile_pair_calibrations_mask(1u,means,
                mean_tolerances,differences,difference_tolerances),
                "per-profile tolerance update applies")||
       !require(guard_runtime_get_profile_pair_calibration(1u,&mean_center,
                &mean_tolerance,&difference_center,&difference_tolerance)&&
                mean_tolerance==80u&&difference_tolerance==40u,
                "updated M and D tolerances are active")||
       !require(guard_runtime_profile_matches_pair(1u,910u,1010u),
                "new M tolerance is used by the classifier"))
        return 1;
    mean_tolerances[0]=original_mean_tolerance;
    difference_tolerances[0]=original_difference_tolerance;
    if(!require(guard_runtime_set_profile_pair_calibrations_mask(1u,means,
                mean_tolerances,differences,difference_tolerances),
                "original per-profile tolerance restores"))
        return 1;
    uint32_t other_mean,other_mean_tolerance,other_difference_tolerance;
    int32_t other_difference;
    if(!require(guard_runtime_get_profile_pair_calibration(2u,&other_mean,
                 &other_mean_tolerance,&other_difference,
                 &other_difference_tolerance),"second profile metadata")||
       !require(!guard_runtime_set_profile_pair_centers(1u,other_mean,
                 other_difference),"overlapping paired centers rejected")||
       !require(guard_runtime_set_profile_pair_centers(1u,mean_center,
                 difference_center),"original paired center remains valid"))
        return 1;
    uint32_t batch_means[9],saved_means[9];
    int32_t batch_differences[9],saved_differences[9];
    for(uint8_t id=1u;id<=9u;++id){
        uint32_t mean_tolerance,difference_tolerance;
        if(!require(guard_runtime_get_profile_pair_calibration(id,
                &batch_means[id-1u],&mean_tolerance,
                &batch_differences[id-1u],&difference_tolerance),
                "batch snapshot"))return 1;
    }
    memcpy(saved_means,batch_means,sizeof(saved_means));
    memcpy(saved_differences,batch_differences,sizeof(saved_differences));
    for(uint8_t i=0u;i<9u;++i)batch_means[i]+=100u;
    if(!require(guard_runtime_validate_profile_pair_centers(batch_means,
                 batch_differences),"valid paired batch")||
       !require(guard_runtime_set_profile_pair_centers_batch(batch_means,
                 batch_differences),"paired batch applies atomically"))
        return 1;
    batch_means[1]=batch_means[0];
    batch_differences[1]=batch_differences[0];
    if(!require(!guard_runtime_set_profile_pair_centers_batch(batch_means,
                 batch_differences),"overlapping paired batch rejected")||
       !require(guard_runtime_get_profile_pair_calibration(1u,&mean_center,
                 &mean_tolerance,&difference_center,&difference_tolerance)&&
                 mean_center==saved_means[0]+100u,
                "rejected batch leaves active profiles unchanged"))
        return 1;
    if(!require(guard_runtime_set_profile_pair_centers_batch(saved_means,
                 saved_differences),"paired batch restores original centers"))
        return 1;

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