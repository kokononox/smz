#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "abvm_vm.h"
#include "hid_keyboard.h"
#include "shift_identity_runtime.h"
#include "tusb.h"
#define SHIFT_KEY_LANE 252u
typedef struct BuzzerTone { uint16_t hz,duration,gap; } BuzzerTone;
static AbvmVm vm;
static ShiftIdentityRuntime shift_identity;
static bool shift_checkpoint,shift_key_inflight,shift_launch_pending,shift_cue_started,shift_failure_paused;
static uint8_t shift_lane;
static uint32_t shift_generation,shift_cue_until;
static bool connected,guard_paused,alarm;
static unsigned attempts,day_cues,night_cues;
static char typed[8];static unsigned typed_count;
bool tud_mounted(void){return true;}
bool tud_hid_ready(void){return true;}
static bool tud_cdc_connected(void){return connected;}
bool tud_hid_keyboard_report(uint8_t id,uint8_t modifiers,const uint8_t keys[6]) {
    (void)id;(void)modifiers;
    if(keys[0]==HID_KEY_4)++attempts;
    else if(keys[0]) {
        assert(!shift_checkpoint&&!connected&&!guard_paused);
        assert(typed_count<sizeof(typed));
        typed[typed_count++]=(char)('A'+keys[0]-HID_KEY_A);
    }
    return true;
}
static bool arm_uart_mouse_busy(void){return false;}
static bool guard_runtime_running(void){return true;}
static bool guard_runtime_pause(void){guard_paused=true;return true;}
static void cycle_runtime_hold(uint32_t now){(void)now;}
static void buzzer_watchdog_alarm_start(uint32_t now){(void)now;alarm=true;}
static void buzzer_watchdog_alarm_stop(void){alarm=false;}
static void buzzer_play_sequence(const BuzzerTone *cue,uint8_t count,uint8_t volume,uint8_t envelope,uint32_t now) {
    (void)volume;(void)envelope;(void)now;assert(count==2u);
    if(cue[0].hz==880u)++day_cues;else if(cue[1].hz==660u)++night_cues;else assert(false);
}
static uint8_t calibration_store_shift_attempts(void){return 0u;}
static uint8_t calibration_store_shift_target(void){return 0u;}
static bool cycle_runtime_begin_shift(uint16_t route,uint8_t target,uint8_t maximum,uint32_t now){(void)route;(void)target;(void)maximum;(void)now;return false;}
static bool cycle_runtime_shift_confirmed(uint32_t now){(void)now;return true;}
static void guard_runtime_stop(void){}
static void release_all_actors(uint32_t now){(void)now;}
static void arm_uart_mouse_discard_completion(void){}
static void cycle_runtime_fail(uint8_t stage){(void)stage;}
/* PRODUCTION_ADAPTER */
int main(int argc,char **argv) {
    assert(argc==2);FILE *f=fopen(argv[1],"rb");assert(f);
    fseek(f,0,SEEK_END);long size=ftell(f);rewind(f);
    uint8_t *image=malloc((size_t)size);assert(image);
    assert(fread(image,1,(size_t)size,f)==(size_t)size);fclose(f);
    assert(abvm_init(&vm,image,(size_t)size));hid_keyboard_init();
    assert(abvm_start_route(&vm,1u,0u));
    bool sent_unknown=false,resumed=false,reply_sent=false,startup=false,global=false;
    uint32_t old_nonce=0u,reply_at=0u;unsigned completed=0u;
    for(uint32_t now=0u;now<12000u;++now) {
        uint8_t lane;
        if(hid_keyboard_service(now,&lane)) {
            if(lane==SHIFT_KEY_LANE)shift_key_inflight=false;
            else assert(abvm_complete_action(&vm,lane,now));
        }
        if(shift_checkpoint&&shift_identity.phase==SHIFT_WAIT_REPLY&&!shift_key_inflight&&attempts) {
            connected=true;
            if(!sent_unknown) {
                uint8_t unknown[32]={0x33};old_nonce=shift_identity.nonce;
                assert(!shift_identity_reply(&shift_identity,old_nonce,unknown,true));
                sent_unknown=true;connected=false;
            } else if(resumed&&!reply_sent) {
                assert(!shift_identity_reply(&shift_identity,old_nonce,shift_identity.day_hash,true));
                const uint8_t *hash=startup?shift_identity.night_hash:shift_identity.day_hash;
                assert(shift_identity_reply(&shift_identity,shift_identity.nonce,hash,true));
                reply_sent=true;reply_at=now;
            }
        }
        if(reply_sent&&now-reply_at>700u)connected=false;
        service_shift_check(now);
        if(vm.status==ABVM_STATUS_PAUSED&&!resumed) {
            assert(alarm&&typed_count==0u&&shift_identity.selected==SHIFT_GLOBAL);
            assert(abvm_resume(&vm,now));guard_paused=false;resumed=true;
        }
        if(shift_checkpoint&&!vm.pending_release)continue;
        AbvmEvent e=abvm_tick(&vm,now);assert(e.type!=ABVM_EVENT_FAULT);
        if(e.type==ABVM_EVENT_RELEASE_ALL) {
            if(shift_checkpoint&&shift_identity.phase!=SHIFT_FAILED) {
                shift_identity_cancel(&shift_identity);shift_failure_paused=true;
            }
            hid_keyboard_release_all();
            if(shift_key_inflight){shift_key_inflight=false;hid_keyboard_discard_completion();}
        } else if(e.type==ABVM_EVENT_ACTION) {
            if(e.opcode==ABVM_OP_SHIFT_CHECK) {
                const uint8_t *p;uint32_t length;
                assert(abvm_constant(&vm,e.operand_a,ABVM_CONST_SHIFT,&p,&length));
                assert(shift_identity_load(&shift_identity,p,length,now+99u));
                shift_lane=e.lane;shift_checkpoint=true;shift_generation=vm.route_generation;
                launch_shift_check(now);
            } else {
                if(e.opcode==ABVM_OP_SHIFT_TYPE)e.opcode=ABVM_OP_TYPE;
                assert(hid_keyboard_submit(&vm,&e,now)==HID_KEYBOARD_ACCEPTED);
            }
        } else if(e.type==ABVM_EVENT_ROUTE_COMPLETE) {
            ++completed;
            if(!startup) {
                assert(typed_count==1u&&typed[0]=='D'&&day_cues==1u);
                startup=true;reply_sent=false;connected=false;
                assert(abvm_start_route(&vm,3u,now));
            } else if(!global) {
                assert(typed_count==2u&&typed[1]=='N'&&night_cues==1u);
                abvm_stop(&vm,now);service_shift_check(now);
                assert(shift_identity.selected==SHIFT_GLOBAL);
                global=true;assert(abvm_start_route(&vm,8u,now));
            } else break;
        }
    }
    assert(sent_unknown&&resumed&&completed==3u);
    assert(typed_count==3u&&!memcmp(typed,"DNG",3u));
    free(image);puts("actual Pico adapter: failure/Pause/retry, closure, day/night/Global typing passed");
    return 0;
}