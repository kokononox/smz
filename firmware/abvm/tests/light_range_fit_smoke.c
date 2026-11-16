#include <assert.h>
#include <stdio.h>
#include "light_range_fit.h"
int main(void){
 uint32_t c[9]={100,130,300,400,500,600,700,800,900};
 uint32_t l[9]={80,110,290,390,490,590,690,790,890};
 uint32_t h[9]={120,150,310,410,510,610,710,810,910};uint16_t m;
 assert(light_ranges_fit(l,h,c,&m));assert(l[0]==80&&h[0]==115&&l[1]==116&&h[1]==150&&m==3);
 assert(light_ranges_fit(l,h,c,&m)&&m==0);
 c[1]=100;uint32_t old=h[0];assert(!light_ranges_fit(l,h,c,&m)&&h[0]==old);
 puts("fit example, repeat and collision pass");
}
