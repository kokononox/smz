// ARM 2.8: ARM 2.7 plus genuine hostless relative HID movement.
// Relative MMOVE never resets the OS cursor, so movement starts from its real
// current position without a Windows bridge or a centre reset.
// Arduino only auto-generates prototypes for the primary sketch, not for a legacy
// .ino included as a compatibility unit. Declare the forward references used by
// arm26 before its definitions are included.
#include <Arduino.h>
#include <avr/interrupt.h>
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
// ARM 2.8.2-S4 keeps the hardware-proven S3 mouse cadence byte-for-byte.
// Sound uses the ATmega32U4 free-running ADC in the background: the ISR builds
// the same 10 ms peak windows used by blocking WSND/SCAL, so a 145-unit splash
// remains a 145-unit splash while MMOVE micro-steps are being emitted.
static volatile uint8_t asndState=0; // 0=off, 1=listening, 2=detected/block moves
static volatile uint16_t asndThreshold=0,asndMinimum=0;
static uint32_t asndDeadline=0;
static volatile uint16_t asndWindowPeak=0,asndObservedPeak=0;
static volatile uint16_t asndWindowSamples=0,asndSustainedMs=0;
static volatile uint8_t asndDetected=0;
#define ASND_WINDOW_SAMPLES 96U // 16 MHz / 128 / 13 ~= 9615 ADC samples/s = ~10 ms

ISR(ADC_vect){
  if(asndState!=1 || asndDetected)return;
  uint16_t sample=ADC;
  uint16_t d=(sample>=512U)?(sample-512U):(512U-sample);
  if(d>asndWindowPeak)asndWindowPeak=d;
  if(d>asndObservedPeak)asndObservedPeak=d;
  if(++asndWindowSamples>=ASND_WINDOW_SAMPLES){
    if(asndWindowPeak>=asndThreshold){
      uint16_t next=(uint16_t)(asndSustainedMs+10U);
      asndSustainedMs=next;
      if(next>=asndMinimum)asndDetected=1;
    }else asndSustainedMs=0;
    asndWindowPeak=0;asndWindowSamples=0;
  }
}

static void asnd_adc_stop(){
  uint8_t saved=SREG;cli();
  ADCSRA&=(uint8_t)~(_BV(ADIE)|_BV(ADATE));
  SREG=saved;
}
static void asnd_adc_start(){
  // Let Arduino select A0/ADC7 once, then leave conversions to hardware.
  (void)analogRead(SND_PIN);
  uint8_t saved=SREG;cli();
  asndWindowPeak=0;asndObservedPeak=0;asndWindowSamples=0;
  asndSustainedMs=0;asndDetected=0;
  ADCSRB&=(uint8_t)~(_BV(ADTS2)|_BV(ADTS1)|_BV(ADTS0)); // free-running trigger
  ADCSRA|=_BV(ADEN)|_BV(ADATE)|_BV(ADIE)|_BV(ADIF)|_BV(ADSC);
  SREG=saved;
}
static uint16_t asnd_observed(){
  uint8_t saved=SREG;cli();uint16_t value=asndObservedPeak;SREG=saved;return value;
}
static void asnd_event(const __FlashStringHelper* value,uint16_t peak){
  Serial1.print(F("EVT|ASND|"));Serial1.print(value);
  Serial1.print(F("|peak="));Serial1.println(peak);Serial1.flush();
}
static void arm28_sound_cancel(){asnd_adc_stop();asndState=0;asndDetected=0;}
static bool arm28_sound_blocks_move(){return asndState==2;}
static bool arm28_sound_tick(){
  if(asndState!=1)return asndState==2;
  uint32_t now=millis();
  if((int32_t)(now-asndDeadline)>=0){
    uint16_t peak=asnd_observed();arm28_sound_cancel();
    asnd_event(F("TIMEOUT"),peak);return false;
  }
  if(asndDetected){
    uint16_t peak=asnd_observed();asnd_adc_stop();asndState=2;
    asnd_event(F("DETECTED"),peak);return true;
  }
  return false;
}
static bool arm27_handle(char* line){
  if(!strcmp(line,"HVER")){send_line("OK|HVER|2.8.2-S4|REL=1|ASND=1");return true;}
  if(!strncmp(line,"ASND|",5)){
    int thr=60;unsigned long minimum=60,timeout=30000;
    sscanf(line+5,"%d,%lu,%lu",&thr,&minimum,&timeout);
    arm28_sound_cancel();
    asndThreshold=(uint16_t)max(1,thr);
    asndMinimum=(uint16_t)max(1UL,minimum);
    asndDeadline=millis()+max(1UL,timeout);
    asndState=1;asnd_adc_start();
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
