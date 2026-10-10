#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "calibration_store.c"
uint8_t *test_flash;
static bool corrupt_write;
uint32_t save_and_disable_interrupts(void){return 0;}
void restore_interrupts(uint32_t n){(void)n;}
void flash_range_erase(uint32_t offset,size_t size){memset(test_flash+offset,255,size);}
void flash_range_program(uint32_t offset,const uint8_t *data,size_t size){
 memcpy(test_flash+offset,data,size);if(corrupt_write)test_flash[offset]^=1;
}
static void load_v4(AbvmVm *vm){
 LegacyCalibrationRecordV4 old={0};old.magic=CAL_MAGIC;old.version=4;old.size=sizeof(old.payload);old.sequence=20;
 memcpy(old.payload.binding,vm->header.program_sha256,32);old.payload.light_mask=1;
 old.payload.light_low[0]=91;old.payload.light_high[0]=109;old.payload.cycle_count=3;
 uint8_t bytes[sizeof(old.sequence)+sizeof(old.payload)];memcpy(bytes,&old.sequence,sizeof(old.sequence));
 memcpy(bytes+sizeof(old.sequence),&old.payload,sizeof(old.payload));old.crc32=crc32_bytes(bytes,sizeof(bytes));
 memcpy(test_flash+CAL_OFFSET_A,&old,sizeof(old));
}
static void load_v6(AbvmVm *vm){
 LegacyCalibrationRecordV6 old={0};old.magic=CAL_MAGIC;old.version=6;old.size=sizeof(old.payload);old.sequence=30;
 memcpy(old.payload.binding,vm->header.program_sha256,32);
 old.payload.light_mask=1;old.payload.light_low[0]=7;old.payload.light_high[0]=9;
 old.payload.shift_target=1;old.payload.shift_attempts=2;
 old.payload.wake_flags=3;old.payload.wake_recovery=1;old.payload.wake_next_start=1234;
 /* Whatever a v6 record happened to carry where the learned cold start now lives
  * is not a cold start.  The noise is written before the CRC so the record stays
  * valid: a migration that read the padding instead of ignoring it would load a
  * host boot time this machine never had. */
 uint8_t *raw=(uint8_t *)&old.payload;
 raw[sizeof(old.payload)-2]=0xa5;raw[sizeof(old.payload)-1]=0x5a;
 uint8_t bytes[sizeof(old.sequence)+sizeof(old.payload)];memcpy(bytes,&old.sequence,sizeof(old.sequence));
 memcpy(bytes+sizeof(old.sequence),&old.payload,sizeof(old.payload));old.crc32=crc32_bytes(bytes,sizeof(bytes));
 memcpy(test_flash+CAL_OFFSET_A,&old,sizeof(old));
}
int main(void){
 test_flash=malloc(PICO_FLASH_SIZE_BYTES);assert(test_flash);memset(test_flash,255,PICO_FLASH_SIZE_BYTES);
 AbvmVm vm={0};memset(vm.header.program_sha256,0x11,32);load_v4(&vm);calibration_store_init(&vm);
 uint32_t lo,hi;assert(calibration_store_light_get(1,&lo,&hi)&&lo==91&&hi==109);
 assert(calibration_store_cycle_count()==3&&calibration_store_shift_target()==0);
 assert(calibration_store_shift_begin(2,2));assert(calibration_store_cycle_count()==3);
 assert(calibration_store_shift_target()==2&&calibration_store_shift_attempts()==1&&calibration_store_cycle_armed());
 calibration_store_init(&vm);assert(calibration_store_shift_target()==2&&calibration_store_shift_attempts()==1);
 assert(calibration_store_shift_begin(2,2));assert(!calibration_store_shift_begin(2,2));
 assert(calibration_store_shift_attempts()==2&&calibration_store_cycle_count()==3);
 corrupt_write=true;assert(!calibration_store_shift_complete());
 assert(calibration_store_shift_attempts()==2&&calibration_store_cycle_count()==3);corrupt_write=false;
 calibration_store_init(&vm);assert(calibration_store_shift_attempts()==2); /* previous A/B record survived */
 assert(calibration_store_shift_complete());assert(!calibration_store_shift_target()&&!calibration_store_shift_attempts());
 assert(!calibration_store_cycle_count()&&!calibration_store_cycle_armed());
 assert(calibration_store_light_get(1,&lo,&hi)&&lo==91&&hi==109);
 assert(calibration_store_shift_begin(1,2));corrupt_write=true;
 assert(!calibration_store_shift_begin(2,2));assert(calibration_store_shift_attempts()==1&&calibration_store_shift_target()==1);
 corrupt_write=false;assert(calibration_store_shift_clear());
 vm.header.program_sha256[0]=0x22;calibration_store_init(&vm);assert(!calibration_store_shift_target());
 /* The learned cold start is the field v6 carried as padding.  A v6 record has to
  * migrate with that field empty, with every field the two versions share intact,
  * and a value learned afterwards has to survive a reload with the decision it
  * travelled with. */
 memset(test_flash,255,PICO_FLASH_SIZE_BYTES);load_v6(&vm);calibration_store_init(&vm);
 WakeStoreState wake={0};assert(calibration_store_wake_get(&wake));
 assert(wake.host_asleep&&wake.pending&&wake.recovery_attempts==1&&wake.next_start==1234);
 assert(wake.host_boot_s==0);
 assert(calibration_store_light_get(1,&lo,&hi)&&lo==7&&hi==9);
 assert(calibration_store_shift_target()==1&&calibration_store_shift_attempts()==2);
 wake.host_boot_s=31;assert(calibration_store_wake_set(&wake));
 calibration_store_init(&vm);
 WakeStoreState reloaded={0};assert(calibration_store_wake_get(&reloaded));
 assert(reloaded.host_boot_s==31&&reloaded.next_start==1234&&reloaded.recovery_attempts==1);
 free(test_flash);puts("actual NVM v6: v4/v6 migration, learned cold start, power loss, attempt limit, atomic successful reset and rollback passed");return 0;
}
