#include "calibration_store.h"
#include <stddef.h>
#include <string.h>
#include "hardware/flash.h"
#include "hardware/sync.h"
#include "pico/stdlib.h"

#define CAL_MAGIC 0x314c4143u
#define CAL_VERSION 3u
#define LIGHT_PROFILE_COUNT 8u
#define SOUND_PROFILE_COUNT 3u
#define CAL_SLOT_SIZE FLASH_SECTOR_SIZE
#define CAL_AREA_SIZE (2u * CAL_SLOT_SIZE)
#define CAL_OFFSET_A (PICO_FLASH_SIZE_BYTES - CAL_AREA_SIZE)
#define CAL_OFFSET_B (PICO_FLASH_SIZE_BYTES - CAL_SLOT_SIZE)

typedef struct CalibrationPayload {
    uint8_t binding[32];
    uint8_t light_mask;
    uint8_t sound_mask;
    uint8_t cycle_armed;
    uint8_t cycle_count;
    uint32_t light_low[LIGHT_PROFILE_COUNT];
    uint32_t light_high[LIGHT_PROFILE_COUNT];
    uint16_t sound_threshold[SOUND_PROFILE_COUNT];
    uint16_t sound_minimum[SOUND_PROFILE_COUNT];
    uint16_t sound_silence[SOUND_PROFILE_COUNT];
    uint16_t sound_peak[SOUND_PROFILE_COUNT];
} CalibrationPayload;

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
        const LegacyCalibrationRecordV2 *la=(const LegacyCalibrationRecordV2 *)a;
        const LegacyCalibrationRecordV2 *lb=(const LegacyCalibrationRecordV2 *)b;
        bool lav=legacy_record_valid(la,binding),lbv=legacy_record_valid(lb,binding);
        const LegacyCalibrationRecordV2 *legacy=NULL;
        if(lav&&(!lbv||(int32_t)(la->sequence-lb->sequence)>0)){legacy=la;active_offset=CAL_OFFSET_A;}
        else if(lbv){legacy=lb;active_offset=CAL_OFFSET_B;}
        else active_offset=CAL_OFFSET_B;
        current.magic=CAL_MAGIC;current.version=CAL_VERSION;
        current.size=sizeof(CalibrationPayload);memcpy(current.payload.binding,binding,32u);
        if(legacy){
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
    return calibration_store_light_update((uint8_t)(1u<<(id-1u)),lows,highs);
}
bool calibration_store_light_update(uint8_t update_mask,const uint32_t lows[8],
                                    const uint32_t highs[8]){
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
