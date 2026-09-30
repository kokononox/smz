#include "abvm_vm.h"
#include "cycle_runtime.h"
#include <stdio.h>
#include <stdlib.h>

static bool marker_armed;
static uint8_t marker_count;

bool calibration_store_cycle_armed(void){return marker_armed;}
uint8_t calibration_store_cycle_count(void){return marker_count;}
bool calibration_store_cycle_arm_next(uint8_t maximum){
    if(marker_count>=maximum){marker_armed=false;return false;}
    marker_armed=true;++marker_count;return true;
}
bool calibration_store_cycle_clear_armed(void){marker_armed=false;return true;}
bool calibration_store_cycle_reset(void){
    marker_armed=false;marker_count=0u;return true;
}

static int require(int condition,const char *message){
    if(!condition)fprintf(stderr,"ABVM Cycle smoke failure: %s\n",message);
    return condition;
}
static void drain(void){CycleEvent event;while(cycle_runtime_take_event(&event)){}}

int main(int argc,char **argv){
    if(argc!=2)return 2;
    FILE *file=fopen(argv[1],"rb");if(!file)return 2;
    fseek(file,0,SEEK_END);long length=ftell(file);rewind(file);
    uint8_t *image=malloc((size_t)length);
    if(!image||fread(image,1,(size_t)length,file)!=(size_t)length)return 2;
    fclose(file);
    AbvmVm vm;
    if(!require(abvm_init(&vm,image,(size_t)length),"image")||
       !require(cycle_runtime_init(&vm,0u),"descriptor")||
       !require(cycle_runtime_available(),"available"))return 1;
    cycle_runtime_manual_start(0u);drain();
    if(!require(cycle_runtime_service(999u,true,ARM_HOST_USB_UP)==CYCLE_ACTION_NONE,
                "deadline early")||
       !require(cycle_runtime_service(1000u,true,ARM_HOST_USB_UP)==CYCLE_ACTION_EXPIRE,
                "deadline")||
       !require(cycle_runtime_begin_after(1000u),"arm after")||
       !require(marker_armed&&marker_count==1u,"persistent marker"))return 1;
    drain();
    (void)cycle_runtime_service(1100u,true,ARM_HOST_USB_DOWN);drain();
    if(!require(!cycle_runtime_route_complete(cycle_runtime_after_route(),1200u),
                "after complete")||
       !require(cycle_runtime_service(1300u,true,ARM_HOST_USB_UP)==CYCLE_ACTION_NONE,
                "USB stable early")||
       !require(cycle_runtime_service(3300u,true,ARM_HOST_USB_UP)==
                    CYCLE_ACTION_START_STARTUP,"USB stable"))return 1;
    cycle_runtime_begin_startup();drain();
    if(!require(cycle_runtime_route_complete(cycle_runtime_startup_route(),3400u),
                "startup complete")||
       !require(!marker_armed&&marker_count==1u,"one-shot clear")||
       !require(cycle_runtime_service(4399u,true,ARM_HOST_USB_UP)==CYCLE_ACTION_NONE,
                "resumed deadline early")||
       !require(cycle_runtime_service(4400u,true,ARM_HOST_USB_UP)==CYCLE_ACTION_EXPIRE,
                "resumed deadline"))return 1;
    cycle_runtime_manual_stop();
    if(!require(!marker_armed&&!marker_count,"manual reset"))return 1;

    marker_armed=true;marker_count=1u;
    if(!require(cycle_runtime_init(&vm,0u),"boot descriptor"))return 1;
    drain();
    if(!require(cycle_runtime_service(0u,true,ARM_HOST_USB_UP)==CYCLE_ACTION_NONE,
                "boot stable early")||
       !require(cycle_runtime_service(2000u,true,ARM_HOST_USB_UP)==
                    CYCLE_ACTION_START_STARTUP,"armed boot authority"))return 1;
    free(image);
    puts("ABVM native persistent cycle state machine smoke passed");
    return 0;
}