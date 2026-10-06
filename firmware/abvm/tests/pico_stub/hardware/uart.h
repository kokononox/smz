#ifndef TEST_HARDWARE_UART_H
#define TEST_HARDWARE_UART_H
#ifndef PICO_UINT_DEFINED
#define PICO_UINT_DEFINED
typedef unsigned int uint;
#endif
#include <stdbool.h>
typedef struct uart_inst { int dummy; } uart_inst_t;
extern uart_inst_t *const uart0;
typedef enum uart_parity { UART_PARITY_NONE = 0 } uart_parity_t;
uint uart_init(uart_inst_t *uart, uint baudrate);
void uart_set_format(uart_inst_t *uart, uint data_bits, uint stop_bits,
                     uart_parity_t parity);
void uart_set_fifo_enabled(uart_inst_t *uart, bool enabled);
bool uart_is_readable(uart_inst_t *uart);
char uart_getc(uart_inst_t *uart);
bool uart_is_writable(uart_inst_t *uart);
void uart_putc_raw(uart_inst_t *uart, char c);
#endif
