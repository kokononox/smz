#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "abvm_vm.h"
#include "hid_keyboard.h"
#include "shift_identity_runtime.h"
#include "tusb.h"
#define SHIFT_KEY_LANE 252u
typedef struct BuzzerTone {uint16_t hz,duration,gap;} BuzzerTone;
static AbvmVm vm;
static ShiftIdentityRuntime shift_identity;
static bool shift_checkpoint,shift_key_inflight,shift_launch_pending,shift_cue_started,shift_failure_paused;
static uint8_t shift_lane;
static uint32_t shift_generation,shift_cue_until;
static bool connected,guard_on=true,alarm;
static unsigned switch_calls,reset_calls,typed,day_cues,night_cues;
static uint8_t attempt_count,pending_target;
static uint16_t switched_route;
bool tud_mounted(void){return true;}
bool tud_hid_ready(void){return true;}
static bool tud_cdc_connected(void){return connected;}
bool tud_hid_keyboard_report(uint8_t id,uint8_t mods,const uint8_t keys[6]){
 (void)id;if(keys[0]&&!(mods&KEYBOARD_MODIFIER_LEFTGUI))++typed;return true;
}
static bool arm_uart_mouse_busy(void){return false;}
static bool guard_runtime_running(void){return guard_on;}
static bool guard_runtime_pause(void){return true;}
static void guard_runtime_stop(void){guard_on=false;}
static void cycle_runtime_hold(uint32_t now){(void)now;}
static void cycle_runtime_fail(uint8_t stage){(void)stage;assert(false);}
static uint8_t calibration_store_shift_attempts(void){return attempt_count;}
static uint8_t calibration_store_shift_target(void){return pending_target;}
static bool cycle_runtime_begin_shift(uint16_t route,uint8_t target,uint8_t maximum,uint32_t now){
 (void)now;assert(!connected);assert(attempt_count<maximum);++attempt_count;pending_target=target;switched_route=route;++switch_calls;return true;
}
static bool cycle_runtime_shift_confirmed(uint32_t now){
 (void)now;assert(!connected);++reset_calls;attempt_count=0;pending_target=0;return true;
}
static void arm_uart_mouse_discard_completion(void){}
static void release_all_actors(uint32_t now){(void)now;hid_keyboard_release_all();}
static void buzzer_watchdog_alarm_start(uint32_t now){(void)now;alarm=true;}
static void buzzer_watchdog_alarm_stop(void){alarm=false;}
static void buzzer_play_sequence(const BuzzerTone *cue,uint8_t count,uint8_t volume,uint8_t envelope,uint32_t now){
 (void)volume;(void)envelope;(void)now;assert(count==2u);if(cue[0].hz==880u)++day_cues;else ++night_cues;
}
/* PRODUCTION_ADAPTER */
int main(int argc,char **argv){
 assert(argc==3);int mode=atoi(argv[2]);FILE *f=fopen(argv[1],"rb");assert(f);fseek(f,0,SEEK_END);long size=ftell(f);rewind(f);
 uint8_t *image=malloc((size_t)size);assert(image);assert(fread(image,1,(size_t)size,f)==(size_t)size);fclose(f);
 assert(abvm_init(&vm,image,(size_t)size));hid_keyboard_init();assert(abvm_start_route(&vm,1u,0u));
 if(mode==3)attempt_count=2;
 if(mode==4){pending_target=1;attempt_count=2;}
 bool replied=false;uint32_t reply_at=0;
 for(uint32_t now=0;now<10000;++now){
  uint8_t lane;if(hid_keyboard_service(now,&lane)){
   if(lane==SHIFT_KEY_LANE)shift_key_inflight=false;else assert(abvm_complete_action(&vm,lane,now));
  }
  if(shift_checkpoint&&shift_identity.phase==SHIFT_WAIT_REPLY&&!shift_key_inflight&&!replied){
   connected=true;uint16_t minute=mode==1?1200u:mode==2?1140u:480u;
   const uint8_t *hash=(mode==1||mode==4)?shift_identity.day_hash:shift_identity.night_hash;
   uint8_t unknown[32]={0x55};if(mode==5)hash=unknown;
   bool accepted=shift_identity_reply_clock(&shift_identity,shift_identity.nonce,hash,minute,true);
   assert(accepted==(mode!=5));replied=true;reply_at=now;
  }
  if(replied&&now-reply_at>=600u)connected=false;
  service_shift_check(now);
  if(vm.status==ABVM_STATUS_PAUSED)break;
  if(shift_checkpoint&&!vm.pending_release)continue;
  AbvmEvent e=abvm_tick(&vm,now);assert(e.type!=ABVM_EVENT_FAULT);
  if(e.type==ABVM_EVENT_RELEASE_ALL){hid_keyboard_release_all();}
  else if(e.type==ABVM_EVENT_ACTION){
   if(e.opcode==ABVM_OP_SHIFT_CHECK){
    const uint8_t *p;uint32_t n;assert(abvm_constant(&vm,e.operand_a,ABVM_CONST_SHIFT,&p,&n));
    assert(shift_identity_load(&shift_identity,p,n,now+99));shift_lane=e.lane;shift_checkpoint=true;
    shift_generation=vm.route_generation;launch_shift_check(now);
   }else{if(e.opcode==ABVM_OP_SHIFT_TYPE)e.opcode=ABVM_OP_TYPE;assert(hid_keyboard_submit(&vm,&e,now)==HID_KEYBOARD_ACCEPTED);}
  }else if(e.type==ABVM_EVENT_ROUTE_COMPLETE)break;
 }
 assert(replied);
 if(mode==0||mode==1){assert(switch_calls==1&&switched_route==(mode==0?16:17)&&typed==0&&!reset_calls&&!day_cues&&!night_cues);}
 else if(mode==2){assert(!switch_calls&&!reset_calls&&typed==1&&night_cues==1&&!alarm);}
 else if(mode==3||mode==5){assert(vm.status==ABVM_STATUS_PAUSED&&alarm&&!switch_calls&&!typed&&!reset_calls);}
 else{assert(mode==4&&!switch_calls&&reset_calls==1&&typed==1&&day_cues==1&&!pending_target&&!attempt_count);}
 free(image);printf("actual schedule adapter mode %d passed\n",mode);return 0;
}
