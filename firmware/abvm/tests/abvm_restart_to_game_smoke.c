#include "abvm_vm.h"
#include "cycle_runtime.h"
#include "guard_runtime.h"

#include <stdio.h>
#include <stdlib.h>

static bool marker_armed;
static uint8_t marker_count;

uint8_t calibration_store_shift_target(void){return 0u;}
bool calibration_store_shift_begin(uint8_t target,uint8_t maximum){(void)target;(void)maximum;return false;}
bool calibration_store_shift_complete(void){return true;}
bool calibration_store_cycle_armed(void){return marker_armed;}
uint8_t calibration_store_cycle_count(void){return marker_count;}
bool calibration_store_cycle_arm_next(uint8_t maximum){
    if(marker_count>=maximum){marker_armed=false;return false;}
    marker_armed=true;++marker_count;return true;
}
bool calibration_store_cycle_clear_armed(void){marker_armed=false;return true;}
bool calibration_store_cycle_reset(void){marker_armed=false;marker_count=0u;return true;}
bool light_sensor_latest(uint32_t *lux_tenths,uint32_t *age_ms,uint32_t now){
    (void)lux_tenths;(void)age_ms;(void)now;return false;
}

static int require(int condition,const char *message){
    if(!condition)fprintf(stderr,"Restart-to-Game smoke failure: %s\n",message);
    return condition;
}
static void drain_cycle(void){CycleEvent event;while(cycle_runtime_take_event(&event)){} }
static int stable(AbvmVm *vm,uint32_t lux,uint32_t at,uint16_t route,uint8_t stage){
    guard_runtime_observe(vm,lux,at);
    guard_runtime_observe(vm,lux,at+100u);
    GuardRuntimeEvent event;
    return require(guard_runtime_take_event(&event),"Guard event")&&
           require(event.type==GUARD_EVENT_ROUTE,"Guard route event")&&
           require(event.route_id==route,"ordered route")&&
           require(event.stage==stage,"ordered stage")&&
           require(vm->route_id==route,"VM route");
}

int main(int argc,char **argv){
    if(argc!=2)return 2;
    FILE *file=fopen(argv[1],"rb");if(!file)return 2;
    fseek(file,0,SEEK_END);long length=ftell(file);rewind(file);
    uint8_t *image=malloc((size_t)length);
    if(!image||fread(image,1,(size_t)length,file)!=(size_t)length)return 2;
    fclose(file);
    AbvmVm vm;
    if(!require(abvm_init(&vm,image,(size_t)length),"image")||
       !require(cycle_runtime_init(&vm,0u),"Cycle descriptor")||
       !require(guard_runtime_init(&vm),"Guard descriptor"))return 1;

    cycle_runtime_manual_start(0u);drain_cycle();
    if(!require(cycle_runtime_service(1000u,true,ARM_HOST_USB_UP,true)==
                CYCLE_ACTION_EXPIRE,"Cycle deadline")||
       !require(cycle_runtime_begin_after(1000u),"begin Restart route")||
       !require(marker_armed&&marker_count==1u,"persistent restart marker")||
       !require(abvm_start_route(&vm,cycle_runtime_after_route(),1000u),
                "start route 2"))return 1;
    drain_cycle();
    (void)cycle_runtime_service(1100u,true,ARM_HOST_USB_DOWN,true);drain_cycle();
    if(!require(!cycle_runtime_route_complete(2u,1200u),"complete route 2")||
       !require(cycle_runtime_service(1300u,true,ARM_HOST_USB_UP,false)==
                CYCLE_ACTION_NONE,"USB stability window")||
       !require(cycle_runtime_service(3300u,true,ARM_HOST_USB_UP,false)==
                CYCLE_ACTION_NONE,"USB alone cannot start Startup")||
       !require(cycle_runtime_service(3400u,true,ARM_HOST_USB_UP,true)==
                CYCLE_ACTION_NONE,"Desktop stability window")||
       !require(cycle_runtime_service(4400u,true,ARM_HOST_USB_UP,true)==
                CYCLE_ACTION_START_STARTUP,"stable USB and Desktop start Startup")||
       !require(abvm_start_route(&vm,cycle_runtime_startup_route(),4400u),
                "start route 3"))return 1;
    cycle_runtime_begin_startup();drain_cycle();
    if(!require(cycle_runtime_route_complete(3u,4500u),"complete Startup")||
       !require(!marker_armed&&marker_count==1u,"clear one-shot marker")||
       !require(guard_runtime_start_after_restart(4500u),"start post-restart Guard")||
       !require(guard_runtime_stage()==1u&&guard_runtime_expected_profile()==2u,
                "Desktop skipped; Login expected"))return 1;

    if(!stable(&vm,2000u,4600u,4u,2u)||
       !stable(&vm,3000u,4800u,6u,3u)||
       !stable(&vm,4000u,5000u,7u,4u)||
       !stable(&vm,5000u,5200u,8u,5u)||
       !require(guard_runtime_expected_profile()==0u,"Game reached")||
       !require(cycle_runtime_service(5499u,true,ARM_HOST_USB_UP,true)==
                CYCLE_ACTION_NONE,"resumed deadline early")||
       !require(cycle_runtime_service(5500u,true,ARM_HOST_USB_UP,true)==
                CYCLE_ACTION_EXPIRE,"new Cycle armed after recovery"))return 1;

    free(image);
    puts("ABVM Restart-to-Game integrated smoke passed");
    return 0;
}
