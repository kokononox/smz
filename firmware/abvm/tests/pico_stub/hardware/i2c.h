#ifndef TEST_HARDWARE_I2C_H
#define TEST_HARDWARE_I2C_H
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#ifndef PICO_UINT_DEFINED
#define PICO_UINT_DEFINED
typedef unsigned int uint;
#endif
typedef struct i2c_inst { int unused; } i2c_inst_t;
extern i2c_inst_t test_i2c0;
#define i2c0 (&test_i2c0)
uint i2c_init(i2c_inst_t *i2c, uint baudrate);
int i2c_write_timeout_us(i2c_inst_t *i2c, uint8_t addr,
                         const uint8_t *src, size_t len, bool nostop,
                         uint timeout_us);
int i2c_read_timeout_us(i2c_inst_t *i2c, uint8_t addr, uint8_t *dst,
                        size_t len, bool nostop, uint timeout_us);
#endif
