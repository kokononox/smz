#include "hid_keyboard.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "tusb.h"

#define REPORT_ID_KEYBOARD 0u
#define MAX_KEYS 6u
/* Keep each printable key down across more than one typical 60 Hz game/UI
 * input frame.  Eight milliseconds was USB-valid but some login/game fields
 * observed characters in apparent bursts or missed them entirely. */
#define TYPE_HOLD_MS 24u
#define LIVE_LANE 0xffu
#define INTERNAL_LANE 0xfeu

typedef enum ActorPhase {
    ACTOR_IDLE = 0,
    ACTOR_SEND_PRESS,
    ACTOR_WAIT_HOLD,
    ACTOR_SEND_RESTORE,
    ACTOR_SEND_PERSISTENT,
    ACTOR_TYPE_PREPARE,
    ACTOR_TYPE_SEND_PRESS,
    ACTOR_TYPE_WAIT_HOLD,
    ACTOR_TYPE_SEND_RELEASE,
    ACTOR_TYPE_WAIT_GAP,
} ActorPhase;

typedef struct KeyboardActor {
    uint8_t persistent_modifiers;
    uint8_t persistent_keys[MAX_KEYS];
    uint8_t report_modifiers;
    uint8_t report_keys[MAX_KEYS];
    uint8_t lane;
    uint8_t phase;
    uint32_t due;
    bool release_pending;
    bool completion_pending;
    uint8_t completion_lane;

    const uint8_t *text_cursor;
    const uint8_t *text_end;
    uint8_t type_pending[3];
    uint8_t type_pending_count;
    uint8_t type_pending_index;
    uint8_t type_actual;
    uint32_t rng;
    uint32_t hmin, hmax, wmin, wmax, pmin, pmax;
    uint32_t think_min, think_max, typo_min, typo_max;
    uint16_t word_chance, think_chance;
    uint32_t eligible_since_typo, typo_due;
    char live_text[72];
    char live_ack[12];
    bool live_reply_pending;
} KeyboardActor;

static KeyboardActor actor;

static bool deadline_reached(uint32_t now, uint32_t due) {
    return (int32_t)(now - due) >= 0;
}

static uint32_t random_next(void) {
    uint32_t x = actor.rng ? actor.rng : 0x9e3779b9u;
    x ^= x << 13; x ^= x >> 17; x ^= x << 5;
    actor.rng = x;
    return x;
}

static uint32_t random_range(uint32_t lo, uint32_t hi) {
    if (hi <= lo) return lo;
    return lo + random_next() % (hi - lo + 1u);
}

static bool chance(uint16_t percent) {
    return percent >= 100u || (percent && random_next() % 100u < percent);
}

static bool add_key(uint8_t keys[MAX_KEYS], uint8_t key) {
    if (!key) return true;
    for (uint8_t i = 0; i < MAX_KEYS; ++i) {
        if (keys[i] == key) return true;
        if (!keys[i]) {
            keys[i] = key;
            return true;
        }
    }
    return false;
}

static bool vk_to_hid(uint8_t vk, uint8_t *modifier, uint8_t *keycode) {
    *modifier = 0;
    *keycode = 0;
    if (vk >= 'A' && vk <= 'Z') {
        *keycode = (uint8_t)(HID_KEY_A + vk - 'A');
        return true;
    }
    if (vk >= '0' && vk <= '9') {
        *keycode = vk == '0' ? HID_KEY_0 : (uint8_t)(HID_KEY_1 + vk - '1');
        return true;
    }
    if (vk >= 112u && vk <= 123u) {
        *keycode = (uint8_t)(HID_KEY_F1 + vk - 112u);
        return true;
    }
    if (vk >= 96u && vk <= 105u) {
        *keycode = vk == 96u
            ? HID_KEY_KEYPAD_0
            : (uint8_t)(HID_KEY_KEYPAD_1 + vk - 97u);
        return true;
    }
    switch (vk) {
        case 8: *keycode = HID_KEY_BACKSPACE; return true;
        case 9: *keycode = HID_KEY_TAB; return true;
        case 13: *keycode = HID_KEY_ENTER; return true;
        case 27: *keycode = HID_KEY_ESCAPE; return true;
        case 32: *keycode = HID_KEY_SPACE; return true;
        case 37: *keycode = HID_KEY_ARROW_LEFT; return true;
        case 38: *keycode = HID_KEY_ARROW_UP; return true;
        case 39: *keycode = HID_KEY_ARROW_RIGHT; return true;
        case 40: *keycode = HID_KEY_ARROW_DOWN; return true;
        case 91: *modifier = KEYBOARD_MODIFIER_LEFTGUI; return true;
        case 106: *keycode = HID_KEY_KEYPAD_MULTIPLY; return true;
        case 107: *keycode = HID_KEY_KEYPAD_ADD; return true;
        case 109: *keycode = HID_KEY_KEYPAD_SUBTRACT; return true;
        case 110: *keycode = HID_KEY_KEYPAD_DECIMAL; return true;
        case 111: *keycode = HID_KEY_KEYPAD_DIVIDE; return true;
        case 160: *modifier = KEYBOARD_MODIFIER_LEFTSHIFT; return true;
        case 162: *modifier = KEYBOARD_MODIFIER_LEFTCTRL; return true;
        case 164: *modifier = KEYBOARD_MODIFIER_LEFTALT; return true;
        default: return false;
    }
}

static bool ascii_to_hid(uint8_t value, uint8_t *modifier, uint8_t *keycode) {
    *modifier = 0; *keycode = 0;
    if (value >= 'a' && value <= 'z') { *keycode = (uint8_t)(HID_KEY_A + value - 'a'); return true; }
    if (value >= 'A' && value <= 'Z') { *modifier = KEYBOARD_MODIFIER_LEFTSHIFT; *keycode = (uint8_t)(HID_KEY_A + value - 'A'); return true; }
    if (value >= '1' && value <= '9') { *keycode = (uint8_t)(HID_KEY_1 + value - '1'); return true; }
    if (value == '0') { *keycode = HID_KEY_0; return true; }
    switch (value) {
        case ' ': *keycode=HID_KEY_SPACE; return true;
        case '\b': *keycode=HID_KEY_BACKSPACE; return true;
        case '\n': case '\r': *keycode=HID_KEY_ENTER; return true;
        case '\t': *keycode=HID_KEY_TAB; return true;
        case '-': *keycode=HID_KEY_MINUS; return true;
        case '_': *modifier=KEYBOARD_MODIFIER_LEFTSHIFT; *keycode=HID_KEY_MINUS; return true;
        case '=': *keycode=HID_KEY_EQUAL; return true;
        case '+': *modifier=KEYBOARD_MODIFIER_LEFTSHIFT; *keycode=HID_KEY_EQUAL; return true;
        case '[': *keycode=HID_KEY_BRACKET_LEFT; return true;
        case '{': *modifier=KEYBOARD_MODIFIER_LEFTSHIFT; *keycode=HID_KEY_BRACKET_LEFT; return true;
        case ']': *keycode=HID_KEY_BRACKET_RIGHT; return true;
        case '}': *modifier=KEYBOARD_MODIFIER_LEFTSHIFT; *keycode=HID_KEY_BRACKET_RIGHT; return true;
        case '\\': *keycode=HID_KEY_BACKSLASH; return true;
        case '|': *modifier=KEYBOARD_MODIFIER_LEFTSHIFT; *keycode=HID_KEY_BACKSLASH; return true;
        case ';': *keycode=HID_KEY_SEMICOLON; return true;
        case ':': *modifier=KEYBOARD_MODIFIER_LEFTSHIFT; *keycode=HID_KEY_SEMICOLON; return true;
        case '\'': *keycode=HID_KEY_APOSTROPHE; return true;
        case '"': *modifier=KEYBOARD_MODIFIER_LEFTSHIFT; *keycode=HID_KEY_APOSTROPHE; return true;
        case '`': *keycode=HID_KEY_GRAVE; return true;
        case '~': *modifier=KEYBOARD_MODIFIER_LEFTSHIFT; *keycode=HID_KEY_GRAVE; return true;
        case ',': *keycode=HID_KEY_COMMA; return true;
        case '<': *modifier=KEYBOARD_MODIFIER_LEFTSHIFT; *keycode=HID_KEY_COMMA; return true;
        case '.': *keycode=HID_KEY_PERIOD; return true;
        case '>': *modifier=KEYBOARD_MODIFIER_LEFTSHIFT; *keycode=HID_KEY_PERIOD; return true;
        case '/': *keycode=HID_KEY_SLASH; return true;
        case '?': *modifier=KEYBOARD_MODIFIER_LEFTSHIFT; *keycode=HID_KEY_SLASH; return true;
        case '!': *modifier=KEYBOARD_MODIFIER_LEFTSHIFT; *keycode=HID_KEY_1; return true;
        case '@': *modifier=KEYBOARD_MODIFIER_LEFTSHIFT; *keycode=HID_KEY_2; return true;
        case '#': *modifier=KEYBOARD_MODIFIER_LEFTSHIFT; *keycode=HID_KEY_3; return true;
        case '$': *modifier=KEYBOARD_MODIFIER_LEFTSHIFT; *keycode=HID_KEY_4; return true;
        case '%': *modifier=KEYBOARD_MODIFIER_LEFTSHIFT; *keycode=HID_KEY_5; return true;
        case '^': *modifier=KEYBOARD_MODIFIER_LEFTSHIFT; *keycode=HID_KEY_6; return true;
        case '&': *modifier=KEYBOARD_MODIFIER_LEFTSHIFT; *keycode=HID_KEY_7; return true;
        case '*': *modifier=KEYBOARD_MODIFIER_LEFTSHIFT; *keycode=HID_KEY_8; return true;
        case '(': *modifier=KEYBOARD_MODIFIER_LEFTSHIFT; *keycode=HID_KEY_9; return true;
        case ')': *modifier=KEYBOARD_MODIFIER_LEFTSHIFT; *keycode=HID_KEY_0; return true;
        default: return false;
    }
}

static bool send_report(uint8_t modifiers, const uint8_t keys[MAX_KEYS]) {
    if (!tud_mounted() || !tud_hid_ready()) return false;
    return tud_hid_keyboard_report(REPORT_ID_KEYBOARD, modifiers, keys);
}

static bool apply_vk(uint8_t vk, bool down) {
    uint8_t modifier, key;
    if (!vk_to_hid(vk, &modifier, &key)) return false;
    if (modifier) {
        if (down) actor.persistent_modifiers |= modifier;
        else actor.persistent_modifiers &= (uint8_t)~modifier;
        return true;
    }
    if (down) return add_key(actor.persistent_keys, key);
    for (uint8_t i = 0; i < MAX_KEYS; ++i)
        if (actor.persistent_keys[i] == key) actor.persistent_keys[i] = 0;
    return true;
}

static const uint8_t *json_field(const uint8_t *data, uint32_t size, const char *key) {
    size_t key_size = strlen(key);
    for (uint32_t i = 0; i + key_size + 3u < size; ++i) {
        if (data[i] != '"' || memcmp(data + i + 1u, key, key_size) ||
            data[i + key_size + 1u] != '"' || data[i + key_size + 2u] != ':') continue;
        return data + i + key_size + 3u;
    }
    return NULL;
}

static uint32_t json_u32(const uint8_t *data, uint32_t size, const char *key, uint32_t fallback) {
    const uint8_t *p = json_field(data, size, key), *end = data + size;
    if (!p || p >= end || *p < '0' || *p > '9') return fallback;
    uint32_t value = 0;
    while (p < end && *p >= '0' && *p <= '9') {
        uint32_t digit = (uint32_t)(*p++ - '0');
        if (value > (UINT32_MAX - digit) / 10u) return fallback;
        value = value * 10u + digit;
    }
    return value;
}

static bool json_true(const uint8_t *data, uint32_t size, const char *key) {
    const uint8_t *p = json_field(data, size, key);
    return p && (size_t)(data + size - p) >= 4u && !memcmp(p, "true", 4u);
}

static bool json_string_equals(const uint8_t *data, uint32_t size, const char *key, const char *value) {
    const uint8_t *p = json_field(data, size, key);
    size_t length = strlen(value);
    return p && (size_t)(data + size - p) >= length + 2u && *p == '"' &&
           !memcmp(p + 1u, value, length) && p[length + 1u] == '"';
}

static int hex_digit(uint8_t value) {
    if (value >= '0' && value <= '9') return value - '0';
    if (value >= 'a' && value <= 'f') return value - 'a' + 10;
    if (value >= 'A' && value <= 'F') return value - 'A' + 10;
    return -1;
}

static bool next_json_char(const uint8_t **cursor, const uint8_t *end, uint8_t *out) {
    if (*cursor >= end) return false;
    uint8_t value = *(*cursor)++;
    if (value == '\\') {
        if (*cursor >= end) return false;
        value = *(*cursor)++;
        switch (value) {
            case '"': case '\\': case '/': *out=value; return true;
            case 'b': *out='\b'; return true;
            case 'f': *out='\f'; return true;
            case 'n': *out='\n'; return true;
            case 'r': *out='\r'; return true;
            case 't': *out='\t'; return true;
            case 'u': {
                if ((size_t)(end - *cursor) < 4u) return false;
                int code = 0;
                for (uint8_t i=0;i<4u;++i) { int digit=hex_digit(*(*cursor)++); if (digit<0) return false; code=(code<<4)|digit; }
                if (code <= 0 || code > 0x7f) return false;
                *out=(uint8_t)code; return true;
            }
            default: return false;
        }
    }
    if (value < 0x20u || value > 0x7eu || value == '"') return false;
    *out=value; return true;
}

static bool type_configure(const AbvmVm *vm, uint16_t constant_id, uint32_t now) {
    const uint8_t *data, *text, *end;
    uint32_t size;
    if (!abvm_constant(vm, constant_id, ABVM_CONST_TYPE, &data, &size) ||
        json_true(data,size,"secret") || json_string_equals(data,size,"mode","clipboard")) return false;
    text = json_field(data,size,"text"); end = data + size;
    if (!text || text >= end || *text++ != '"') return false;
    const uint8_t *close=text; bool escaped=false;
    while (close<end) { uint8_t v=*close; if (!escaped && v=='"') break; escaped=!escaped && v=='\\'; if (v!='\\') escaped=false; ++close; }
    if (close>=end) return false;
    const uint8_t *check=text; uint8_t value;
    while (check<close) if (!next_json_char(&check,close,&value) || !ascii_to_hid(value,&actor.report_modifiers,&actor.report_keys[0])) return false;
    actor.text_cursor=text; actor.text_end=close; actor.hmin=json_u32(data,size,"hmin",80u); actor.hmax=json_u32(data,size,"hmax",220u);
    actor.wmin=json_u32(data,size,"wmin",0u); actor.wmax=json_u32(data,size,"wmax",0u); actor.word_chance=(uint16_t)json_u32(data,size,"wordPauseChance",60u);
    actor.pmin=json_u32(data,size,"pmin",0u); actor.pmax=json_u32(data,size,"pmax",0u); actor.think_chance=(uint16_t)json_u32(data,size,"thinkChance",0u);
    actor.think_min=json_u32(data,size,"thinkMin",800u); actor.think_max=json_u32(data,size,"thinkMax",2200u);
    actor.typo_min=json_u32(data,size,"typoEveryMin",0u); actor.typo_max=json_u32(data,size,"typoEveryMax",0u);
    if (actor.hmax<actor.hmin) { uint32_t t=actor.hmin; actor.hmin=actor.hmax; actor.hmax=t; }
    if (actor.wmax<actor.wmin) { uint32_t t=actor.wmin; actor.wmin=actor.wmax; actor.wmax=t; }
    if (actor.pmax<actor.pmin) { uint32_t t=actor.pmin; actor.pmin=actor.pmax; actor.pmax=t; }
    if (actor.think_max<actor.think_min) { uint32_t t=actor.think_min; actor.think_min=actor.think_max; actor.think_max=t; }
    if (actor.typo_max<actor.typo_min) { uint32_t t=actor.typo_min; actor.typo_min=actor.typo_max; actor.typo_max=t; }
    if (actor.word_chance > 100u) actor.word_chance = 100u;
    if (actor.think_chance > 100u) actor.think_chance = 100u;
    actor.rng=now ^ ((uint32_t)constant_id<<16) ^ 0xa5c31f27u; actor.eligible_since_typo=0; actor.typo_due=actor.typo_max ? random_range(actor.typo_min ? actor.typo_min : 1u,actor.typo_max) : 0u;
    actor.type_pending_count=actor.type_pending_index=0; return true;
}

static bool prepare_type_key(void) {
    if (actor.type_pending_index >= actor.type_pending_count) {
        if (actor.text_cursor >= actor.text_end) return false;
        uint8_t actual;
        if (!next_json_char(&actor.text_cursor,actor.text_end,&actual)) return false;
        actor.type_actual=actual; actor.type_pending_index=0; actor.type_pending_count=1; actor.type_pending[0]=actual;
        bool eligible=(actual>='a'&&actual<='z')||(actual>='A'&&actual<='Z')||(actual>='0'&&actual<='9');
        if (eligible && actor.typo_due && ++actor.eligible_since_typo>=actor.typo_due) {
            actor.type_pending[0]=(actual=='x'||actual=='X')?'z':'x'; actor.type_pending[1]='\b'; actor.type_pending[2]=actual; actor.type_pending_count=3;
            actor.eligible_since_typo=0; actor.typo_due=random_range(actor.typo_min?actor.typo_min:1u,actor.typo_max);
        }
    }
    uint8_t value=actor.type_pending[actor.type_pending_index++], modifier, key;
    if (!ascii_to_hid(value,&modifier,&key)) return false;
    actor.report_modifiers=(uint8_t)(actor.persistent_modifiers|modifier); memcpy(actor.report_keys,actor.persistent_keys,sizeof(actor.report_keys));
    return add_key(actor.report_keys,key);
}

static uint32_t type_gap(void) {
    uint32_t gap=random_range(actor.hmin,actor.hmax); uint8_t value=actor.type_actual;
    if (actor.type_pending_index < actor.type_pending_count) return gap;
    if (value==' ' && actor.wmax && chance(actor.word_chance)) gap += random_range(actor.wmin,actor.wmax);
    if ((value=='.'||value==','||value=='!'||value=='?'||value==';'||value==':') && actor.pmax) gap += random_range(actor.pmin,actor.pmax);
    if (value==' ' && actor.think_max && chance(actor.think_chance)) gap += random_range(actor.think_min,actor.think_max);
    return gap;
}

void hid_keyboard_init(void) { memset(&actor,0,sizeof(actor)); }

HidKeyboardSubmit hid_keyboard_submit(const AbvmVm *vm, const AbvmEvent *event, uint32_t now) {
    if (actor.phase != ACTOR_IDLE) return HID_KEYBOARD_BUSY;
    if (event->opcode == ABVM_OP_TYPE) {
        actor.lane=event->lane;
        if (!type_configure(vm,event->operand_a,now)) return HID_KEYBOARD_INVALID;
        actor.phase=ACTOR_TYPE_PREPARE; return HID_KEYBOARD_ACCEPTED;
    }
    if (event->opcode != ABVM_OP_KEY && event->opcode != ABVM_OP_KDOWN && event->opcode != ABVM_OP_KUP) return HID_KEYBOARD_UNSUPPORTED;
    actor.lane=event->lane;
    if (event->opcode == ABVM_OP_KDOWN || event->opcode == ABVM_OP_KUP) {
        if (!apply_vk((uint8_t)event->operand_a,event->opcode==ABVM_OP_KDOWN)) return HID_KEYBOARD_INVALID;
        actor.phase=ACTOR_SEND_PERSISTENT; return HID_KEYBOARD_ACCEPTED;
    }
    actor.report_modifiers=actor.persistent_modifiers; memcpy(actor.report_keys,actor.persistent_keys,sizeof(actor.report_keys));
    uint32_t packed=event->operand_b; unsigned width=0; while (width<4u&&((packed>>(width*8u))&0xffu)) ++width; if (!width) return HID_KEYBOARD_INVALID;
    for (unsigned i=0;i<width;++i) { uint8_t modifier,key,vk=(uint8_t)(packed>>(i*8u)); if (!vk_to_hid(vk,&modifier,&key)) return HID_KEYBOARD_INVALID; actor.report_modifiers|=modifier; if (!add_key(actor.report_keys,key)) return HID_KEYBOARD_INVALID; }
    uint32_t lo=event->operand_c,hi=event->operand_d<lo?lo:event->operand_d; actor.due=now+lo+(hi-lo)/2u; actor.phase=ACTOR_SEND_PRESS; return HID_KEYBOARD_ACCEPTED;
}

HidKeyboardSubmit hid_keyboard_submit_live(const char *command, uint32_t now) {
    if (!command) return HID_KEYBOARD_INVALID;
    if (actor.phase != ACTOR_IDLE) return HID_KEYBOARD_BUSY;
    actor.lane=LIVE_LANE;
    actor.live_reply_pending=false;
    if (!strncmp(command,"KDOWN|",6u)||!strncmp(command,"KUP|",4u)) {
        const char *value=command+(command[1]=='D'?6u:4u); char *end=NULL;
        unsigned long vk=strtoul(value,&end,10);
        if (!end||*end||vk>255u||
            !apply_vk((uint8_t)vk,command[1]=='D'))
            return HID_KEYBOARD_INVALID;
        snprintf(actor.live_ack,sizeof(actor.live_ack),"OK|%s",command[1]=='D'?"KDOWN":"KUP");
        actor.phase=ACTOR_SEND_PERSISTENT;
        return HID_KEYBOARD_ACCEPTED;
    }
    if (!strncmp(command,"KCOMBO|",7u)) {
        char spec[64]; size_t length=strlen(command+7u);
        if (!length||length>=sizeof(spec)) return HID_KEYBOARD_INVALID;
        memcpy(spec,command+7u,length+1u);
        char *hold=strchr(spec,','); uint32_t lo=30u,hi=90u;
        if(hold) {
            *hold++=0;char *comma=strchr(hold,',');char *end=NULL;
            if(!comma)return HID_KEYBOARD_INVALID;
            *comma++=0;
            lo=(uint32_t)strtoul(hold,&end,10);
            if(!end||*end)return HID_KEYBOARD_INVALID;
            hi=(uint32_t)strtoul(comma,&end,10);
            if(!end||*end)return HID_KEYBOARD_INVALID;
            if(hi<lo){uint32_t t=lo;lo=hi;hi=t;}
        }
        actor.report_modifiers=actor.persistent_modifiers;
        memcpy(actor.report_keys,actor.persistent_keys,sizeof(actor.report_keys));
        char *cursor=spec;
        while(*cursor) {
            char *plus=strchr(cursor,'+');if(plus)*plus=0;
            char *end=NULL;unsigned long vk=strtoul(cursor,&end,10);
            uint8_t modifier,key;
            if(!end||*end||vk>255u||!vk_to_hid((uint8_t)vk,&modifier,&key))
                return HID_KEYBOARD_INVALID;
            actor.report_modifiers|=modifier;
            if(!add_key(actor.report_keys,key))return HID_KEYBOARD_INVALID;
            if(!plus)break;
            cursor=plus+1;
        }
        snprintf(actor.live_ack,sizeof(actor.live_ack),"OK|KCOMBO");
        actor.due=now+lo+(hi-lo)/2u;actor.phase=ACTOR_SEND_PRESS;
        return HID_KEYBOARD_ACCEPTED;
    }
    if (!strncmp(command,"KTEXT|",6u)) {
        const char *p=command+6u;char *end=NULL;
        uint32_t lo=(uint32_t)strtoul(p,&end,10);
        if(!end||*end!=',')return HID_KEYBOARD_INVALID;
        p=end+1;uint32_t hi=(uint32_t)strtoul(p,&end,10);
        if(!end||*end!=',')return HID_KEYBOARD_INVALID;
        p=end+1;size_t length=strlen(p);
        if(length>=sizeof(actor.live_text))return HID_KEYBOARD_INVALID;
        memcpy(actor.live_text,p,length+1u);
        for(size_t i=0;i<length;++i) {
            uint8_t modifier,key;
            if((uint8_t)actor.live_text[i]<0x20u||(uint8_t)actor.live_text[i]>0x7eu||
               !ascii_to_hid((uint8_t)actor.live_text[i],&modifier,&key))
                return HID_KEYBOARD_INVALID;
        }
        if(hi<lo){uint32_t t=lo;lo=hi;hi=t;}
        actor.text_cursor=(const uint8_t *)actor.live_text;
        actor.text_end=actor.text_cursor+length;
        actor.hmin=lo;actor.hmax=hi;actor.wmin=actor.wmax=0u;
        actor.pmin=actor.pmax=0u;actor.think_min=actor.think_max=0u;
        actor.word_chance=actor.think_chance=0u;
        actor.typo_min=actor.typo_max=actor.typo_due=0u;
        actor.eligible_since_typo=0u;actor.type_pending_count=actor.type_pending_index=0u;
        actor.rng=now^0xa5c31f27u;
        snprintf(actor.live_ack,sizeof(actor.live_ack),"OK|KTEXT");
        actor.phase=ACTOR_TYPE_PREPARE;
        return HID_KEYBOARD_ACCEPTED;
    }
    return HID_KEYBOARD_UNSUPPORTED;
}

HidKeyboardSubmit hid_keyboard_submit_trigger(uint8_t vk, uint32_t hold_min,
                                               uint32_t hold_max, uint32_t now) {
    if(actor.phase!=ACTOR_IDLE)return HID_KEYBOARD_BUSY;
    actor.report_modifiers=actor.persistent_modifiers;
    memcpy(actor.report_keys,actor.persistent_keys,sizeof(actor.report_keys));
    uint8_t modifier,key;
    if(!vk_to_hid(vk,&modifier,&key))return HID_KEYBOARD_INVALID;
    actor.report_modifiers|=modifier;
    if(!add_key(actor.report_keys,key))return HID_KEYBOARD_INVALID;
    if(hold_max<hold_min){uint32_t t=hold_min;hold_min=hold_max;hold_max=t;}
    actor.lane=INTERNAL_LANE;
    actor.due=now+hold_min+(hold_max-hold_min)/2u;
    actor.phase=ACTOR_SEND_PRESS;
    return HID_KEYBOARD_ACCEPTED;
}

static bool complete_action(uint8_t *completed_lane) {
    if(actor.lane==INTERNAL_LANE)return false;
    if(actor.lane==LIVE_LANE) {
        actor.live_reply_pending=true;
        return false;
    }
    *completed_lane=actor.lane;
    return true;
}

bool hid_keyboard_service(uint32_t now, uint8_t *completed_lane) {
    if (actor.release_pending) { uint8_t empty[MAX_KEYS]={0}; if (send_report(0,empty)) actor.release_pending=false; }
    if (!actor.release_pending&&actor.completion_pending) { *completed_lane=actor.completion_lane; actor.completion_pending=false; return true; }
    switch (actor.phase) {
        case ACTOR_SEND_PRESS: if (send_report(actor.report_modifiers,actor.report_keys)) actor.phase=ACTOR_WAIT_HOLD; break;
        case ACTOR_WAIT_HOLD: if (deadline_reached(now,actor.due)) actor.phase=ACTOR_SEND_RESTORE; break;
        case ACTOR_SEND_RESTORE: if (send_report(actor.persistent_modifiers,actor.persistent_keys)) { actor.phase=ACTOR_IDLE; return complete_action(completed_lane); } break;
        case ACTOR_SEND_PERSISTENT: if (send_report(actor.persistent_modifiers,actor.persistent_keys)) { actor.phase=ACTOR_IDLE; return complete_action(completed_lane); } break;
        case ACTOR_TYPE_PREPARE:
            if (!prepare_type_key()) { actor.phase=ACTOR_IDLE; return complete_action(completed_lane); }
            actor.phase=ACTOR_TYPE_SEND_PRESS; break;
        case ACTOR_TYPE_SEND_PRESS: if (send_report(actor.report_modifiers,actor.report_keys)) { actor.due=now+TYPE_HOLD_MS; actor.phase=ACTOR_TYPE_WAIT_HOLD; } break;
        case ACTOR_TYPE_WAIT_HOLD: if (deadline_reached(now,actor.due)) actor.phase=ACTOR_TYPE_SEND_RELEASE; break;
        case ACTOR_TYPE_SEND_RELEASE: if (send_report(actor.persistent_modifiers,actor.persistent_keys)) { actor.due=now+type_gap(); actor.phase=ACTOR_TYPE_WAIT_GAP; } break;
        case ACTOR_TYPE_WAIT_GAP: if (deadline_reached(now,actor.due)) actor.phase=ACTOR_TYPE_PREPARE; break;
        default: break;
    }
    return false;
}

bool hid_keyboard_take_live_reply(char *reply, size_t capacity) {
    if(!actor.live_reply_pending||!reply||!capacity)return false;
    snprintf(reply,capacity,"%s",actor.live_ack);
    actor.live_reply_pending=false;
    return true;
}

void hid_keyboard_release_all(void) {
    if (actor.phase!=ACTOR_IDLE && actor.lane!=LIVE_LANE) {
        actor.completion_pending=true; actor.completion_lane=actor.lane;
    }
    memset(actor.persistent_keys,0,sizeof(actor.persistent_keys)); memset(actor.report_keys,0,sizeof(actor.report_keys)); actor.persistent_modifiers=0; actor.report_modifiers=0; actor.phase=ACTOR_IDLE; actor.release_pending=true;
}
void hid_keyboard_discard_completion(void){actor.completion_pending=false;}

bool hid_keyboard_busy(void) { return actor.phase!=ACTOR_IDLE; }
bool hid_keyboard_locked(void) {
    if(actor.phase!=ACTOR_IDLE||actor.persistent_modifiers)return true;
    for(uint8_t i=0;i<MAX_KEYS;++i)
        if(actor.persistent_keys[i])return true;
    return false;
}
