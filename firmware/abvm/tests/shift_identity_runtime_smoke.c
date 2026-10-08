#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "shift_identity_runtime.h"
static void put32(uint8_t *p,uint32_t n) {
    for(uint8_t i=0u;i<4u;++i)p[i]=(uint8_t)(n>>(8u*i));
}
int main(void) {
    uint8_t p[88]={0};memcpy(p,"SFT1",4u);p[4]=1u;p[5]=2u;
    put32(p+8u,60000u);put32(p+12u,90u);put32(p+16u,160u);
    p[20]=91u;p[21]=52u;memset(p+24u,0x11,32u);memset(p+56u,0x22,32u);
    ShiftIdentityRuntime s;assert(shift_identity_load(&s,p,88u,42u));
    shift_identity_begin(&s,0u);uint32_t first=s.nonce;
    assert(!shift_identity_reply(&s,first+1u,p+24u,true));
    assert(!shift_identity_reply(&s,first,p+24u,false));
    assert(s.selected==SHIFT_GLOBAL);
    assert(shift_identity_reply(&s,first,p+24u,true));
    assert(shift_identity_tick(&s,1000u,true)==SHIFT_WAIT_CLOSE);
    assert(shift_identity_tick(&s,1001u,false)==SHIFT_WAIT_CLOSE);
    assert(shift_identity_tick(&s,1500u,false)==SHIFT_WAIT_CLOSE);
    assert(shift_identity_tick(&s,1501u,false)==SHIFT_OK);
    assert(s.selected==SHIFT_DAY);
    shift_identity_begin(&s,2000u);assert(s.nonce!=first);
    assert(!shift_identity_reply(&s,first,p+24u,true));
    assert(shift_identity_reply(&s,s.nonce,p+56u,true));
    shift_identity_tick(&s,2001u,false);shift_identity_tick(&s,2300u,true);
    shift_identity_tick(&s,2301u,false);
    assert(shift_identity_tick(&s,2800u,false)==SHIFT_WAIT_CLOSE);
    assert(shift_identity_tick(&s,2801u,false)==SHIFT_OK&&s.selected==SHIFT_NIGHT);
    shift_identity_forget(&s);assert(s.selected==SHIFT_GLOBAL);
    uint8_t unknown[32]={0x33};
    shift_identity_begin(&s,3000u);
    assert(!shift_identity_reply(&s,s.nonce,unknown,true)&&s.phase==SHIFT_FAILED);
    shift_identity_begin(&s,UINT32_MAX-100u);
    assert(shift_identity_tick(&s,UINT32_MAX-50u,false)==SHIFT_WAIT_REPLY);
    assert(shift_identity_tick(&s,59900u,false)==SHIFT_FAILED);
    shift_identity_begin(&s,0u);assert(shift_identity_reply(&s,s.nonce,p+24u,true));
    assert(shift_identity_tick(&s,60000u,true)==SHIFT_FAILED);
    memcpy(p+56u,p+24u,32u);assert(!shift_identity_load(&s,p,88u,42u));
    puts("shift identity: stale replies, unknown user, port close, retry and rollover passed");
    return 0;
}