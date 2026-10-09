/* The Python runner inserts the actual main.c adapter into this test.
 * The actual VM and actual Pico keyboard actor are linked; only board I/O and
 * unrelated actors are stubbed. */
#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include "abvm_vm.h"
#include "hid_keyboard.h"
#include "game_buff_runtime.h"
#include "tusb.h"

#define GAME_ROUTE_ID 8u
#define BUFF_KEY_LANE 253u
static AbvmVm vm;
static GameBuffRuntime game_buffs;
static bool buff_checkpoint, buff_key_inflight;
static uint8_t buff_lane;
static uint32_t game_fishing_elapsed, game_age_at, buff_status_at, buff_generation;
static unsigned buff_presses, cast_presses, catch_presses;
static bool overlay, paused, released = true;
static uint32_t elapsed_seen;
bool tud_mounted(void) { return true; }
bool tud_hid_ready(void) { return true; }
bool tud_hid_keyboard_report(uint8_t report_id,uint8_t mods,const uint8_t keys[6]) {
    (void)report_id;(void)mods;
    bool press=keys[0]!=0u;
    if (press) {
        assert(!paused);
        assert(released);released=false;
        /* HID number 8=37, number 7=36, F=9 */
        if(keys[0]==37u) { assert(!overlay);++buff_presses; }
        else if(keys[0]==36u) { assert(!game_buff_pending(&game_buffs,vm.now));++cast_presses; }
        else if(keys[0]==9u)++catch_presses;
        else assert(false);
    } else released=true;
    return true;
}
static bool cycle_runtime_restart_critical(void) { return false; }
static bool input_lock_active(void) { return hid_keyboard_locked(); }
static void arm_uart_mouse_set_game_elapsed(uint32_t elapsed) { elapsed_seen=elapsed; }
static void release_all_actors(uint32_t now) {
    (void)now;
    if(buff_key_inflight) {
        game_buff_key_cancelled(&game_buffs);buff_key_inflight=false;
        hid_keyboard_release_all();hid_keyboard_discard_completion();
    } else {
        if(buff_checkpoint&&vm.route_id!=GAME_ROUTE_ID&&game_buffs.phase==GAME_BUFF_AFTER)
            game_buffs.phase=GAME_BUFF_REQUEST;
        hid_keyboard_release_all();
    }
}
/* PRODUCTION_ADAPTER */

int main(int argc,char **argv) {
    assert(argc==2);
    FILE *f=fopen(argv[1],"rb");assert(f);
    fseek(f,0,SEEK_END);long size=ftell(f);rewind(f);
    uint8_t *image=malloc((size_t)size);assert(image);
    assert(fread(image,1,(size_t)size,f)==(size_t)size);fclose(f);
    assert(abvm_init(&vm,image,(size_t)size));
    hid_keyboard_init();assert(abvm_start_route(&vm,8u,0u));
    bool interruption=false, pause_test=false, resumed=false;
    unsigned during_pause=0u;uint32_t pause_at=0u;
    unsigned catches=0u,timeouts=0u;
    for(uint32_t now=0u;now<9000u;++now) {
        vm.now=now;
        if(!interruption&&game_buffs.phase==GAME_BUFF_AFTER) {
            assert(abvm_interrupt_route(&vm,10u,now));
            overlay=true;interruption=true;
        }
        if(interruption&&!overlay&&!pause_test&&game_buffs.consumed_mask) {
            assert(abvm_pause(&vm,now));paused=true;pause_test=true;
            during_pause=buff_presses;pause_at=now;
        }
        if(paused&&now-pause_at>=2000u) {
            assert(buff_presses==during_pause);
            assert(game_buff_remaining(&game_buffs,0u,now)==0u);
            assert(abvm_resume(&vm,now));paused=false;resumed=true;
        }
        uint8_t lane;
        if(hid_keyboard_service(now,&lane)) {
            if(lane==BUFF_KEY_LANE) {
                assert(buff_key_inflight);buff_key_inflight=false;
                assert(game_buff_key_finished(&game_buffs,now));
            } else assert(abvm_complete_action(&vm,lane,now));
        }
        service_game_buffs(now);
        if(buff_checkpoint&&vm.route_id==8u&&!vm.pending_release)continue;
        AbvmEvent e=abvm_tick(&vm,now);
        assert(e.type!=ABVM_EVENT_FAULT);
        if(e.type==ABVM_EVENT_RELEASE_ALL)release_all_actors(now);
        else if(e.type==ABVM_EVENT_INTERRUPT_RESUME)overlay=false;
        else if(e.type==ABVM_EVENT_ACTION) {
            if(e.opcode==ABVM_OP_BUFF) {
                if(e.flags) {
                    const uint8_t *payload;uint32_t length;
                    assert(abvm_constant(&vm,e.operand_a,ABVM_CONST_BUFF,&payload,&length));
                    assert(game_buff_load(&game_buffs,payload,length,99u));
                    game_buff_new_game(&game_buffs);game_fishing_elapsed=0u;
                }
                buff_lane=e.lane;buff_checkpoint=true;
            } else assert(hid_keyboard_submit(&vm,&e,now)==HID_KEYBOARD_ACCEPTED);
        } else if(e.type==ABVM_EVENT_WATCH_ARMED) {
            /* Alternate caught attempts with real VM timeout behavior. */
            if((catches+timeouts)%2u==0u) {
                assert(abvm_sound_detected(&vm,2u,now));++catches;
            } else ++timeouts;
        }
    }
    fprintf(stderr,"interrupt=%u pause=%u resume=%u overlay=%u vm=%u route=%u phase=%u consumed=%u presses=%u casts=%u\n",interruption,pause_test,resumed,overlay,vm.status,vm.route_id,game_buffs.phase,game_buffs.consumed_mask,buff_presses,cast_presses);
    assert(interruption&&pause_test&&resumed);
    assert(buff_presses>2u&&cast_presses>2u&&catch_presses>0u&&timeouts>0u);
    assert(elapsed_seen>1000u);
    uint32_t generation=vm.route_generation;
    assert(abvm_start_route(&vm,8u,10000u));
    assert(vm.route_generation==generation+1u);
    service_game_buffs(10000u);
    assert(!buff_checkpoint&&!game_buffs.session);

    free(image);
    puts("production Pico buff adapter / VM / keyboard / pause / interrupt smoke passed");
    return 0;
}