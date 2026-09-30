#include "calibration_store.h"
#include <stddef.h>
#include <string.h>
#include "hardware/flash.h"
#include "hardware/sync.h"
#include "pico/stdlib.h"

#define CAL_MAGIC 0x314c4143u
#define CAL_VERSION 1u
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
    uint32_t light_low[6];
    uint32_t light_high[6];
    uint16_t sound_threshold[2];
    uint16_t sound_minimum[2];
    uint16_t sound_silence[2];
    uint16_t sound_peak[2];
} CalibrationPayload;

typedef struct CalibrationRecord {
    uint32_t magic;
    uint16_t version;
    uint16_t size;
    uint32_t sequence;
    uint32_t crc32;
    CalibrationPayload payload;
} CalibrationRecord;

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
static bool record_valid(const CalibrationRecord *record, const uint8_t binding[32]) {
    if(record->magic!=CAL_MAGIC||record->version!=CAL_VERSION||
       record->size!=sizeof(CalibrationPayload)||memcmp(record->payload.binding,binding,32u))return false;
    return record->crc32==record_crc(record);
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
    else {current.magic=CAL_MAGIC;current.version=CAL_VERSION;current.size=sizeof(CalibrationPayload);memcpy(current.payload.binding,binding,32u);active_offset=CAL_OFFSET_B;}
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
    if(id<1u||id>6u||!(current.payload.light_mask&(1u<<(id-1u)))||!low||!high)return false;
    *low=current.payload.light_low[id-1u];*high=current.payload.light_high[id-1u];return true;
}
bool calibration_store_light_set(uint8_t id,uint32_t low,uint32_t high){
    if(id<1u||id>6u||low>high||high>1000000u)return false;
    uint32_t lows[6],highs[6];
    memcpy(lows,current.payload.light_low,sizeof(lows));
    memcpy(highs,current.payload.light_high,sizeof(highs));
    lows[id-1u]=low;highs[id-1u]=high;
    return calibration_store_light_update((uint8_t)(1u<<(id-1u)),lows,highs);
}
bool calibration_store_light_update(uint8_t update_mask,const uint32_t lows[6],
                                    const uint32_t highs[6]){
    if(!update_mask||!lows||!highs||(update_mask&0xc0u))return false;
    for(uint8_t i=0;i<6u;++i)
        if((update_mask&(1u<<i))&&(lows[i]>highs[i]||highs[i]>1000000u))
            return false;
    CalibrationRecord before=current;
    uint32_t before_offset=active_offset;
    for(uint8_t i=0;i<6u;++i)if(update_mask&(1u<<i)){
        current.payload.light_low[i]=lows[i];
        current.payload.light_high[i]=highs[i];
    }
    current.payload.light_mask|=update_mask;
    if(persist())return true;
    current=before;active_offset=before_offset;return false;
}
bool calibration_store_sound_get(uint16_t id,uint16_t *threshold,uint16_t *minimum){
    if(id<1u||id>2u||!(current.payload.sound_mask&(1u<<(id-1u)))||!threshold||!minimum)return false;
    *threshold=current.payload.sound_threshold[id-1u];*minimum=current.payload.sound_minimum[id-1u];return true;
}
bool calibration_store_sound_set(uint16_t id,uint16_t threshold,uint16_t minimum,uint16_t silence,uint16_t peak){
    if(id<1u||id>2u||!threshold||threshold>511u||!minimum||peak<=silence)return false;
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
