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

 uint32_t pc[9]={100,130,300,400,500,600,700,800,900};
 uint32_t pl[9]={80,110,290,390,490,590,690,790,890};
 uint32_t ph[9]={120,150,310,410,510,610,710,810,910};
 assert(light_ranges_fit_preserving_selected(pl,ph,pc,0,&m));
 assert(pl[0]==80&&ph[0]==120&&pl[1]==121&&ph[1]==150&&m==2);

 uint32_t lc[9]={100,130,300,400,500,600,700,800,900};
 uint32_t ll[9]={80,110,290,390,490,590,690,790,890};
 uint32_t lh[9]={120,150,310,410,510,610,710,810,910};
 assert(light_ranges_fit_preserving_selected(ll,lh,lc,1,&m));
 assert(ll[0]==80&&lh[0]==109&&ll[1]==110&&lh[1]==150&&m==1);

 uint32_t nc[9]={450,500,700,800,900,1000,1100,1200,1300};
 uint32_t nl[9]={430,480,680,780,880,980,1080,1180,1280};
 uint32_t nh[9]={470,520,720,820,920,1020,1120,1220,1320};
 assert(light_ranges_fit_preserving_selected(nl,nh,nc,0,&m));
 assert(nl[0]==430&&nh[0]==470&&nl[1]==480&&nh[1]==520&&m==0);

 uint32_t ic[9]={100,105,300,400,500,600,700,800,900};
 uint32_t il[9]={80,85,290,390,490,590,690,790,890};
 uint32_t ih[9]={120,125,310,410,510,610,710,810,910};
 old=ih[0];
 assert(!light_ranges_fit_preserving_selected(il,ih,ic,0,&m));
 assert(il[0]==80&&ih[0]==old&&il[1]==85&&ih[1]==125);
 puts("symmetric sync fit and selected calibration priority pass");
}
