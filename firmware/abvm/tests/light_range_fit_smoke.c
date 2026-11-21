#include <assert.h>
#include <stdio.h>
#include "light_range_fit.h"
int main(void){
 uint32_t c[9]={100,150,200,400,500,600,700,800,900};
 uint32_t l[9]={70,120,170,390,490,590,690,790,890};
 uint32_t h[9]={130,180,230,410,510,610,710,810,910};uint16_t m;
 assert(light_ranges_fit(l,h,c,&m));
 assert(l[0]==70&&h[0]==125);
 assert(l[1]==126&&h[1]==175);
 assert(l[2]==176&&h[2]==230);
 assert(m==7);
 assert(l[0]<=c[0]&&c[0]<=h[0]);
 assert(l[1]<=c[1]&&c[1]<=h[1]);
 assert(l[2]<=c[2]&&c[2]<=h[2]);
 assert(light_ranges_fit(l,h,c,&m)&&m==0);

 /* The profile array order does not decide who keeps the overlap. */
 uint32_t pc[9]={200,100,150,400,500,600,700,800,900};
 uint32_t pl[9]={170,70,120,390,490,590,690,790,890};
 uint32_t ph[9]={230,130,180,410,510,610,710,810,910};
 assert(light_ranges_fit(pl,ph,pc,&m));
 assert(pl[0]==176&&ph[0]==230);
 assert(pl[1]==70&&ph[1]==125);
 assert(pl[2]==126&&ph[2]==175);
 assert(m==7);

 uint32_t ic[9]={100,100,300,400,500,600,700,800,900};
 uint32_t il[9]={80,80,290,390,490,590,690,790,890};
 uint32_t ih[9]={120,120,310,410,510,610,710,810,910};
 uint32_t old_l[9],old_h[9];
 for(int i=0;i<9;i++){old_l[i]=il[i];old_h[i]=ih[i];}
 assert(!light_ranges_fit(il,ih,ic,&m));
 for(int i=0;i<9;i++)assert(il[i]==old_l[i]&&ih[i]==old_h[i]);
 puts("midpoint range fit, stable centers, permutation, and safe failure pass");
}
