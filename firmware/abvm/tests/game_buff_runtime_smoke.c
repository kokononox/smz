#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "game_buff_runtime.h"

static GameBuffGate safe = {true,false,false,true,true};
static GameBuffConfig config(uint8_t key, uint32_t interval) {
    GameBuffConfig c = {0};
    c.enabled = true; c.key_count = 1u; c.keys[0] = key;
    c.interval_min_ms = c.interval_max_ms = interval;
    c.hold_min_ms = c.hold_max_ms = 100u;
    c.before_min_ms = c.before_max_ms = 50u;
    c.after_min_ms = c.after_max_ms = 11000u;
    return c;
}
static GameBuffEvent request(GameBuffRuntime *r, uint32_t now) {
    assert(game_buff_service(r, now, safe).kind == GAME_BUFF_EVENT_NONE);
    GameBuffEvent e = game_buff_service(r, now+50u, safe);
    assert(e.kind == GAME_BUFF_EVENT_KEY_REQUEST);
    assert(e.hold_ms == 100u);
    assert(game_buff_key_accepted(r));
    assert(game_buff_key_finished(r, now+150u));
    return e;
}
static void timing(void) {
    GameBuffRuntime r;
    GameBuffConfig c = config('9',55u*60000u);
    assert(game_buff_init(&r,&c,1u,123u)); game_buff_new_game(&r);
    request(&r,1000u);
    assert(!game_buff_key_finished(&r,1300u));
    GameBuffGate paused = safe; paused.paused = true;
    /* A delayed poll during Pause still anchors cooldown to end of food wait. */
    GameBuffEvent e = game_buff_service(&r,20000u,paused);
    assert(e.kind == GAME_BUFF_EVENT_CONSUMED);
    assert(r.last_consumed[0] == 12150u);
    assert(e.next_due == 12150u+55u*60000u);
    assert(game_buff_remaining(&r,0u,20000u)==e.next_due-20000u);
    assert(game_buff_service(&r,e.next_due+1000u,paused).kind==GAME_BUFF_EVENT_NONE);
    assert(game_buff_pending(&r,e.next_due+1000u));
    GameBuffGate catching = safe; catching.safe_boundary = false;
    assert(game_buff_service(&r,e.next_due+1000u,catching).kind==GAME_BUFF_EVENT_NONE);
    /* Catch completion OR Timeout may open the same safe gate. */
    request(&r,e.next_due+1000u);
    game_buff_service(&r,e.next_due+13000u,safe);
    assert(r.last_consumed[0]==e.next_due+12150u);
    game_buff_new_game(&r); assert(r.consumed_mask==0u);
    request(&r,e.next_due+14000u);
    game_buff_end_game(&r); assert(!game_buff_pending(&r,0u));
}
static void priority_and_retry(void) {
    GameBuffRuntime r;
    GameBuffConfig c[2]={config('8',1000u),config('9',1000u)};
    assert(game_buff_init(&r,c,2u,42u)); game_buff_new_game(&r);
    GameBuffGate blocked=safe; blocked.optical_priority=true;
    assert(game_buff_service(&r,0u,blocked).kind==GAME_BUFF_EVENT_NONE);
    request(&r,100u);
    GameBuffEvent consumed=game_buff_service(&r,12000u,blocked);
    assert(consumed.kind==GAME_BUFF_EVENT_CONSUMED);
    uint8_t first=consumed.index;
    assert(game_buff_service(&r,12001u,blocked).kind==GAME_BUFF_EVENT_NONE);
    GameBuffEvent second=request(&r,12100u);
    assert(second.index!=first); /* unfinished batch resumes, no repeated food */
    assert(game_buff_service(&r,12201u,safe).kind==GAME_BUFF_EVENT_NONE);
    /* Separate failed/incomplete key never starts a new cooldown. */
    game_buff_new_game(&r);
    game_buff_service(&r,0u,safe);
    GameBuffEvent e=game_buff_service(&r,50u,safe);
    assert(game_buff_key_accepted(&r));
    game_buff_key_cancelled(&r);
    assert(r.consumed_mask==0u);
    assert(game_buff_service(&r,100u,blocked).kind==GAME_BUFF_EVENT_NONE);
    GameBuffEvent retry=game_buff_service(&r,200u,safe);
    assert(retry.kind==GAME_BUFF_EVENT_KEY_REQUEST&&retry.index==e.index);
}
static void rollover(void) {
    GameBuffRuntime r; GameBuffConfig c=config('1',60000u);
    assert(game_buff_init(&r,&c,1u,1u));game_buff_new_game(&r);
    uint32_t t=UINT32_MAX-100u; request(&r,t);
    GameBuffEvent e=game_buff_service(&r,t+11150u,safe);
    assert(e.kind==GAME_BUFF_EVENT_CONSUMED);
    assert(game_buff_remaining(&r,0u,t+11150u)==60000u);
    assert(!game_buff_pending(&r,t+71149u));
    assert(game_buff_pending(&r,t+71150u));
}
static void random_and_validation(void) {
    GameBuffConfig c[6];
    for(uint8_t i=0u;i<6u;++i) {
        c[i]=config((uint8_t)('1'+i),55u*60000u);
        c[i].interval_min_ms=54u*60000u;c[i].interval_max_ms=56u*60000u;
        c[i].before_min_ms=c[i].before_max_ms=0u;
        c[i].after_min_ms=c[i].after_max_ms=0u;
    }
    GameBuffRuntime r;assert(game_buff_init(&r,c,6u,99u));
    uint8_t previous[6]={0};unsigned different=0u;
    for(unsigned run=0u;run<1000u;++run) {
        game_buff_new_game(&r);uint16_t seen=0u;uint8_t order[6];
        for(uint8_t i=0u;i<6u;++i) {
            uint32_t now=run*1000u+i*101u;
            GameBuffEvent e=game_buff_service(&r,now,safe);
            assert(e.kind==GAME_BUFF_EVENT_KEY_REQUEST);
            assert(!(seen&(1u<<e.index)));seen|=(uint16_t)(1u<<e.index);
            order[i]=e.index;assert(game_buff_key_accepted(&r));
            assert(game_buff_key_finished(&r,now+100u));
            e=game_buff_service(&r,now+100u,safe);
            assert(e.kind==GAME_BUFF_EVENT_CONSUMED);
            uint32_t gap=e.next_due-r.last_consumed[e.index];
            assert(gap>=54u*60000u&&gap<=56u*60000u);
        }
        assert(seen==63u);
        if(run&&memcmp(previous,order,6u))++different;
        memcpy(previous,order,6u);
    }
    assert(different>900u);
    c[0].interval_max_ms=UINT32_MAX;
    assert(!game_buff_init(&r,c,6u,1u));
    c[0]=config('1',1000u);c[0].key_count=2u;c[0].keys[1]='1';
    assert(!game_buff_init(&r,c,6u,1u));
    assert(game_buff_init(&r,NULL,0u,0u));
    game_buff_new_game(&r);assert(!game_buff_pending(&r,0u));
    printf("shuffle: %u/999 transitions changed\n",different);
}
int main(void) {
    timing();priority_and_retry();rollover();random_and_validation();
    puts("Pico game buff scheduler contracts passed");
    return 0;
}