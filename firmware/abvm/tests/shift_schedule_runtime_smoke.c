#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "shift_identity_runtime.h"
static void put16(uint8_t *p,uint16_t n){p[0]=(uint8_t)n;p[1]=(uint8_t)(n>>8);}
static void put32(uint8_t *p,uint32_t n){for(int i=0;i<4;++i)p[i]=(uint8_t)(n>>(i*8));}
int main(void){
 uint8_t p[104]={0};memcpy(p,"SFT2",4);p[4]=2;p[5]=2;
 put32(p+8,60000);put32(p+12,90);put32(p+16,160);p[20]=91;p[21]=52;
 memset(p+24,0x11,32);memset(p+56,0x22,32);p[88]=1;p[89]=2;
 put16(p+90,480);put16(p+92,1080);put16(p+94,1200);put16(p+96,360);
 put16(p+98,16);put16(p+100,17);
 ShiftIdentityRuntime s;assert(shift_identity_load(&s,p,sizeof(p),42));
 assert(!shift_schedule_valid(480,1080,1000,360));
 assert(!shift_schedule_valid(0,0,1200,360));assert(!shift_schedule_valid(1440,1080,1200,360));
 for(uint16_t m=0;m<1440;++m){
  shift_identity_begin(&s,0);assert(!shift_identity_reply(&s,s.nonce,p+24,true));
  assert(!shift_identity_reply_clock(&s,s.nonce+1,p+24,m,true));
  assert(shift_identity_reply_clock(&s,s.nonce,p+24,m,true));
  assert(shift_identity_tick(&s,1,false)==SHIFT_WAIT_CLOSE);
  assert(shift_identity_tick(&s,501,false)==SHIFT_OK);
  ShiftKind expected=m>=480&&m<1080?SHIFT_DAY:m>=1200||m<360?SHIFT_NIGHT:SHIFT_GLOBAL;
  assert(shift_identity_expected(&s)==expected);
  assert(s.selected==SHIFT_DAY); /* time NEVER changes the actual username's role */
 }
 shift_identity_begin(&s,0);assert(!shift_identity_reply_clock(&s,s.nonce,p+24,1440,true));
 p[89]=0;assert(!shift_identity_load(&s,p,sizeof(p),42));p[89]=2;
 put16(p+100,16);assert(!shift_identity_load(&s,p,sizeof(p),42));
 puts("scheduled identity: all 1440 minutes, gaps, midnight, limits, stale/no-clock replies passed");return 0;
}
