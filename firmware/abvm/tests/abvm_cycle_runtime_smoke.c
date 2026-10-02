#include "abvm_vm.h"
#include "cycle_runtime.h"
#include <stdio.h>
#include <stdlib.h>

static bool marker_armed;
static uint8_t marker_count;
static bool marker_reset_ok=true;

bool calibration_store_cycle_armed(void){return marker_armed;}
uint8_t calibration_store_cycle_count(void){return marker_count;}
bool calibration_store_cycle_arm_next(uint8_t maximum){
    if(marker_count>=maximum){marker_armed=false;return false;}
    marker_armed=true;++marker_count;return true;
}
bool calibration_store_cycle_clear_armed(void){marker_armed=false;return true;}
bool calibration_store_cycle_reset(void){
    if(!marker_reset_ok)return false;
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
    if(!require(cycle_runtime_service(999u,true,ARM_HOST_USB_UP,true)==CYCLE_ACTION_NONE,
                "deadline early")||
       !require(cycle_runtime_service(1000u,true,ARM_HOST_USB_UP,true)==CYCLE_ACTION_EXPIRE,
                "deadline")||
       !require(cycle_runtime_begin_after(1000u),"arm after")||
       !require(marker_armed&&marker_count==1u,"persistent marker"))return 1;
    drain();
    (void)cycle_runtime_service(1100u,true,ARM_HOST_USB_DOWN,true);drain();
    if(!require(!cycle_runtime_route_complete(cycle_runtime_after_route(),1200u),
                "after complete")||
       !require(cycle_runtime_service(1300u,true,ARM_HOST_USB_UP,false)==CYCLE_ACTION_NONE,
                "USB stable early")||
       !require(cycle_runtime_service(3300u,true,ARM_HOST_USB_UP,false)==
                    CYCLE_ACTION_NONE,"USB alone cannot start Startup")||
       !require(cycle_runtime_service(3400u,true,ARM_HOST_USB_UP,true)==
                    CYCLE_ACTION_NONE,"Desktop stable early")||
       !require(cycle_runtime_service(4400u,true,ARM_HOST_USB_UP,true)==
                    CYCLE_ACTION_START_STARTUP,"USB and Desktop stable"))return 1;
    cycle_runtime_begin_startup();
    CycleEvent startup_event;
    if(!require(cycle_runtime_take_event(&startup_event)&&
                startup_event.type==CYCLE_EVENT_STARTUP_START&&
                startup_event.startup_gate==1u,
                "Desktop-light startup gate"))return 1;
    if(!require(cycle_runtime_route_complete(cycle_runtime_startup_route(),4500u),
                "startup complete")||
       !require(!marker_armed&&marker_count==1u,"one-shot clear")||
       !require(cycle_runtime_service(5499u,true,ARM_HOST_USB_UP,true)==CYCLE_ACTION_NONE,
                "resumed deadline early")||
       !require(cycle_runtime_service(5500u,true,ARM_HOST_USB_UP,true)==CYCLE_ACTION_EXPIRE,
                "resumed deadline"))return 1;
    cycle_runtime_manual_stop();
    if(!require(!marker_armed&&!marker_count,"manual reset"))return 1;
    cycle_runtime_manual_start(5000u);drain();
    cycle_runtime_hold(5500u);
    if(!require(cycle_runtime_held(),"operator hold")||
       !require(cycle_runtime_service(7000u,true,ARM_HOST_USB_UP,true)==
                    CYCLE_ACTION_NONE,"held Cycle cannot expire"))return 1;
    cycle_runtime_continue(7000u);
    if(!require(!cycle_runtime_held(),"operator continue")||
       !require(cycle_runtime_service(7499u,true,ARM_HOST_USB_UP,true)==
                    CYCLE_ACTION_NONE,"remaining deadline preserved")||
       !require(cycle_runtime_service(7500u,true,ARM_HOST_USB_UP,true)==
                    CYCLE_ACTION_EXPIRE,"deadline resumes after hold"))return 1;
    cycle_runtime_manual_stop();

    marker_armed=true;marker_count=1u;
    if(!require(cycle_runtime_init(&vm,0u),"boot descriptor"))return 1;
    drain();
    if(!require(cycle_runtime_service(0u,true,ARM_HOST_USB_UP,true)==CYCLE_ACTION_NONE,
                "boot stable early")||
       !require(cycle_runtime_service(2000u,true,ARM_HOST_USB_UP,true)==
                    CYCLE_ACTION_START_STARTUP,"armed boot authority"))return 1;

    /* A complete AutoCycle session owns five persistent restart transitions.
     * Clearing the one-shot armed byte after Startup must preserve the count,
     * so rounds three through five cannot silently collapse to IDLE. */
    cycle_runtime_manual_stop();
    cycle_runtime_manual_start(10000u);drain();
    uint32_t now=10000u;
    for(uint8_t round=1u;round<=5u;++round){
        if(!require(cycle_runtime_service(now+1000u,true,ARM_HOST_USB_UP,true)==
                        CYCLE_ACTION_EXPIRE,"five-cycle deadline")||
           !require(cycle_runtime_begin_after(now+1000u),
                        "five-cycle arm Restart")||
           !require(marker_armed&&marker_count==round,
                        "five-cycle persistent count")||
           !require(!cycle_runtime_route_complete(
                        cycle_runtime_after_route(),now+1010u),
                        "five-cycle Restart complete")||
           !require(cycle_runtime_service(now+1100u,true,ARM_HOST_USB_UP,true)==
                        CYCLE_ACTION_NONE,"five-cycle USB early")||
           !require(cycle_runtime_service(now+3100u,true,ARM_HOST_USB_UP,true)==
                        CYCLE_ACTION_START_STARTUP,
                        "five-cycle Desktop starts Startup"))return 1;
        cycle_runtime_begin_startup();drain();
        if(!require(cycle_runtime_route_complete(
                        cycle_runtime_startup_route(),now+3200u),
                        "five-cycle Startup complete")||
           !require(!marker_armed&&marker_count==round,
                        "five-cycle count survives Startup"))return 1;
        drain();now+=3200u;
    }
    if(!require(cycle_runtime_service(now+1000u,true,ARM_HOST_USB_UP,true)==
                    CYCLE_ACTION_START_FINISH,"limit starts Finish"))return 1;
    cycle_runtime_begin_finish();
    if(!require(cycle_runtime_take_event(&startup_event)&&
                    startup_event.type==CYCLE_EVENT_FINISH_START&&
                    startup_event.route_id==cycle_runtime_finish_route()&&
                    startup_event.count==5u,
                    "Finish start telemetry")||
       !require(!cycle_runtime_route_complete(
                    cycle_runtime_finish_route(),now+1010u),
                    "Finish completes without another run")||
       !require(cycle_runtime_take_event(&startup_event)&&
                    startup_event.type==CYCLE_EVENT_FINISH_COMPLETE&&
                    startup_event.count==5u,
                    "Finish completion telemetry")||
       !require(!marker_armed&&marker_count==0u,
                    "Finish clears the terminal counter")||
       !require(cycle_runtime_service(now+2000u,true,ARM_HOST_USB_UP,true)==
                    CYCLE_ACTION_NONE,"cycle is idle after Finish"))return 1;

    /* Starting a new operator session must never inherit a stale terminal
     * count.  If Flash reset fails, refuse to arm instead of running Finish
     * after the first deadline. */
    marker_count=5u;marker_armed=false;marker_reset_ok=false;
    if(!require(!cycle_runtime_manual_start(now+3000u),
                    "stale count reset failure blocks start")||
       !require(marker_count==5u,
                    "failed reset does not masquerade as a clean session")||
       !require(cycle_runtime_service(now+5000u,true,ARM_HOST_USB_UP,true)==
                    CYCLE_ACTION_NONE,
                    "blocked start cannot select Finish"))return 1;
    marker_reset_ok=true;

    /* Desktop light remains the preferred readiness signal, but a failed or
     * slightly drifting calibration must not strand a later round forever. */
    cycle_runtime_manual_stop();
    cycle_runtime_manual_start(50000u);drain();
    if(!require(cycle_runtime_service(51000u,true,ARM_HOST_USB_UP,false)==
                    CYCLE_ACTION_EXPIRE,"fallback deadline")||
       !require(cycle_runtime_begin_after(51000u),"fallback arm Restart")||
       !require(!cycle_runtime_route_complete(
                    cycle_runtime_after_route(),51010u),
                    "fallback Restart complete")||
       !require(cycle_runtime_service(51100u,true,ARM_HOST_USB_UP,false)==
                    CYCLE_ACTION_NONE,"fallback timer starts")||
       !require(cycle_runtime_service(83099u,true,ARM_HOST_USB_UP,false)==
                    CYCLE_ACTION_NONE,"fallback early")||
       !require(cycle_runtime_service(83100u,true,ARM_HOST_USB_UP,false)==
                    CYCLE_ACTION_START_STARTUP,"bounded fallback starts Startup"))
        return 1;
    cycle_runtime_begin_startup();
    if(!require(cycle_runtime_take_event(&startup_event)&&
                startup_event.type==CYCLE_EVENT_STARTUP_START&&
                startup_event.startup_gate==2u,
                "USB timeout fallback gate"))return 1;
    free(image);
    puts("ABVM native persistent cycle state machine smoke passed");
    return 0;
}