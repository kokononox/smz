// ARM 2.8.4: hostless relative HID with USB-paced dense HANDPATH replay.
// Relative MMOVE never resets the OS cursor, so movement starts from its real
// current position without a Windows bridge or a centre reset.
// Arduino only auto-generates prototypes for the primary sketch, not for a legacy
// .ino included as a compatibility unit. Declare the forward references used by
// arm26 before its definitions are included.
#include <Arduino.h>
static bool read_line_blocking(uint16_t timeoutMs);
static bool decrypt_to(const char* line, char* out, uint8_t outMax);
static void do_halt();

#define setup arm26_setup
#define loop arm26_loop
#include "ams_board26_impl.h"
#undef setup
#undef loop
#include "human_mouse_v3.h"

static bool arm27_human_command(const char* line){
  return !strcmp(line,"HVER")||!strncmp(line,"HCFG|",5)||!strncmp(line,"HPAUSE|",7)||!strncmp(line,"HMOVE|",6)||!strncmp(line,"HRANDOM|",8)||!strncmp(line,"HSETCUR|",8);
}
static bool arm27_handle(char* line){
  if(!strcmp(line,"HVER")){send_line("OK|HVER|2.8.4|HMOUSE=1|REL=1|MR=1");return true;}
  if(!arm27_human_command(line))return false;
  return human_mouse_v3_handle(line);
}

void setup(){ arm26_setup(); }
void loop(){
  poll_host_usb();
  if(serial1_line_ready()){
    g_out=&Serial1;
    if(!arm27_handle(g_line1)) handle(g_line1);
    g_out=0;
  }
  if(!g_secure){if(Serial)do_handshake(40);else delay(1);return;}
  if(Serial.available()&&read_line_blocking(50)){
    static char cmd[MAX_PT];
    if(decrypt_to(g_line,cmd,MAX_PT)){g_lastFrameMs=millis();handle(cmd);}
    else if(hello_from_line(g_line)){}
    else{digitalWrite(LED_ERR,LOW);delay(30);digitalWrite(LED_ERR,HIGH);}
  }
  if(SESSION_IDLE_MS&&(millis()-g_lastFrameMs>SESSION_IDLE_MS))session_reset();
}
