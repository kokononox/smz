#include "shift_identity_runtime.h"
#include <string.h>
static uint32_t le32(const uint8_t *p) {
    return (uint32_t)p[0]|((uint32_t)p[1]<<8)|((uint32_t)p[2]<<16)|((uint32_t)p[3]<<24);
}
static uint32_t random_next(ShiftIdentityRuntime *s) {
    uint32_t x=s->rng;x^=x<<13;x^=x>>17;x^=x<<5;s->rng=x;return x;
}
bool shift_identity_load(ShiftIdentityRuntime *s,const uint8_t *p,uint32_t size,uint32_t seed) {
    if(!s||!p||size!=88u||memcmp(p,"SFT1",4u)||p[4]!=1u||
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
    loaded.rng=seed?seed:0x743da159u;
    *s=loaded;return true;
}
void shift_identity_begin(ShiftIdentityRuntime *s,uint32_t now) {
    s->selected=SHIFT_GLOBAL;s->candidate=SHIFT_GLOBAL;
    s->nonce=random_next(s);s->deadline=now+s->timeout_ms;
    s->phase=SHIFT_WAIT_REPLY;s->closing=false;s->reason=NULL;
}
bool shift_identity_reply(ShiftIdentityRuntime *s,uint32_t nonce,
                          const uint8_t hash[32],bool connected) {
    if(!s||!hash||s->phase!=SHIFT_WAIT_REPLY||nonce!=s->nonce||!connected)return false;
    if(!memcmp(hash,s->day_hash,32u))s->candidate=SHIFT_DAY;
    else if(!memcmp(hash,s->night_hash,32u))s->candidate=SHIFT_NIGHT;
    else { s->phase=SHIFT_FAILED;s->reason="unknown-user";return false; }
    s->phase=SHIFT_WAIT_CLOSE;s->closing=false;return true;
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