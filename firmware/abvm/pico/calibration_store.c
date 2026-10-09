#include "calibration_store.h"
#include <stddef.h>
#include <string.h>
#include "hardware/flash.h"
#include "hardware/sync.h"
#include "pico/stdlib.h"

#define CAL_MAGIC 0x314c4143u
#define CAL_VERSION 6u
#define LIGHT_PROFILE_COUNT 9u
#define SOUND_PROFILE_COUNT 3u
#define CAL_SLOT_SIZE FLASH_SECTOR_SIZE
#define CAL_AREA_SIZE (2u * CAL_SLOT_SIZE)
#define CAL_OFFSET_A (PICO_FLASH_SIZE_BYTES - CAL_AREA_SIZE)
#define CAL_OFFSET_B (PICO_FLASH_SIZE_BYTES - CAL_SLOT_SIZE)

typedef struct LegacyCalibrationPayloadV4 {
    uint8_t binding[32];
    uint16_t light_mask;
    uint8_t sound_mask;
    uint8_t cycle_armed;
    uint8_t cycle_count;
    uint32_t light_low[LIGHT_PROFILE_COUNT];
    uint32_t light_high[LIGHT_PROFILE_COUNT];
    uint16_t sound_threshold[SOUND_PROFILE_COUNT];
    uint16_t sound_minimum[SOUND_PROFILE_COUNT];
    uint16_t sound_silence[SOUND_PROFILE_COUNT];
    uint16_t sound_peak[SOUND_PROFILE_COUNT];
} LegacyCalibrationPayloadV4;
typedef struct CalibrationPayload {
    uint8_t binding[32];
    uint16_t light_mask;
    uint8_t sound_mask,cycle_armed,cycle_count;
    uint32_t light_low[LIGHT_PROFILE_COUNT],light_high[LIGHT_PROFILE_COUNT];
    uint16_t sound_threshold[SOUND_PROFILE_COUNT],sound_minimum[SOUND_PROFILE_COUNT];
    uint16_t sound_silence[SOUND_PROFILE_COUNT],sound_peak[SOUND_PROFILE_COUNT];
    uint8_t shift_attempts,shift_target;
    /* bit0 host asleep at last observation, bit1 a wake was still owed */
    uint8_t wake_flags;
    uint8_t wake_recovery;
    uint16_t wake_next_start;
} CalibrationPayload;
typedef struct LegacyCalibrationPayloadV5 {
    uint8_t binding[32];
    uint16_t light_mask;
    uint8_t sound_mask,cycle_armed,cycle_count;
    uint32_t light_low[LIGHT_PROFILE_COUNT],light_high[LIGHT_PROFILE_COUNT];
    uint16_t sound_threshold[SOUND_PROFILE_COUNT],sound_minimum[SOUND_PROFILE_COUNT];
    uint16_t sound_silence[SOUND_PROFILE_COUNT],sound_peak[SOUND_PROFILE_COUNT];
    uint8_t shift_attempts,shift_target;
} LegacyCalibrationPayloadV5;
typedef struct LegacyCalibrationRecordV5 {
    uint32_t magic;uint16_t version,size;uint32_t sequence,crc32;
    LegacyCalibrationPayloadV5 payload;
} LegacyCalibrationRecordV5;
typedef struct LegacyCalibrationRecordV4 {
    uint32_t magic;uint16_t version,size;uint32_t sequence,crc32;
    LegacyCalibrationPayloadV4 payload;
} LegacyCalibrationRecordV4;

typedef struct CalibrationRecord {
    uint32_t magic;
    uint16_t version;
    uint16_t size;
    uint32_t sequence;
    uint32_t crc32;
    CalibrationPayload payload;
} CalibrationRecord;

typedef struct LegacyCalibrationPayloadV2 {
    uint8_t binding[32];
    uint8_t light_mask,sound_mask,cycle_armed,cycle_count;
    uint32_t light_low[7],light_high[7];
    uint16_t sound_threshold[2],sound_minimum[2],sound_silence[2],sound_peak[2];
} LegacyCalibrationPayloadV2;
typedef struct LegacyCalibrationRecordV2 {
    uint32_t magic;
    uint16_t version,size;
    uint32_t sequence,crc32;
    LegacyCalibrationPayloadV2 payload;
} LegacyCalibrationRecordV2;
typedef struct LegacyCalibrationPayloadV3 {
    uint8_t binding[32];
    uint8_t light_mask,sound_mask,cycle_armed,cycle_count;
    uint32_t light_low[8],light_high[8];
    uint16_t sound_threshold[3],sound_minimum[3],sound_silence[3],sound_peak[3];
} LegacyCalibrationPayloadV3;
typedef struct LegacyCalibrationRecordV3 {
    uint32_t magic;
    uint16_t version,size;
    uint32_t sequence,crc32;
    LegacyCalibrationPayloadV3 payload;
} LegacyCalibrationRecordV3;

_Static_assert(sizeof(CalibrationRecord) <= FLASH_PAGE_SIZE, "calibration record page");
static CalibrationRecord current;
static uint32_t active_offset;

static uint32_t crc32_bytes(const uint8_t *data, size_t size) {
    uint32_t crc=0xffffffffu;
    while(size--){crc^=*data++;for(uint8_t i=0;i<8u;++i)crc=(crc>>1)^(0xedb88320u & (uint32_t)-(int32_t)(crc&1u));}
    return crc^0xffffffffu;
}
static uint32_t record_crc(const CalibrationRecord *record) {
    uint8_t bytes[sizeof(record->sequence)+sizeof(record->payload)];
    memcpy(bytes,&record->sequence,sizeof(record->sequence));
    memcpy(bytes+sizeof(record->sequence),&record->payload,sizeof(record->payload));
    return crc32_bytes(bytes,sizeof(bytes));
}
static uint32_t legacy_record_crc(const LegacyCalibrationRecordV2 *record) {
    uint8_t bytes[sizeof(record->sequence)+sizeof(record->payload)];
    memcpy(bytes,&record->sequence,sizeof(record->sequence));
    memcpy(bytes+sizeof(record->sequence),&record->payload,sizeof(record->payload));
    return crc32_bytes(bytes,sizeof(bytes));
}
static uint32_t legacy_v3_record_crc(const LegacyCalibrationRecordV3 *record) {
    uint8_t bytes[sizeof(record->sequence)+sizeof(record->payload)];
    memcpy(bytes,&record->sequence,sizeof(record->sequence));
    memcpy(bytes+sizeof(record->sequence),&record->payload,sizeof(record->payload));
    return crc32_bytes(bytes,sizeof(bytes));
}
static bool legacy_v4_record_valid(const LegacyCalibrationRecordV4 *r,const uint8_t binding[32]) {
    if(r->magic!=CAL_MAGIC||r->version!=4u||r->size!=sizeof(r->payload)||memcmp(r->payload.binding,binding,32u))return false;
    uint8_t bytes[sizeof(r->sequence)+sizeof(r->payload)];
    memcpy(bytes,&r->sequence,sizeof(r->sequence));memcpy(bytes+sizeof(r->sequence),&r->payload,sizeof(r->payload));
    return r->crc32==crc32_bytes(bytes,sizeof(bytes));
}
static bool legacy_v5_record_valid(const LegacyCalibrationRecordV5 *r,const uint8_t binding[32]) {
    if(r->magic!=CAL_MAGIC||r->version!=5u||r->size!=sizeof(r->payload)||memcmp(r->payload.binding,binding,32u))return false;
    uint8_t bytes[sizeof(r->sequence)+sizeof(r->payload)];
    memcpy(bytes,&r->sequence,sizeof(r->sequence));memcpy(bytes+sizeof(r->sequence),&r->payload,sizeof(r->payload));
    return r->crc32==crc32_bytes(bytes,sizeof(bytes));
}
static bool record_valid(const CalibrationRecord *record, const uint8_t binding[32]) {
    if(record->magic!=CAL_MAGIC||record->version!=CAL_VERSION||
       record->size!=sizeof(CalibrationPayload)||memcmp(record->payload.binding,binding,32u))return false;
    return record->crc32==record_crc(record);
}
static bool legacy_record_valid(const LegacyCalibrationRecordV2 *record,
                                const uint8_t binding[32]) {
    if(record->magic!=CAL_MAGIC||record->version!=2u||
       record->size!=sizeof(LegacyCalibrationPayloadV2)||
       memcmp(record->payload.binding,binding,32u))return false;
    return record->crc32==legacy_record_crc(record);
}
static bool legacy_v3_record_valid(const LegacyCalibrationRecordV3 *record,
                                   const uint8_t binding[32]) {
    if(record->magic!=CAL_MAGIC||record->version!=3u||
       record->size!=sizeof(LegacyCalibrationPayloadV3)||
       memcmp(record->payload.binding,binding,32u))return false;
    return record->crc32==legacy_v3_record_crc(record);
}
static const CalibrationRecord *flash_record(uint32_t offset) {
    return (const CalibrationRecord *)(uintptr_t)(XIP_BASE + offset);
}
void calibration_store_init(const AbvmVm *vm) {
    uint8_t binding[32]={0};
    if(vm)memcpy(binding,vm->header.program_sha256,sizeof(binding));
    const CalibrationRecord *a=flash_record(CAL_OFFSET_A),*b=flash_record(CAL_OFFSET_B);
    bool av=record_valid(a,binding),bv=record_valid(b,binding);
    memset(&current,0,sizeof(current));
    if(av&&(!bv||(int32_t)(a->sequence-b->sequence)>0)){memcpy(&current,a,sizeof(current));active_offset=CAL_OFFSET_A;}
    else if(bv){memcpy(&current,b,sizeof(current));active_offset=CAL_OFFSET_B;}
    else {
        const LegacyCalibrationRecordV5 *v5a=(const LegacyCalibrationRecordV5 *)a,*v5b=(const LegacyCalibrationRecordV5 *)b;
        bool v5av=legacy_v5_record_valid(v5a,binding),v5bv=legacy_v5_record_valid(v5b,binding);
        const LegacyCalibrationRecordV5 *v5=NULL;
        if(v5av&&(!v5bv||(int32_t)(v5a->sequence-v5b->sequence)>0)){v5=v5a;active_offset=CAL_OFFSET_A;}
        else if(v5bv){v5=v5b;active_offset=CAL_OFFSET_B;}
        if(v5){
            current.magic=CAL_MAGIC;current.version=CAL_VERSION;current.size=sizeof(CalibrationPayload);
            current.sequence=v5->sequence;
            /* Wake fields follow v5's trailing padding; copy named common bytes
             * only, so the migrated record starts with no wake history. */
            memcpy(&current.payload,&v5->payload,offsetof(CalibrationPayload,wake_flags));
            return;
        }
        const LegacyCalibrationRecordV4 *v4a=(const LegacyCalibrationRecordV4 *)a,*v4b=(const LegacyCalibrationRecordV4 *)b;
        bool v4av=legacy_v4_record_valid(v4a,binding),v4bv=legacy_v4_record_valid(v4b,binding);
        const LegacyCalibrationRecordV4 *v4=NULL;
        if(v4av&&(!v4bv||(int32_t)(v4a->sequence-v4b->sequence)>0)){v4=v4a;active_offset=CAL_OFFSET_A;}
        else if(v4bv){v4=v4b;active_offset=CAL_OFFSET_B;}
        if(v4){
            current.magic=CAL_MAGIC;current.version=CAL_VERSION;current.size=sizeof(CalibrationPayload);
            current.sequence=v4->sequence;
            /* New fields follow v4's trailing padding; copy named common bytes only. */
            memcpy(&current.payload,&v4->payload,offsetof(CalibrationPayload,shift_attempts));
            current.payload.shift_attempts=0u;current.payload.shift_target=0u;return;
        }
        const LegacyCalibrationRecordV3 *v3a=(const LegacyCalibrationRecordV3 *)a;
        const LegacyCalibrationRecordV3 *v3b=(const LegacyCalibrationRecordV3 *)b;
        bool v3av=legacy_v3_record_valid(v3a,binding);
        bool v3bv=legacy_v3_record_valid(v3b,binding);
        const LegacyCalibrationRecordV3 *legacy_v3=NULL;
        if(v3av&&(!v3bv||(int32_t)(v3a->sequence-v3b->sequence)>0)){
            legacy_v3=v3a;active_offset=CAL_OFFSET_A;
        } else if(v3bv){legacy_v3=v3b;active_offset=CAL_OFFSET_B;}
        const LegacyCalibrationRecordV2 *la=(const LegacyCalibrationRecordV2 *)a;
        const LegacyCalibrationRecordV2 *lb=(const LegacyCalibrationRecordV2 *)b;
        bool lav=!legacy_v3&&legacy_record_valid(la,binding);
        bool lbv=!legacy_v3&&legacy_record_valid(lb,binding);
        const LegacyCalibrationRecordV2 *legacy=NULL;
        if(lav&&(!lbv||(int32_t)(la->sequence-lb->sequence)>0)){legacy=la;active_offset=CAL_OFFSET_A;}
        else if(lbv){legacy=lb;active_offset=CAL_OFFSET_B;}
        else active_offset=CAL_OFFSET_B;
        current.magic=CAL_MAGIC;current.version=CAL_VERSION;
        current.size=sizeof(CalibrationPayload);memcpy(current.payload.binding,binding,32u);
        if(legacy_v3){
            current.sequence=legacy_v3->sequence;
            current.payload.light_mask=legacy_v3->payload.light_mask;
            current.payload.sound_mask=legacy_v3->payload.sound_mask;
            current.payload.cycle_armed=legacy_v3->payload.cycle_armed;
            current.payload.cycle_count=legacy_v3->payload.cycle_count;
            memcpy(current.payload.light_low,legacy_v3->payload.light_low,
                   sizeof(legacy_v3->payload.light_low));
            memcpy(current.payload.light_high,legacy_v3->payload.light_high,
                   sizeof(legacy_v3->payload.light_high));
            memcpy(current.payload.sound_threshold,legacy_v3->payload.sound_threshold,
                   sizeof(legacy_v3->payload.sound_threshold));
            memcpy(current.payload.sound_minimum,legacy_v3->payload.sound_minimum,
                   sizeof(legacy_v3->payload.sound_minimum));
            memcpy(current.payload.sound_silence,legacy_v3->payload.sound_silence,
                   sizeof(legacy_v3->payload.sound_silence));
            memcpy(current.payload.sound_peak,legacy_v3->payload.sound_peak,
                   sizeof(legacy_v3->payload.sound_peak));
        } else if(legacy){
            current.sequence=legacy->sequence;
            current.payload.light_mask=legacy->payload.light_mask;
            current.payload.sound_mask=legacy->payload.sound_mask;
            current.payload.cycle_armed=legacy->payload.cycle_armed;
            current.payload.cycle_count=legacy->payload.cycle_count;
            memcpy(current.payload.light_low,legacy->payload.light_low,
                   sizeof(legacy->payload.light_low));
            memcpy(current.payload.light_high,legacy->payload.light_high,
                   sizeof(legacy->payload.light_high));
            memcpy(current.payload.sound_threshold,legacy->payload.sound_threshold,
                   sizeof(legacy->payload.sound_threshold));
            memcpy(current.payload.sound_minimum,legacy->payload.sound_minimum,
                   sizeof(legacy->payload.sound_minimum));
            memcpy(current.payload.sound_silence,legacy->payload.sound_silence,
                   sizeof(legacy->payload.sound_silence));
            memcpy(current.payload.sound_peak,legacy->payload.sound_peak,
                   sizeof(legacy->payload.sound_peak));
        }
    }
}
static bool persist(void) {
    uint32_t target=active_offset==CAL_OFFSET_A?CAL_OFFSET_B:CAL_OFFSET_A;
    uint8_t page[FLASH_PAGE_SIZE];
    ++current.sequence;
    current.crc32=record_crc(&current);
    memset(page,0xff,sizeof(page));memcpy(page,&current,sizeof(current));
    uint32_t irq=save_and_disable_interrupts();
    flash_range_erase(target,CAL_SLOT_SIZE);
    flash_range_program(target,page,sizeof(page));
    restore_interrupts(irq);
    const CalibrationRecord *written=flash_record(target);
    if(!record_valid(written,current.payload.binding)||written->sequence!=current.sequence)return false;
    active_offset=target;return true;
}
bool calibration_store_light_get(uint8_t id,uint32_t *low,uint32_t *high){
    if(id<1u||id>LIGHT_PROFILE_COUNT||!(current.payload.light_mask&(1u<<(id-1u)))||!low||!high)return false;
    *low=current.payload.light_low[id-1u];*high=current.payload.light_high[id-1u];return true;
}
bool calibration_store_light_set(uint8_t id,uint32_t low,uint32_t high){
    if(id<1u||id>LIGHT_PROFILE_COUNT||low>high||high>1000000u)return false;
    uint32_t lows[LIGHT_PROFILE_COUNT],highs[LIGHT_PROFILE_COUNT];
    memcpy(lows,current.payload.light_low,sizeof(lows));
    memcpy(highs,current.payload.light_high,sizeof(highs));
    lows[id-1u]=low;highs[id-1u]=high;
    return calibration_store_light_update((uint16_t)(1u<<(id-1u)),lows,highs);
}
bool calibration_store_light_update(uint16_t update_mask,const uint32_t lows[9],
                                    const uint32_t highs[9]){
    if(!update_mask||!lows||!highs)return false;
    for(uint8_t i=0;i<LIGHT_PROFILE_COUNT;++i)
        if((update_mask&(1u<<i))&&(lows[i]>highs[i]||highs[i]>1000000u))
            return false;
    CalibrationRecord before=current;
    uint32_t before_offset=active_offset;
    for(uint8_t i=0;i<LIGHT_PROFILE_COUNT;++i)if(update_mask&(1u<<i)){
        current.payload.light_low[i]=lows[i];
        current.payload.light_high[i]=highs[i];
    }
    current.payload.light_mask|=update_mask;
    if(persist())return true;
    current=before;active_offset=before_offset;return false;
}
bool calibration_store_sound_get(uint16_t id,uint16_t *threshold,uint16_t *minimum){
    if(id<1u||id>SOUND_PROFILE_COUNT||!(current.payload.sound_mask&(1u<<(id-1u)))||!threshold||!minimum)return false;
    *threshold=current.payload.sound_threshold[id-1u];*minimum=current.payload.sound_minimum[id-1u];return true;
}
bool calibration_store_sound_set(uint16_t id,uint16_t threshold,uint16_t minimum,uint16_t silence,uint16_t peak){
    if(id<1u||id>SOUND_PROFILE_COUNT||!threshold||threshold>511u||!minimum||peak<=silence)return false;
    uint8_t i=(uint8_t)(id-1u);current.payload.sound_threshold[i]=threshold;current.payload.sound_minimum[i]=minimum;
    current.payload.sound_silence[i]=silence;current.payload.sound_peak[i]=peak;
    current.payload.sound_mask|=(uint8_t)(1u<<i);return persist();
}
bool calibration_store_cycle_armed(void){return current.payload.cycle_armed==0xa5u;}
uint8_t calibration_store_cycle_count(void){return current.payload.cycle_count;}
bool calibration_store_cycle_arm_next(uint8_t maximum){
    if(!maximum||current.payload.cycle_count>=maximum){
        if(!current.payload.cycle_armed)return false;
        current.payload.cycle_armed=0u;
        (void)persist();
        return false;
    }
    current.payload.cycle_armed=0xa5u;
    ++current.payload.cycle_count;
    return persist();
}
bool calibration_store_cycle_clear_armed(void){
    if(!current.payload.cycle_armed)return true;
    current.payload.cycle_armed=0u;
    return persist();
}
bool calibration_store_cycle_reset(void){
    if(!current.payload.cycle_armed&&!current.payload.cycle_count)return true;
    current.payload.cycle_armed=0u;
    current.payload.cycle_count=0u;
    return persist();
}
uint32_t calibration_store_revision(void){return current.sequence;}

uint8_t calibration_store_shift_attempts(void){return current.payload.shift_attempts;}
uint8_t calibration_store_shift_target(void){return current.payload.shift_target;}
static bool persist_transaction(CalibrationRecord before,uint32_t offset){
    if(persist())return true;
    current=before;active_offset=offset;return false;
}
bool calibration_store_shift_begin(uint8_t target,uint8_t maximum){
    if((target!=1u&&target!=2u)||!maximum||maximum>10u||current.payload.shift_attempts>=maximum)return false;
    CalibrationRecord before=current;uint32_t offset=active_offset;
    ++current.payload.shift_attempts;current.payload.shift_target=target;current.payload.cycle_armed=0xa5u;
    return persist_transaction(before,offset);
}
bool calibration_store_shift_complete(void){
    if(!current.payload.shift_target)return true;
    CalibrationRecord before=current;uint32_t offset=active_offset;
    current.payload.shift_target=0u;current.payload.shift_attempts=0u;
    current.payload.cycle_count=0u;current.payload.cycle_armed=0u;
    return persist_transaction(before,offset);
}
bool calibration_store_shift_clear(void){
    if(!current.payload.shift_target&&!current.payload.shift_attempts)return true;
    CalibrationRecord before=current;uint32_t offset=active_offset;
    current.payload.shift_target=0u;current.payload.shift_attempts=0u;
    return persist_transaction(before,offset);
}

static uint8_t wake_flags_of(bool host_asleep,bool pending){
    uint8_t flags=0u;
    if(host_asleep)flags|=1u;
    if(pending)flags|=2u;
    return flags;
}
bool calibration_store_wake_get(WakeStoreState *state){
    if(!state)return false;
    state->host_asleep=(current.payload.wake_flags&1u)!=0u;
    state->pending=(current.payload.wake_flags&2u)!=0u;
    state->recovery_attempts=current.payload.wake_recovery;
    state->next_start=current.payload.wake_next_start;
    return true;
}
/* Called from the main loop, so it is deliberately write-free when nothing the
 * recovery decision depends on has changed: a sector erase per loop iteration
 * would wear the slot out in days. */
bool calibration_store_wake_set(const WakeStoreState *state){
    if(!state)return false;
    uint8_t flags=wake_flags_of(state->host_asleep,state->pending);
    if(current.payload.wake_flags==flags&&
       current.payload.wake_recovery==state->recovery_attempts&&
       current.payload.wake_next_start==state->next_start)return true;
    CalibrationRecord before=current;uint32_t offset=active_offset;
    current.payload.wake_flags=flags;
    current.payload.wake_recovery=state->recovery_attempts;
    current.payload.wake_next_start=state->next_start;
    return persist_transaction(before,offset);
}
bool calibration_store_wake_recovery_reset(void){
    if(!current.payload.wake_recovery)return true;
    CalibrationRecord before=current;uint32_t offset=active_offset;
    current.payload.wake_recovery=0u;
    return persist_transaction(before,offset);
}
