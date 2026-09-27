/*
   ams_board22.ino — Arduino Macro Studio dedicated mouse/sound firmware
   Target: Arduino Pro Micro (ATmega32U4, 5V/16MHz)

   Layers (single firmware, no interference — peripherals are independent):
     1) USB HID: PortableMouse relative interface (real host cursor, no centre reset)
     2) USB CDC serial command channel, AES-128-CTR encrypted after HELLO handshake
     3) ADC sound detection on pin A0 (AUX tap / mic module), non-blocking + HALT-abortable
     4) Brain link on Serial1 (pins 0/1): plain-text commands from the Pico brain (fw 1.7)

   Protocol (line-based, '\n'-terminated):
     Plaintext before session:  HELLO|<16-byte nonce hex>
     Board reply:               OK|HELLO|AMS_BOARD|<ver>|<proof hex>
     After handshake, every line:  E|<counter>|<hex ciphertext>
     Decrypted plaintexts are:  COMMAND|ARGS  /  OK|...  /  ERR|CODE|MSG  /  EVT|...

   Commands: PING VER HELLO HALT SETRES MMOVE MCLICK MDOWN MUP MDRAG MWHEEL
             WSND SCAL TRGSND
*/
#include "portable_relative_mouse.h"
#include "ams_key.h"   // static const uint8_t AMS_PSK[16] = {...};

#ifndef ARM_SOUND_TICK
#define ARM_SOUND_TICK() false
#endif
#ifndef ARM_SOUND_BLOCKS_MOVE
#define ARM_SOUND_BLOCKS_MOVE() false
#endif
#ifndef ARM_SOUND_CANCEL
#define ARM_SOUND_CANCEL() ((void)0)
#endif

// fw 2.6 (11 Sep 2026): debounced HOSTUSB UP/SUSPEND/DOWN events on Serial1; no PC helper.
// fw 2.3 (09 Sep 2026): paced catch-up + optional integrity frames on the brain link.
//   14k-record field evidence (09 Sep 2026): after a lost OK|MMOVE the Pico coalesced
//   ~450 px and fw 2.2 played it as 8 x ~57 px inside <=20 ms (~18,000 px/s - the
//   "linked mouse" dart), and one byte-dropped MMOVE ('1532'->'32') caused a 1500 px
//   out-and-back. Now: (a) stream points > 60 px play as a ~2.5 px/ms sweep (up to
//   160 ms, <=60 steps of <=8 px) while <=60 px points keep the old <=20 ms behaviour;
//   (b) brain-link lines may be framed as '#' + 2 hex (byte-sum) + '|' + payload -
//   a mismatch is dropped with ERR|CKSUM, never executed. Legacy unframed lines and
//   the encrypted USB channel stay byte-identical to fw 2.2.
// fw 2.2 (09 Sep 2026): smooth 3-pixel stream + non-blocking idle USB session.
//   The USB encrypted channel is polled only when bytes are available; a stale HELLO
//   can no longer stall every Pico/Serial1 mouse point for 50 ms. Stream segments are
//   split into ~3 px micro-steps paced across <=20 ms to resemble measured hand motion.
// fw 2.1 (09 Sep 2026): dedicated role enforced: SingleAbsoluteMouse + sound only.
//   Removed Keyboard HID initialization, VK mapper, and KCOMBO/KTEXT/KDOWN/KUP.
//   Mouse uses its own HID interface/endpoint without a shared Report ID.
//   Pico remains the exclusive keyboard + light + plan-engine controller.
// fw 1.4 (19 Aug 2026): fixed absolute-mouse axis mapping. See toAbsX/toAbsY.
// fw 1.5 (19 Aug 2026): a lost PC->board frame no longer wedges the session;
//                       the replay counter only has to increase. See decrypt_to.
// fw 1.6 (19 Aug 2026): long SCAL windows lost their reply. The ADC loop now
//                       runs in 10 ms slices like listen_loop, and send_line
//                       flushes and retries a dropped CDC write.
// fw 1.7 (07 Sep 2026): brain link on Serial1 (pins 0=RX, 1=TX, 115200 8N1) - plain-text
//                       command channel for the Pico brain, no HELLO/AES on this wire; it
//                       never leaves the box. Replies/events return on the same wire. The
//                       encrypted USB-CDC path is unchanged.
// fw 1.8 (08 Sep 2026): click/wheel no longer drag the cursor to screen centre.
//   HID-Project resends its STORED axes on every button/wheel report and those axes
//   boot to (0,0) = centre, while our pixel tracker booted at (0,0) = top-left: any
//   click/scroll without a prior board-driven move jumped to the middle of the screen.
//   Now the tracker boots at centre and every button/wheel report carries the TRACKED
//   position (cursor_sync). Bonus: rel-MMOVE and MDRAG moved by AXIS units (+-127 of
//   32767 ~ 7 px!) instead of pixels - both go through mouse_move_abs now.
#define FW_VER   "2.8.2-S2"
// 0 = disabled. If > 0, an idle secure session is dropped after this many ms
// (releases mouse buttons and allows a fresh HELLO). Keep 0 for long scripts.
#define SESSION_IDLE_MS 0UL
#define SND_PIN  A0
#define LED_ERR  17   // Pro Micro RX LED (active LOW) — blinks when a frame fails to decrypt
#define MAX_PT   96     // max plaintext command/reply bytes
#define MAX_LINE (8 + MAX_PT * 2)  // E|ctr|hex

// ================= AES-128 (encrypt only — CTR mode) =================
static const uint8_t SBOX[256] PROGMEM = {
  0x63, 0x7c, 0x77, 0x7b, 0xf2, 0x6b, 0x6f, 0xc5, 0x30, 0x01, 0x67, 0x2b, 0xfe, 0xd7, 0xab, 0x76,
  0xca, 0x82, 0xc9, 0x7d, 0xfa, 0x59, 0x47, 0xf0, 0xad, 0xd4, 0xa2, 0xaf, 0x9c, 0xa4, 0x72, 0xc0,
  0xb7, 0xfd, 0x93, 0x26, 0x36, 0x3f, 0xf7, 0xcc, 0x34, 0xa5, 0xe5, 0xf1, 0x71, 0xd8, 0x31, 0x15,
  0x04, 0xc7, 0x23, 0xc3, 0x18, 0x96, 0x05, 0x9a, 0x07, 0x12, 0x80, 0xe2, 0xeb, 0x27, 0xb2, 0x75,
  0x09, 0x83, 0x2c, 0x1a, 0x1b, 0x6e, 0x5a, 0xa0, 0x52, 0x3b, 0xd6, 0xb3, 0x29, 0xe3, 0x2f, 0x84,
  0x53, 0xd1, 0x00, 0xed, 0x20, 0xfc, 0xb1, 0x5b, 0x6a, 0xcb, 0xbe, 0x39, 0x4a, 0x4c, 0x58, 0xcf,
  0xd0, 0xef, 0xaa, 0xfb, 0x43, 0x4d, 0x33, 0x85, 0x45, 0xf9, 0x02, 0x7f, 0x50, 0x3c, 0x9f, 0xa8,
  0x51, 0xa3, 0x40, 0x8f, 0x92, 0x9d, 0x38, 0xf5, 0xbc, 0xb6, 0xda, 0x21, 0x10, 0xff, 0xf3, 0xd2,
  0xcd, 0x0c, 0x13, 0xec, 0x5f, 0x97, 0x44, 0x17, 0xc4, 0xa7, 0x7e, 0x3d, 0x64, 0x5d, 0x19, 0x73,
  0x60, 0x81, 0x4f, 0xdc, 0x22, 0x2a, 0x90, 0x88, 0x46, 0xee, 0xb8, 0x14, 0xde, 0x5e, 0x0b, 0xdb,
  0xe0, 0x32, 0x3a, 0x0a, 0x49, 0x06, 0x24, 0x5c, 0xc2, 0xd3, 0xac, 0x62, 0x91, 0x95, 0xe4, 0x79,
  0xe7, 0xc8, 0x37, 0x6d, 0x8d, 0xd5, 0x4e, 0xa9, 0x6c, 0x56, 0xf4, 0xea, 0x65, 0x7a, 0xae, 0x08,
  0xba, 0x78, 0x25, 0x2e, 0x1c, 0xa6, 0xb4, 0xc6, 0xe8, 0xdd, 0x74, 0x1f, 0x4b, 0xbd, 0x8b, 0x8a,
  0x70, 0x3e, 0xb5, 0x66, 0x48, 0x03, 0xf6, 0x0e, 0x61, 0x35, 0x57, 0xb9, 0x86, 0xc1, 0x1d, 0x9e,
  0xe1, 0xf8, 0x98, 0x11, 0x69, 0xd9, 0x8e, 0x94, 0x9b, 0x1e, 0x87, 0xe9, 0xce, 0x55, 0x28, 0xdf,
  0x8c, 0xa1, 0x89, 0x0d, 0xbf, 0xe6, 0x42, 0x68, 0x41, 0x99, 0x2d, 0x0f, 0xb0, 0x54, 0xbb, 0x16
};

static uint8_t sget(uint8_t i) {
  return pgm_read_byte(&SBOX[i]);
}
static uint8_t xtime(uint8_t x) {
  return (uint8_t)((x << 1) ^ ((x & 0x80) ? 0x1b : 0x00));
}

// expand 16-byte key into 176-byte round key schedule
static void aes_expand(const uint8_t* key, uint8_t* rk) {
  uint8_t t[4]; uint8_t rcon = 0x01; uint16_t bytes = 16; uint8_t i;
  for (i = 0; i < 16; i++) rk[i] = key[i];
  while (bytes < 176) {
    for (i = 0; i < 4; i++) t[i] = rk[bytes - 4 + i];
    if ((bytes & 15) == 0) {
      uint8_t tt = t[0]; t[0] = t[1]; t[1] = t[2]; t[2] = t[3]; t[3] = tt;
      for (i = 0; i < 4; i++) t[i] = sget(t[i]);
      t[0] ^= rcon; rcon = xtime(rcon);
    }
    for (i = 0; i < 4; i++) {
      rk[bytes] = rk[bytes - 16] ^ t[i];
      bytes++;
    }
  }
}

static void aes_encrypt(const uint8_t* rk, const uint8_t* in, uint8_t* out) {
  uint8_t s[16]; uint8_t i, c, round; uint8_t a0, a1, a2, a3;
  for (i = 0; i < 16; i++) s[i] = in[i] ^ rk[i];
  for (round = 1; round <= 10; round++) {
    for (i = 0; i < 16; i++) s[i] = sget(s[i]);           // SubBytes
    // ShiftRows (state column-major: s[r + 4c])
    uint8_t tt;
    tt = s[1]; s[1] = s[5]; s[5] = s[9]; s[9] = s[13]; s[13] = tt;   // row1 <<1
    tt = s[2]; s[2] = s[10]; s[10] = tt; tt = s[6]; s[6] = s[14]; s[14] = tt; // row2 <<2
    tt = s[15]; s[15] = s[11]; s[11] = s[7]; s[7] = s[3]; s[3] = tt; // row3 <<3
    if (round < 10) {                                      // MixColumns
      for (c = 0; c < 4; c++) {
        a0 = s[4 * c]; a1 = s[4 * c + 1]; a2 = s[4 * c + 2]; a3 = s[4 * c + 3];
        s[4 * c]   = xtime(a0) ^ (xtime(a1) ^ a1) ^ a2 ^ a3;
        s[4 * c + 1] = a0 ^ xtime(a1) ^ (xtime(a2) ^ a2) ^ a3;
        s[4 * c + 2] = a0 ^ a1 ^ xtime(a2) ^ (xtime(a3) ^ a3);
        s[4 * c + 3] = (xtime(a0) ^ a0) ^ a1 ^ a2 ^ xtime(a3);
      }
    }
    for (i = 0; i < 16; i++) s[i] ^= rk[round * 16 + i];   // AddRoundKey
  }
  for (i = 0; i < 16; i++) out[i] = s[i];
}

// ================= crypto session =================
static uint8_t  g_nonce[16];
static uint8_t  g_sk[16];          // session key = AES(PSK, nonce)
static uint8_t  g_rk[176];         // expanded session key
static uint32_t g_txCtr = 0;       // board -> pc frame counter
static uint32_t g_rxCtr = 0;       // pc  -> board expected counter
static bool     g_secure = false;
static uint32_t g_lastFrameMs = 0; // millis() of last accepted frame (idle watchdog)

// keystream block: nonce[0:6] || BE64(frame) || BE16(chunk)
static void ks_block(uint32_t n, uint16_t j, uint8_t* out16) {
  uint8_t ctr[16];
  uint64_t nn = n;                                  // 64-bit shift (fix UB for n>>32+)
  for (uint8_t i = 0; i < 6; i++) ctr[i] = g_nonce[i];
  for (uint8_t i = 0; i < 8; i++) ctr[6 + i] = (uint8_t)(nn >> (56 - 8 * i));
  ctr[14] = (uint8_t)(j >> 8); ctr[15] = (uint8_t)(j & 0xFF);
  aes_encrypt(g_rk, ctr, out16);
}

// XOR crypt in place (CTR) — one shared buffer keeps RAM low
static void frame_crypt(uint32_t n, uint8_t* data, uint16_t len) {
  uint8_t ks[16]; uint16_t pos = 0; uint16_t j = 0;
  while (pos < len) {
    ks_block(n, j++, ks);
    for (uint8_t i = 0; i < 16 && pos < len; i++) data[pos] ^= ks[i], pos++;
  }
}

// ================= serial helpers =================
static char    g_line[MAX_LINE];
static uint8_t g_lineLen = 0;
static uint8_t g_pt[MAX_PT];      // single crypto buffer — CTR XOR works in place

static void hex_encode(const uint8_t* in, uint16_t len, char* out) {
  const char* H = "0123456789ABCDEF";
  for (uint16_t i = 0; i < len; i++) {
    out[2 * i] = H[in[i] >> 4];
    out[2 * i + 1] = H[in[i] & 15];
  }
  out[2 * len] = 0;
}
static bool hex_decode(const char* in, uint8_t* out, uint16_t maxLen, uint16_t* outLen) {
  uint16_t n = 0;
  while (in[2 * n] && in[2 * n + 1]) {
    if (n >= maxLen) return false;
    auto v = [](char c) -> int { if (c >= '0' && c <= '9') return c - '0'; if (c >= 'A' && c <= 'F') return c - 'A' + 10; if (c >= 'a' && c <= 'f') return c - 'a' + 10; return -1; };
  int hi = v(in[2 * n]), lo = v(in[2 * n + 1]);
    if (hi < 0 || lo < 0) return false;
    out[n] = (uint8_t)((hi << 4) | lo); n++;
  }
  *outLen = n; return true;
}

// fw 1.7: reply channel for the command being handled (0 = USB default, &Serial1 = brain link)
static Stream* g_out = 0;

// send encrypted (or plaintext if !secure) reply/event line
static void send_line(const char* pt) {
  if (g_out) {                       // brain link: plain text on Serial1, always
    g_out->println(pt);
    g_out->flush();
    return;
  }
  if (!g_secure) {
    Serial.println(pt);
    return;
  }
  uint16_t len = strlen(pt);
  if (len > MAX_PT) len = MAX_PT;
  uint16_t padded = (len + 15) & ~15;
  memset(g_pt, 0, padded);
  memcpy(g_pt, pt, len);
  g_txCtr++;
  frame_crypt(g_txCtr, g_pt, padded);        // in-place
  // build the wire line into g_line (RX line already consumed) — no extra buffer
  const char* H = "0123456789ABCDEF";
  int p = snprintf(g_line, MAX_LINE, "E|%lu|", (unsigned long)g_txCtr);
  for (uint16_t i = 0; i < padded && p < MAX_LINE - 3; i++) {
    g_line[p++] = H[g_pt[i] >> 4];
    g_line[p++] = H[g_pt[i] & 15];
  }
  g_line[p] = 0;
  // A 32U4 CDC write can be discarded by the core when the host endpoint is not
  // ready; Serial.println then returns 0 and the frame silently disappears while
  // g_txCtr has already advanced, which the PC sees as a lost reply. Retry only
  // when nothing at all went out, so the PC never receives a duplicate frame.
  size_t wrote = Serial.println(g_line);
  Serial.flush();
  if (wrote == 0) {
    delay(2);
    Serial.println(g_line);
    Serial.flush();
  }
}

static void reply_ok(const char* what)  {
  char b[64];
  snprintf(b, sizeof(b), "OK|%s", what);
  send_line(b);
}
static void reply_err(const char* code) {
  char b[64];
  snprintf(b, sizeof(b), "ERR|%s", code);
  send_line(b);
}

// ================= humanized mouse =================
static uint16_t g_scrW = 1920, g_scrH = 1080;
static int32_t  g_curX = 960, g_curY = 540;   // fw 1.8 - tracked in screen pixels; boots at centre (Windows' boot cursor), not the corner

// ARM 2.8 sends only relative HID reports. g_curX/g_curY remain a virtual
// planning canvas for the legacy absolute/HMOVE protocol; g_reportX/g_reportY
// track the last virtual point already emitted. Windows applies every delta to
// the real current cursor, so boot never teleports it to the virtual centre.
static int32_t g_reportX = 960, g_reportY = 540;
static uint8_t g_buttons = 0;

static void mouse_delta_report(int32_t dx, int32_t dy, signed char wheel = 0) {
  while (dx || dy) {
    int8_t sx = (int8_t)constrain(dx, -127L, 127L);
    int8_t sy = (int8_t)constrain(dy, -127L, 127L);
    PortableMouse.move(sx, sy, 0);
    dx -= sx; dy -= sy;
  }
  if (wheel) PortableMouse.move(0, 0, wheel);
}

static void mouse_report(int32_t x, int32_t y, signed char wheel = 0) {
  mouse_delta_report(x - g_reportX, y - g_reportY, wheel);
  g_reportX = x; g_reportY = y;
}

// Emit a DDA path without accumulating rounding error. A one-millisecond pace
// lets USB transmit every report separately instead of collapsing a fast burst.
static void mouse_move_steps(int32_t x, int32_t y, uint16_t steps, uint8_t paceMs) {
  if (steps < 1) {
    mouse_report(x, y);
    g_curX = x; g_curY = y;
    return;
  }
  int32_t sx = g_curX, sy = g_curY;
  int32_t dx = x - sx, dy = y - sy;
  int32_t px = sx, py = sy;
  for (uint16_t i = 1; i <= steps; i++) {
    px = sx + (int32_t)((dx * (int32_t)i) / (int32_t)steps);
    py = sy + (int32_t)((dy * (int32_t)i) / (int32_t)steps);
    mouse_report(px, py);
    // Keep the hardware-proven ARM 2.8.1 HID cadence byte-for-byte: sound
    // sampling must never run between micro-steps.  The ARM 2.8.2 watcher is
    // serviced at command boundaries in the outer loop instead.
    if (i < steps && paceMs) delay(paceMs);
  }
  g_curX = x; g_curY = y;
}

static void mouse_move_relative_native(int32_t dx, int32_t dy) {
  // Target two pixels of path per DDA interval. Integer endpoint rounding can
  // make a report slightly longer than the target, but exhaustive -127..127
  // verification keeps the actual Euclidean HID report at <= sqrt(8) < 3 px.
  uint32_t dist = (uint32_t)sqrt((float)(dx * dx + dy * dy));
  uint16_t steps = (uint16_t)((dist + 1U) / 2U);  // ceil(dist/2)
  mouse_move_steps(g_curX + dx, g_curY + dy, steps, 1);
}

static void mouse_move_abs(int32_t x, int32_t y, bool human) {
  if (!human) {
    mouse_report(x, y);
    g_curX = x;
    g_curY = y;
    return;
  }
  int32_t dx = x - g_curX, dy = y - g_curY;
  uint32_t dist = (uint32_t)sqrt((float)(dx * dx + dy * dy));
  uint8_t steps = (uint8_t)constrain(dist / 8, 6, 48);
  for (uint8_t i = 1; i <= steps; i++) {
    float t = (float)i / steps;
    float e = t * t * (3.0f - 2.0f * t);            // smoothstep easing
    int32_t jx = (i == steps) ? 0 : (int32_t)random(-2, 3);
    int32_t jy = (i == steps) ? 0 : (int32_t)random(-2, 3);
    int32_t px = g_curX + (int32_t)(dx * e) + jx;
    int32_t py = g_curY + (int32_t)(dy * e) + jy;
    mouse_report(px, py);
    delay((uint16_t)random(2, 7));
  }
  g_curX = x; g_curY = y;
}

// fw 2.3: paced by distance. <=60 px stream points keep the fw 2.2 behaviour (split
// into ~3 px micro-steps across <=20 ms) so the >=25 ms Pico feed cadence is never
// overrun. A rare big catch-up jump (Pico coalescing after a lost ack - the 14k-record
// "dart") plays as a ~2.5 px/ms human-fast sweep instead of 8 instant ~57 px steps:
// up to 60 micro-steps of <=8 px over at most 160 ms.
static void mouse_move_stream(int32_t x, int32_t y) {
  int32_t dx = x - g_curX, dy = y - g_curY;
  uint32_t dist = (uint32_t)sqrt((float)(dx * dx + dy * dy));
  uint16_t totalMs;
  if (dist <= 60U) {
    totalMs = 20U;                            // fw 2.2 behaviour, unchanged
  } else {
    totalMs = (uint16_t)((dist * 2U) / 5U);   // ~2500 px/s sweep
    if (totalMs > 160U) totalMs = 160U;       // never hog the brain link longer
  }
  uint8_t steps = (uint8_t)((dist + 2U) / 3U);        // ceil(dist/3): target ~3 px
  uint8_t maxSteps = (uint8_t)(totalMs / 2U);         // never faster than 2 ms/report
  if (steps > maxSteps) steps = maxSteps;
  if (steps > 60) steps = 60;                         // hard cap (RAM-safe, HID-friendly)
  if (steps < 1) {
    mouse_report(x, y);
    g_curX = x; g_curY = y;
    return;
  }
  uint8_t paceMs = (uint8_t)(totalMs / steps);
  if (paceMs < 2) paceMs = 2;
  mouse_move_steps(x, y, steps, paceMs);
}

// fw 1.8: make the next button/wheel report carry the TRACKED cursor position.
// HID-Project's press/release/wheel resend the library's stored axes, which boot to
// (0,0) = screen centre - that was the "click/scroll jumps to the middle" bug.
static void cursor_sync() {
  mouse_report(g_curX, g_curY);
}

static uint8_t parse_button(const char* s) {
  if (!strcmp(s, "left") || !strcmp(s, "0")) return MOUSE_LEFT;
  if (!strcmp(s, "right") || !strcmp(s, "2")) return MOUSE_RIGHT;
  if (!strcmp(s, "middle") || !strcmp(s, "1")) return MOUSE_MIDDLE;
  return MOUSE_LEFT;
}

// ================= brain link (Serial1, fw 1.7) =================
// Plain-text line channel for the Pico brain on pins 0 (RX) / 1 (TX).
static char    g_line1[MAX_PT];
static uint8_t g_line1Len = 0;

// fw 2.6: helper-free Windows lifecycle signal. USBDevice.configured() and
// USBDevice.isSuspended() come from the ATmega32U4 Arduino USB core. State changes
// are debounced and reported only on the private Serial1 link to the Pico.
#define HOST_USB_DOWN    0
#define HOST_USB_SUSPEND 1
#define HOST_USB_UP      2
static uint8_t g_hostUsbState = HOST_USB_DOWN;
static uint8_t g_hostUsbCandidate = HOST_USB_DOWN;
static bool g_hostUsbInitialized = false;
static unsigned long g_hostUsbCandidateSince = 0;
static unsigned long g_hostUsbLastReport = 0;
#define HOST_USB_DEBOUNCE_MS 120UL
#define HOST_USB_HEARTBEAT_MS 2000UL

static uint8_t read_host_usb_state() {
  if (!USBDevice.configured()) return HOST_USB_DOWN;
  if (USBDevice.isSuspended()) return HOST_USB_SUSPEND;
  return HOST_USB_UP;
}

static const __FlashStringHelper* host_usb_name(uint8_t s) {
  if (s == HOST_USB_UP) return F("UP");
  if (s == HOST_USB_SUSPEND) return F("SUSPEND");
  return F("DOWN");
}

static void report_host_usb(uint8_t s) {
  Serial1.print(F("EVT|HOSTUSB|"));
  Serial1.println(host_usb_name(s));
  Serial1.flush();
}

static void poll_host_usb() {
  uint8_t sample = read_host_usb_state();
  unsigned long now = millis();
  if (!g_hostUsbInitialized) {
    g_hostUsbCandidate = sample;
    g_hostUsbState = sample;
    g_hostUsbCandidateSince = now;
    g_hostUsbInitialized = true;
    report_host_usb(sample);
    g_hostUsbLastReport = now;
    return;
  }
  if (sample != g_hostUsbCandidate) {
    g_hostUsbCandidate = sample;
    g_hostUsbCandidateSince = now;
    return;
  }
  if (sample != g_hostUsbState && now - g_hostUsbCandidateSince >= HOST_USB_DEBOUNCE_MS) {
    g_hostUsbState = sample;
    report_host_usb(sample);
    g_hostUsbLastReport = now;
    return;
  }
  // Re-announce the current state so a Pico that boots more slowly cannot miss
  // the one initial UART event. The Pico deduplicates identical heartbeats.
  if (now - g_hostUsbLastReport >= HOST_USB_HEARTBEAT_MS) {
    report_host_usb(g_hostUsbState);
    g_hostUsbLastReport = now;
  }
}

// fw 2.3: optional integrity frame from the Pico brain: '#' + 2 hex chars (sum of the
// payload bytes mod 256) + '|' + payload. The unframed pico<->arm UART drops bytes in
// the field (the 14k record's 1500 px out-and-back = a corrupted MMOVE). A framed line
// whose sum does not match is DROPPED with ERR|CKSUM, never executed; legacy unframed
// lines pass through untouched (a fw <= 2.2 Pico keeps working). Runs inside the line
// assembler, so EVERY consumer (loop, WSND/TRGSND/SCAL abort checks) sees verified,
// unwrapped payloads - a framed HALT still aborts. Returns false = drop the line.
#define BRAIN_BAUD 57600UL   // fw 2.4: must match ARM_BAUD in the Pico's code.py
static bool g_framedLink = false;  // fw 2.4: a valid '#' frame was seen on Serial1
#define MOVE_SANITY_PX 700         // fw 2.5: no legitimate path point hops this far
static uint16_t g_badMoves = 0;    // streamed targets refused as impossible

static bool frame_unwrap(char* line) {
  if (line[0] != '#') {
    // fw 2.4: STRICT. An RX overflow can eat a whole '#XX|' header, and fw 2.3
    // executed the header-less remnant (a truncated MMOVE dragged the cursor
    // ~1000 px). Once the link has proven it frames, bare lines are never run.
    if (g_framedLink) return false;
    return true;                                 // legacy Pico (fw <= 2.3 protocol)
  }
  if (line[1] == 0 || line[2] == 0 || line[3] != '|') return false;   // malformed
  uint8_t want = (uint8_t)strtol(line + 1, 0, 16);
  const char* payload = line + 4;
  uint8_t sum = 0;
  for (const char* p = payload; *p; p++) sum = (uint8_t)(sum + (uint8_t)*p);
  if (sum != want) return false;                 // corrupted on the wire: never execute
  g_framedLink = true;                           // fw 2.4: latch strict mode
  memmove(line, payload, strlen(payload) + 1);   // unwrap in place
  return true;
}

static bool serial1_line_ready() {
  while (Serial1.available()) {
    char c = (char)Serial1.read();
    if (c == '\r') continue;
    if (c == '\n') {
      if (g_line1Len == 0) continue;
      g_line1[g_line1Len] = 0;
      g_line1Len = 0;
      if (!frame_unwrap(g_line1)) {              // fw 2.3: bad checksum -> drop + report
        g_out = &Serial1; reply_err(g_line1[0] == '#' ? "CKSUM" : "NOFRAME"); g_out = 0;
        continue;                                // keep reading; no line was consumed
      }
      return true;
    }
    if (g_line1Len < MAX_PT - 1) g_line1[g_line1Len++] = c;
  }
  return false;
}

// ================= sound (A0) =================
static uint16_t sound_peak(uint16_t windowMs) {
  unsigned long t0 = millis(); uint16_t peak = 0;
  while (millis() - t0 < windowMs) {
    int v = analogRead(SND_PIN); int d = v - 512; if (d < 0) d = -d;
    if (d > peak) peak = d;
  }
  return peak;
}

// returns: 0 = still listening, 1 = detected, 2 = aborted by HALT, 3 = timeout
static uint8_t listen_loop(uint16_t thr, uint16_t minMs, uint32_t timeoutMs) {
  unsigned long start = millis(); uint32_t sustained = 0;
  while (true) {
    if (timeoutMs && (millis() - start >= timeoutMs)) return 3;
    if (sound_peak(10) >= thr) {
      sustained += 10;
      if (sustained >= minMs) return 1;
    }
    else sustained = 0;
    if (Serial.available()) {                       // stay abortable
      if (read_line_blocking(50)) {
        static char cmd[MAX_PT];
        if (decrypt_to(g_line, cmd, MAX_PT) && !strncmp(cmd, "HALT", 4)) return 2;
        reply_err("BUSY");                          // other commands rejected while listening
      }
    }
    if (serial1_line_ready()) {                     // fw 1.7: the brain link stays abortable too
      if (!strncmp(g_line1, "HALT", 4)) return 2;
      g_out = &Serial1; reply_err("BUSY"); g_out = 0;
    }
  }
}

// ================= frame decrypt =================
// decrypts an "E|n|hex" line into cmd; returns false if not encrypted frame / replay
static bool decrypt_to(const char* line, char* out, uint8_t outMax) {
  if (!g_secure || line[0] != 'E' || line[1] != '|') return false;
  const char* p1 = line + 2;
  const char* p2 = strchr(p1, '|');
  if (!p2) return false;
  uint32_t n = strtoul(p1, NULL, 10);
  // Strictly increasing is all replay protection needs. Demanding exactly
  // +1 meant one dropped frame made the board answer ERR|REPLAY forever.
  if (n <= g_rxCtr) {
    reply_err("REPLAY");
    return false;
  }
  uint16_t ctLen = 0;
  if (!hex_decode(p2 + 1, g_pt, MAX_PT, &ctLen) || ctLen == 0 || (ctLen & 15)) return false;
  g_rxCtr = n;
  frame_crypt(n, g_pt, ctLen);               // in-place
  uint8_t plen = ctLen;
  while (plen > 0 && g_pt[plen - 1] == 0) plen--;   // strip zero padding
  if (plen >= outMax) plen = outMax - 1;
  memcpy(out, g_pt, plen); out[plen] = 0;
  return true;
}

static bool read_line_blocking(uint16_t timeoutMs) {
  unsigned long t0 = millis(); g_lineLen = 0;
  while (millis() - t0 < timeoutMs) {
    while (Serial.available()) {
      char c = (char)Serial.read();
      if (c == '\r') continue;
      if (c == '\n') {
        g_line[g_lineLen] = 0;
        return g_lineLen > 0;
      }
      if (g_lineLen < MAX_LINE - 1) g_line[g_lineLen++] = c;
    }
  }
  return false;
}

// ================= HALT =================
static void do_halt() {
  ARM_SOUND_CANCEL();
  g_buttons = 0;
  PortableMouse.releaseAll();
  mouse_report(g_curX, g_curY);
}

// Drops the current session so the board is ready for a fresh HELLO
// without unplugging the USB cable.
static void session_reset() {
  do_halt();
  g_secure = false;
  g_txCtr = 0;
  g_rxCtr = 0;
}

// Keep long physical clicks HALT-abortable instead of blocking in delay(hold).
static bool wait_mouse_hold_or_abort(uint16_t holdMs) {
  unsigned long started = millis();
  while (millis() - started < holdMs) {
    if (serial1_line_ready()) {
      if (!strncmp(g_line1, "HALT", 4)) {
        do_halt();
        send_line("ERR|ABORTED|MCLICK");
        return false;
      }
      reply_err("BUSY");
    }
    if (Serial.available() && read_line_blocking(4)) {
      static char pending[MAX_PT];
      if (decrypt_to(g_line, pending, MAX_PT) && !strncmp(pending, "HALT", 4)) {
        do_halt();
        send_line("ERR|ABORTED|MCLICK");
        return false;
      }
      reply_err("BUSY");
    }
    delay(2);
  }
  return true;
}

// ================= command handler =================
static void handle(char* cmd) {
  char* args = strchr(cmd, '|');
  if (args) *args++ = 0; else args = (char*)"";

  if (!strcmp(cmd, "HALT"))    {
    do_halt();
    reply_ok("HALT");
    return;
  }
  if (!strcmp(cmd, "BYE"))     {
    reply_ok("BYE");  // clean end of a PC run
    session_reset();
    return;
  }
  if (!strcmp(cmd, "PING"))    {
    reply_ok("PING");
    return;
  }
  if (!strcmp(cmd, "VER"))     {
    char b[96];
    // Capability markers are advertised only on the private brain link; the
    // encrypted PC reply remains backward-compatible.
    snprintf(b, sizeof(b), "OK|VER|%s|relMouse=1|sound=1|enc=1|badmoves=%u%s", FW_VER, (unsigned)g_badMoves,
             (g_out == &Serial1) ? "|FRM|HOSTUSB=1" : "");
    send_line(b);
    return;
  }
  if (!strcmp(cmd, "SETRES"))  {
    int w = 0, h = 0;
    if (sscanf(args, "%d,%d", &w, &h) == 2 && w > 0 && h > 0) {
      g_scrW = w;
      g_scrH = h;
      reply_ok("SETRES");
    }
    else reply_err("ARG");
    return;
  }
  if (!strcmp(cmd, "MMOVE")) {
    if (ARM_SOUND_BLOCKS_MOVE()) {
      reply_ok("MMOVE");
      return;
    }
    int x = 0, y = 0; char mode[8] = "abs"; char hm[4] = "1";
    if (sscanf(args, "%d,%d,%7[^,],%3s", &x, &y, mode, hm) >= 3) {
      if (!strcmp(mode, "rel")) {
        // ARM 2.8: genuine relative HID. Windows applies this delta to the
        // actual cursor; the virtual ledger is updated only for compatibility.
        mouse_move_relative_native(x, y);
      }
      else if (hm[0] == '2') {
        // fw 2.5: forensics + last line of defence. A streamed path point sits a few
        // dozen px from the tracked position (the brain coalesces, so a few hundred px
        // is still legitimate), but a jump beyond MOVE_SANITY_PX can only be a corrupt
        // line that matched its checksum anyway - the '1216' -> '216' digit drop.
        // Refuse it and echo the RAW wire bytes so the brain log shows what arrived.
        int32_t ddx = (int32_t)x - g_curX, ddy = (int32_t)y - g_curY;
        if (ddx * ddx + ddy * ddy > (int32_t)MOVE_SANITY_PX * (int32_t)MOVE_SANITY_PX) {
          g_badMoves++;
          reply_ok("MMOVE");                             // keep the brain's ack ledger sane
          return;
        }
        mouse_move_stream(x, y);                         // fw 1.9: interpolated path point
      }
      else mouse_move_abs(x, y, hm[0] == '1');
      reply_ok("MMOVE");
    } else reply_err("ARG");
    return;
  }
  if (!strcmp(cmd, "MCLICK")) {
    char bs[8] = "left"; int cnt = 1; int hmn = 0, hmx = 0;
    sscanf(args, "%7[^,],%d,%d,%d", bs, &cnt, &hmn, &hmx);
    uint8_t b = parse_button(bs);
    if (cnt < 1) cnt = 1;
    cursor_sync();            // fw 1.8: click at the tracked position, never at centre
    for (int i = 0; i < cnt; i++) {
      g_buttons |= b;
      PortableMouse.press(b);
      int hold = (hmx > hmn && hmn > 0) ? (int)random(hmn, hmx + 1) : 45;
      if (!wait_mouse_hold_or_abort((uint16_t)hold)) return;
      PortableMouse.release(b);
      g_buttons &= (uint8_t)~b;
      if (i + 1 < cnt) delay((uint16_t)random(60, 140));
    }
    reply_ok("MCLICK");
    return;
  }
  if (!strcmp(cmd, "MDOWN")) {
    cursor_sync();            // fw 1.8
    uint8_t b = parse_button(args);
    g_buttons |= b;
    PortableMouse.press(b);
    reply_ok("MDOWN");
    return;
  }
  if (!strcmp(cmd, "MUP"))   {
    cursor_sync();            // fw 1.8
    uint8_t b = parse_button(args);
    PortableMouse.release(b);
    g_buttons &= (uint8_t)~b;
    reply_ok("MUP");
    return;
  }
  if (!strcmp(cmd, "MDRAG")) {
    int dx = 0, dy = 0; char bs[8] = "left";
    if (sscanf(args, "%d,%d,%7s", &dx, &dy, bs) >= 2) {
      uint8_t b = parse_button(bs);
      cursor_sync();          // fw 1.8
      g_buttons |= b;
      PortableMouse.press(b); delay(60);
      mouse_move_abs(g_curX + dx, g_curY + dy, false);   // fw 1.8: drag delta in PIXELS (was axis units)
      delay(60);
      PortableMouse.release(b);
      g_buttons &= (uint8_t)~b;
      reply_ok("MDRAG");
    } else reply_err("ARG");
    return;
  }
  if (!strcmp(cmd, "MWHEEL")) {
    int d = atoi(args);
    // fw 1.8: the wheel report carries the TRACKED axes. The old move(0,0,d) resent the
    // library's boot state (0,0 = screen centre) and dragged the cursor to the middle.
    mouse_report(g_curX, g_curY, (int8_t)d);
    reply_ok("MWHEEL");
    return;
  }
  if (!strcmp(cmd, "SCAL")) {
    int ms = atoi(args); if (ms <= 0) ms = 500;
    // The old loop hammered analogRead for the whole window without ever
    // returning to the USB stack. With ms >= ~800 the CDC write that followed
    // was dropped by the core and the reply never reached the PC (measured:
    // SCAL|800 lost its reply 15 times out of 15, while SCAL|120 never did).
    // listen_loop has always been reliable because it works in 10 ms slices,
    // so SCAL now does the same and stays abortable while it measures.
    unsigned long t0 = millis(); uint32_t sum = 0; uint16_t peak = 0; uint32_t n = 0;
    while (millis() - t0 < (unsigned long)ms) {
      unsigned long c0 = millis();
      while (millis() - c0 < 10) {
        int v = analogRead(SND_PIN); int d = v - 512; if (d < 0) d = -d;
        sum += d; n++; if (d > peak) peak = d;
      }
      delay(1);                                   // let USB breathe between slices
      if (Serial.available()) {                   // stay abortable, like listen_loop
        if (read_line_blocking(50)) {
          static char abrt[MAX_PT];
          if (decrypt_to(g_line, abrt, MAX_PT) && !strncmp(abrt, "HALT", 4)) {
            reply_ok("HALT");
            return;
          }
          reply_err("BUSY");
        }
      }
      if (serial1_line_ready()) {               // fw 1.7: brain link aborts SCAL too
        if (!strncmp(g_line1, "HALT", 4)) {
          g_out = &Serial1; reply_ok("HALT"); g_out = 0;
          return;
        }
        g_out = &Serial1; reply_err("BUSY"); g_out = 0;
      }
    }
    char b[64]; snprintf(b, sizeof(b), "OK|SCAL|avg=%lu|max=%u", (unsigned long)(n ? sum / n : 0), peak);
    send_line(b);
    return;
  }
  if (!strcmp(cmd, "WSND")) {
    int thr = 60; unsigned long minMs = 100, timeoutMs = 30000;
    sscanf(args, "%d,%lu,%lu", &thr, &minMs, &timeoutMs);
    uint8_t r = listen_loop((uint16_t)thr, (uint16_t)minMs, timeoutMs);
    if (r == 1) {
      char b[64];
      snprintf(b, sizeof(b), "OK|WSND|DETECTED|t=%lu", (unsigned long)millis());
      send_line(b);
    }
    else if (r == 2) send_line("OK|WSND|ABORTED");
    else reply_err("TIMEOUT|WSND");
    return;
  }
  if (!strcmp(cmd, "TRGSND")) {
    int thr = 60, act = 1; unsigned long minMs = 100, timeoutMs = 30000;
    int rMin = 80, rMax = 180, hMin = 30, hMax = 90;
    sscanf(args, "%d,%lu,%lu,%d,%d,%d,%d,%d", &thr, &minMs, &timeoutMs, &act, &rMin, &rMax, &hMin, &hMax);
    uint8_t r = listen_loop((uint16_t)thr, (uint16_t)minMs, timeoutMs);
    if (r == 2) {
      send_line("OK|TRGSND|ABORTED");
      return;
    }
    if (r == 3) {
      reply_err("TIMEOUT|TRGSND");
      return;
    }
    long react = random(rMin, rMax + 1);
    long hold  = random(hMin, hMax + 1);
    delay((uint16_t)react);
    uint8_t b = (act == 2) ? MOUSE_RIGHT : (act == 3) ? MOUSE_MIDDLE : MOUSE_LEFT;
    cursor_sync();            // fw 1.8: the armed click must not jump to centre
    g_buttons |= b;
    PortableMouse.press(b); delay((uint16_t)hold); PortableMouse.release(b);
    g_buttons &= (uint8_t)~b;
    char eb[80]; snprintf(eb, sizeof(eb), "EVT|TRG|react=%ld|hold=%ld|t=%lu", react, hold, (unsigned long)millis());
    send_line(eb);
    reply_ok("TRGSND");
    return;
  }
  reply_err("UNKNOWN");
}

// ================= handshake =================
// Handles one plaintext "HELLO|<nonce hex>" line. Works both before a session
// exists and while a stale session is still open (re-handshake), so a new PC
// run never needs a power cycle.
static bool hello_from_line(const char* line) {
  if (strncmp(line, "HELLO|", 6)) return false;
  uint16_t nlen = 0;
  if (!hex_decode(line + 6, g_nonce, 16, &nlen) || nlen != 16) {
    Serial.println(F("ERR|NONCE"));
    return false;
  }
  do_halt();                            // previous session may have left mouse buttons pressed
  aes_expand(AMS_PSK, g_rk);            // expand PSK straight into the global schedule (no 176B on stack)
  aes_encrypt(g_rk, g_nonce, g_sk);     // proof = AES(PSK, nonce) = session key
  aes_expand(g_sk, g_rk);
  char hexbuf[33]; hex_encode(g_sk, 16, hexbuf);
  Serial.print(F("OK|HELLO|AMS_BOARD|")); Serial.print(FW_VER); Serial.print('|'); Serial.println(hexbuf);
  g_secure = true; g_txCtr = 0; g_rxCtr = 0;
  g_lastFrameMs = millis();
  return true;
}

static bool do_handshake(uint32_t waitMs) {
  unsigned long t0 = millis();
  while (millis() - t0 < waitMs) {
    if (!read_line_blocking(40)) continue;   // fw 1.7: bounded so Serial1 stays responsive
    if (hello_from_line(g_line)) return true;
  }
  return false;
}

// ================= arduino =================
void setup() {
  pinMode(SND_PIN, INPUT);
  pinMode(LED_ERR, OUTPUT); digitalWrite(LED_ERR, HIGH);   // LED off (active low)
  Serial.begin(115200);
  Serial1.begin(BRAIN_BAUD);   // fw 2.4: 57600 - double the edge margin on the BSS138 shifter
  PortableMouse.begin();
  // seed random from analog noise on A0 (LSBs) + micros
  uint32_t seed = 0;
  for (uint8_t i = 0; i < 32; i++) {
    seed = (seed << 1) ^ (analogRead(SND_PIN) & 1);
  }
  randomSeed(seed ^ micros());
  do_halt();
}

void loop() {
  poll_host_usb();  // fw 2.6: report configured/suspended lifecycle to Pico over Serial1
  // fw 1.7: the brain link is serviced first and is never gated by the USB session.
  if (serial1_line_ready()) {
    g_out = &Serial1;
    handle(g_line1);
    g_out = 0;
  }
  if (!g_secure) {
    // An open CDC/DTR port with no queued bytes must not stall Serial1 mouse.
    if (Serial.available()) do_handshake(40);
    else delay(1);                  // headless arm behind the Pico -> keep the link snappy
    return;
  }
  // fw 2.2: never wait 50 ms on an idle/stale USB session. The Pico brain link
  // stays responsive even after a version-check HELLO opens and closes the CDC port.
  if (Serial.available() && read_line_blocking(50)) {
    static char cmd[MAX_PT];              // static: keep 128B off the stack
    if (decrypt_to(g_line, cmd, MAX_PT)) {
      g_lastFrameMs = millis();
      handle(cmd);
    }
    else if (hello_from_line(g_line)) { }  // new PC run on a stale session → fresh handshake
    else {
      digitalWrite(LED_ERR, LOW);  // frame rejected → RX LED blink
      delay(30);
      digitalWrite(LED_ERR, HIGH);
    }
  }
  if (SESSION_IDLE_MS && (millis() - g_lastFrameMs > SESSION_IDLE_MS)) session_reset();  // idle watchdog (off by default)
}
