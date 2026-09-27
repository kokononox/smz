// ARM 2.8: ARM 2.7 plus genuine hostless relative HID movement.
// Relative MMOVE never resets the OS cursor, so movement starts from its real
// current position without a Windows bridge or a centre reset.
// Arduino only auto-generates prototypes for the primary sketch, not for a legacy
// .ino included as a compatibility unit. Declare the forward references used by
// arm26 before its definitions are included.
#include <Arduino.h>
static bool read_line_blocking(uint16_t timeoutMs);
static bool decrypt_to(const char* line, char* out, uint8_t outMax);
static void do_halt();
static bool arm28_sound_tick();
static bool arm28_sound_blocks_move();
static void arm28_sound_cancel();

#define ARM_SOUND_TICK() arm28_sound_tick()
#define ARM_SOUND_BLOCKS_MOVE() arm28_sound_blocks_move()
#define ARM_SOUND_CANCEL() arm28_sound_cancel()
#define setup arm26_setup
#define loop arm26_loop
#include "ams_board26_impl.h"
#undef setup
#undef loop
// ARM 2.8.2-S2 intentionally drops the unused legacy absolute HumanMouse layer.
// Portable mode uses native relative MMOVE; the recovered flash is used for a
// concurrent sound watcher.  S1 restores the hardware-proven 2.8.1 mouse
// cadence: ADC sampling runs between MMOVE commands, never between HID
// micro-steps.  With Pico's ~8 ms stream this still detects a sustained sound
// promptly without injecting variable work into the motion loop.
static uint8_t asndState=0; // 0=off, 1=listening, 2=detected/block moves
static uint16_t asndThreshold=0,asndMinimum=0;
static uint32_t asndDeadline=0,asndHighSince=0;

static void asnd_event(const __FlashStringHelper* value){
  Serial1.print(F("EVT|ASND|")); Serial1.println(value); Serial1.flush();
}
static void arm28_sound_cancel(){asndState=0;asndHighSince=0;}
static bool arm28_sound_blocks_move(){return asndState==2;}
static bool arm28_sound_tick(){
  if(asndState!=1)return asndState==2;
  uint32_t now=millis();
  if((int32_t)(now-asndDeadline)>=0){
    asndState=0;asnd_event(F("TIMEOUT"));return false;
  }
  int d=analogRead(SND_PIN)-512;if(d<0)d=-d;
  if((uint16_t)d>=asndThreshold){
    if(!asndHighSince)asndHighSince=now;
    if(now-asndHighSince>=asndMinimum){
      asndState=2;asnd_event(F("DETECTED"));return true;
    }
  }else asndHighSince=0;
  return false;
}
static bool arm27_handle(char* line){
  if(!strcmp(line,"HVER")){send_line("OK|HVER|2.8.2-S2|REL=1|ASND=1");return true;}
  if(!strncmp(line,"ASND|",5)){
    int thr=60;unsigned long minimum=60,timeout=30000;
    sscanf(line+5,"%d,%lu,%lu",&thr,&minimum,&timeout);
    asndThreshold=(uint16_t)max(1,thr);
    asndMinimum=(uint16_t)max(1UL,minimum);
    asndDeadline=millis()+max(1UL,timeout);
    asndHighSince=0;asndState=1;
    send_line("OK|ASND");return true;
  }
  if(!strcmp(line,"ASNDCANCEL")){
    arm28_sound_cancel();send_line("OK|ASNDCANCEL");return true;
  }
  return false;
}

void setup(){ arm26_setup(); }
void loop(){
  arm28_sound_tick();
  poll_host_usb();
  if(serial1_line_ready()){
    g_out=&Serial1;
    if(!arm27_handle(g_line1)) handle(g_line1);
    g_out=0;
  }
  // An open CDC/DTR port is not proof that a HELLO frame is waiting. Polling
  // do_handshake(40) merely because COM is open injects a 40-52 ms stall
  // between Serial1 mouse commands. Only real queued bytes may start it.
  if(!g_secure){if(Serial.available())do_handshake(40);else delay(1);return;}
  if(Serial.available()&&read_line_blocking(50)){
    static char cmd[MAX_PT];
    if(decrypt_to(g_line,cmd,MAX_PT)){g_lastFrameMs=millis();handle(cmd);}
    else if(hello_from_line(g_line)){}
    else{digitalWrite(LED_ERR,LOW);delay(30);digitalWrite(LED_ERR,HIGH);}
  }
  if(SESSION_IDLE_MS&&(millis()-g_lastFrameMs>SESSION_IDLE_MS))session_reset();
}
