#include "shift_identity_runtime.h"
#include <string.h>
static uint32_t le32(const uint8_t *p) {
    return (uint32_t)p[0]|((uint32_t)p[1]<<8)|((uint32_t)p[2]<<16)|((uint32_t)p[3]<<24);
}
static uint32_t random_next(ShiftIdentityRuntime *s) {
    uint32_t x=s->rng;x^=x<<13;x^=x>>17;x^=x<<5;s->rng=x;return x;
}
static uint16_t le16(const uint8_t *p) { return (uint16_t)(p[0]|((uint16_t)p[1]<<8)); }
static bool inside(uint16_t minute,uint16_t start,uint16_t end) {
    return start<end ? minute>=start&&minute<end : minute>=start||minute<end;
}
bool shift_schedule_valid(uint16_t ds,uint16_t de,uint16_t ns,uint16_t ne) {
    if(ds>=1440u||de>=1440u||ns>=1440u||ne>=1440u||ds==de||ns==ne)return false;
    for(uint16_t m=0u;m<1440u;++m)if(inside(m,ds,de)&&inside(m,ns,ne))return false;
    return true;
}
ShiftKind shift_kind_at_minute(uint16_t minute,uint16_t day_start,uint16_t day_end,
                               uint16_t night_start,uint16_t night_end) {
    /* A disabled schedule loads as four zeros, and "00:00-00:00" is the wrap
     * case of `inside()`, which would then claim every minute belongs to the
     * day shift.  An unconfigured schedule owns no window at all. */
    if(minute>=1440u||day_start>=1440u||day_end>=1440u||night_start>=1440u||
       night_end>=1440u||day_start==day_end||night_start==night_end)return SHIFT_GLOBAL;
    if(inside(minute,day_start,day_end))return SHIFT_DAY;
    if(inside(minute,night_start,night_end))return SHIFT_NIGHT;
    return SHIFT_GLOBAL; /* an uncovered minute is intentionally ignored */
}
ShiftKind shift_identity_expected(const ShiftIdentityRuntime *s) {
    if(!s||!s->schedule_enabled||!s->clock_received)return SHIFT_GLOBAL;
    return shift_kind_at_minute(s->minute,s->day_start,s->day_end,
                                s->night_start,s->night_end);
}
bool shift_identity_load(ShiftIdentityRuntime *s,const uint8_t *p,uint32_t size,uint32_t seed) {
    if(!s||!p||!((size==88u&&!memcmp(p,"SFT1",4u)&&p[4]==1u)||
                  (size==104u&&!memcmp(p,"SFT2",4u)&&p[4]==2u))||
       !p[5]||p[5]>4u||p[6]||p[7])return false;
    ShiftIdentityRuntime loaded={0};
    loaded.key_count=p[5];loaded.timeout_ms=le32(p+8u);
    loaded.hold_min=le32(p+12u);loaded.hold_max=le32(p+16u);
    if(loaded.timeout_ms<1000u||loaded.timeout_ms>120000u||
       !loaded.hold_min||loaded.hold_min>loaded.hold_max||loaded.hold_max>10000u)return false;
    for(uint8_t i=0u;i<4u;++i) {
        if(i<loaded.key_count) {
            if(!p[20u+i])return false;
            for(uint8_t j=0u;j<i;++j)if(p[20u+i]==p[20u+j])return false;
        } else if(p[20u+i])return false;
        loaded.keys[i]=p[20u+i];
    }
    memcpy(loaded.day_hash,p+24u,32u);memcpy(loaded.night_hash,p+56u,32u);
    uint8_t zero[32]={0};
    if(!memcmp(loaded.day_hash,loaded.night_hash,32u)||
       !memcmp(loaded.day_hash,zero,32u)||!memcmp(loaded.night_hash,zero,32u))return false;
    if(size==104u) {
        if(p[88]!=1u||!p[89]||p[89]>10u||le16(p+102u))return false;
        loaded.schedule_enabled=true;loaded.max_attempts=p[89];
        loaded.day_start=le16(p+90u);loaded.day_end=le16(p+92u);
        loaded.night_start=le16(p+94u);loaded.night_end=le16(p+96u);
        loaded.day_route=le16(p+98u);loaded.night_route=le16(p+100u);
        if(!shift_schedule_valid(loaded.day_start,loaded.day_end,loaded.night_start,loaded.night_end)||
           loaded.day_route!=16u||loaded.night_route!=17u)return false;
    }
    loaded.rng=seed?seed:0x743da159u;
    *s=loaded;return true;
}
void shift_identity_begin(ShiftIdentityRuntime *s,uint32_t now) {
    s->selected=SHIFT_GLOBAL;s->candidate=SHIFT_GLOBAL;s->clock_received=false;
    s->nonce=random_next(s);s->deadline=now+s->timeout_ms;
    s->phase=SHIFT_WAIT_REPLY;s->closing=false;s->reason=NULL;
}
bool shift_identity_reply(ShiftIdentityRuntime *s,uint32_t nonce,
                          const uint8_t hash[32],bool connected) {
    if(!s||!hash||(s->schedule_enabled&&!s->clock_received)||s->phase!=SHIFT_WAIT_REPLY||nonce!=s->nonce||!connected)return false;
    if(!memcmp(hash,s->day_hash,32u))s->candidate=SHIFT_DAY;
    else if(!memcmp(hash,s->night_hash,32u))s->candidate=SHIFT_NIGHT;
    else { s->phase=SHIFT_FAILED;s->reason="unknown-user";return false; }
    s->phase=SHIFT_WAIT_CLOSE;s->closing=false;return true;
}
bool shift_identity_reply_clock(ShiftIdentityRuntime *s,uint32_t nonce,
                                const uint8_t hash[32],uint16_t minute,bool connected) {
    if(!s||minute>=1440u||s->phase!=SHIFT_WAIT_REPLY||nonce!=s->nonce||!connected)return false;
    s->clock_received=true;s->minute=minute;
    return shift_identity_reply(s,nonce,hash,connected);
}
ShiftPhase shift_identity_tick(ShiftIdentityRuntime *s,uint32_t now,bool connected) {
    if(!s)return SHIFT_FAILED;
    if(s->phase==SHIFT_WAIT_REPLY||s->phase==SHIFT_WAIT_CLOSE) {
        if((int32_t)(now-s->deadline)>=0) {
            s->phase=SHIFT_FAILED;s->reason="timeout-or-bridge-not-closed";
            s->selected=SHIFT_GLOBAL;return s->phase;
        }
        if(s->phase==SHIFT_WAIT_CLOSE) {
            if(connected)s->closing=false;
            else if(!s->closing){s->closing=true;s->closed_at=now;}
            else if(now-s->closed_at>=500u) {
                s->selected=s->candidate;s->phase=SHIFT_OK;
            }
        }
    }
    return s->phase;
}
void shift_identity_cancel(ShiftIdentityRuntime *s) {
    s->phase=SHIFT_FAILED;s->selected=SHIFT_GLOBAL;s->reason="cancelled";
}
void shift_identity_forget(ShiftIdentityRuntime *s) {
    s->selected=SHIFT_GLOBAL;s->candidate=SHIFT_GLOBAL;s->phase=SHIFT_IDLE;
}