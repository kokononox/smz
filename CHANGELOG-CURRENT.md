# Classroom Studio — Current Hardware Changelog

این سند مرجع سریع وضعیت شاخهٔ پایدار `stable/natural-mouse-v1` است. ترتیب ورودی‌ها معکوس زمانی است؛ جدیدترین Build همیشه بالاتر قرار می‌گیرد.

## وضعیت فعلی در یک نگاه

- **Candidate Build 91:** دو Wait For Sound هم‌زمان دیگر Listener دوم روی ARM باز نمی‌کنند. Scheduler یک Listener فیزیکی با پایین‌ترین Threshold می‌سازد و با Peak گزارش‌شده، بالاترین پروفایل منطبق را برای اجرای Buzzer انتخاب می‌کند.
- **Candidate Build 90:** پروژهٔ `s1.amsj` دیگر به‌خاطر دو Buzzer داخل شاخه‌های موازی Wait For Sound مسدود نمی‌شود؛ Buzzer اکنون به `BEEP/DELAY` قابل‌اجرای Pico تبدیل می‌شود و متن خطاهای واقعی نیز مستقیماً در پنجرهٔ Export نمایش داده می‌شود.
- **Hardware-passed Build 89:** بستهٔ E اتصال مجدد CDC ویندوز را تشخیص داد، Pico را یک‌بار Reset کرد، `CIRCUITPY` دوباره قابل‌نوشتن شد و Startup/Mouse ادامه یافت. نوت‌های مرحله‌ای پذیرفته‌شده نیز به Runtime استاندارد منتقل شدند. کالیبراسیون صدا هنوز تست سخت‌افزاری نشده است.
- **Hardware-passed Build 88:** بستهٔ تشخیصی A پس از Warm Restart بدون Start دستی زنده ماند و Startup را اجرا کرد. همین مسیر Marker + USB fusion اکنون مسیر استاندارد خروجی Classroom است.
- **Candidate Build 87:** USB DOWN/UP که حین انتهای Route After رخ می‌دهد دیگر پاک نمی‌شود؛ Startup پس از بازگشت Windows ادامه می‌یابد. صدای Save کالیبراسیون نیز به یک الگوی سه‌نتی واضح‌تر ارتقا یافت و نتیجهٔ Save در NVM Debug ثبت می‌شود.
- **Candidate Build 86:** صدای خطای کالیبراسیون برای Sample ناپایدار و فشار زرد هنگام Busy اضافه شد؛ بازهٔ کامل چرخه با پیش‌فرض ۱۱۰–۱۳۰ دقیقه به UI و Runtime برگشت؛ فایل شش‌پروفایلی به‌روز با Dashboard برابر `13.3 ± 3.0 lux` همیشه داخل بستهٔ Classroom قرار می‌گیرد.
- **Candidate Build 85:** کالیبراسیون فیزیکی نور دیگر به Revision خروجی وابسته نیست؛ Snapshot قدیمی CAL1 بازیابی/مهاجرت می‌شود و منبع مؤثر با `CALSTATUS source=nvm` قابل مشاهده است.
- **Candidate Build 84:** چرخهٔ زمان‌محور قدیمی حذف شد؛ پایان Game فوراً After را اجرا می‌کند، Marker پس از Restart تب Startup را یک‌بار اجرا می‌کند، Desktop رد می‌شود و مسیر از Login/DC ادامه می‌یابد. CIRCUITPY نیز دوباره در اختیار Windows است و کالیبراسیون فیزیکی در NVM کنترل‌شده ذخیره می‌شود.
- **Candidate Build 83:** Runtime مدرن اکنون `RUNFOR/AUTORESUME/POSTLAUNCH` را اجرا می‌کند؛ Deadline مسیر جاری را متوقف، Restart ویندوز را ارسال، Marker را در NVM نگه‌داری و پس از USB Down/Up و تأخیر تنظیم‌شده برنامهٔ Pin‌شده را اجرا می‌کند.
- **Candidate Build 82:** Tolerance کالیبراسیون نور اکنون فاصلهٔ هر سمت از Median را مستقل محاسبه می‌کند؛ نمونهٔ نامتقارن Dashboard دیگر بلافاصله پس از Save به `unknown` تبدیل نمی‌شود.
- **Candidate Build 81:** خروجی Classroom Studio اکنون با تست صریح بسته‌بندی کنترل می‌شود تا Runtime سازگار با ARM 2.8.2-S4 شامل شروع ASND، تشخیص/Timeout و Telemetry موازی باشد؛ این Build جایگزین Release قدیمی Build 98 می‌شود.
- **Candidate Build 80:** ARM 2.8.2-S4 با ADC آزادِ پس‌زمینه، Peak صدا را حین حرکت بدون قرار دادن `analogRead` در Cadence موس نگه می‌دارد؛ مقیاس ASND دوباره با SCAL یکسان است.
- **Candidate Build 79:** WSND اکنون Peak واقعی را در تشخیص/Timeout گزارش می‌کند و Timeout عادی دیگر Guard Failure نیست؛ مسیر موس و Cadence تغییر نکرده‌اند.

- **Candidate Build 78:** ARM 2.8.2-S2 مانع توقف ۴۰–۵۲ms ناشی از بازبودن COM بدون HELLO می‌شود؛ پایش صدا و Cadence یک‌میلی‌ثانیه‌ای حفظ شده‌اند.
- **Candidate Build 77:** ARM 2.8.2-S1 پایش صدا را از حلقهٔ Micro-step خارج می‌کند تا Cadence نرم 2.8.1 برگردد؛ Typo نیز دوباره فاصلهٔ کاراکتری واقعی است.
- **Baseline سخت‌افزاری:** Build 68 مسیرهای Desktop/Login/DC/Game را بدون MemoryError روی Pico اجرا کرد؛ حرکت Natural Mouse v1 حفظ شد.
- **مسئلهٔ باز Build 68:** برای جلوگیری از `ERR|BUSY`، Sound فقط هنگام توقف Mouse Poll می‌شد و واکنش F به صدای قلاب دیر می‌رسید.
- **راه‌حل Build 69:** ARM 2.8.2 صدا را درون حلقهٔ Mouse به‌صورت Async پایش می‌کند، حرکت را همان لحظه متوقف می‌کند و Pico کلید F را بدون انتظار برای پایان Mouse می‌زند.
- **اصل معماری:** Pico مسئول Keyboard/Guard/Route است؛ Pro Micro مسئول Mouse HID و پایش Sound هم‌زمان است.
- **کالیبراسیون Game:** در Export مدرن از پروفایل ذخیره‌شدهٔ فعلی Classroom استفاده می‌شود؛ مقدار ثابت Template دیگر منبع اجرا نیست.
- **Golden 100:** جدا و بدون تغییر باقی مانده است.

| Build | نتیجهٔ سخت‌افزاری | مسئله/تغییر اصلی | وضعیت |
| --- | --- | --- | --- |
| 91 | Build 143: Runtime با `only one WSND listener is allowed` متوقف شد | Listener مشترک ADC و انتخاب پروفایل با Peak | CI candidate؛ Sound pending |
| 90 | `s1.amsj`: Export با ۲ خطا Block شد | پشتیبانی Buzzer داخل ForLoop شاخه‌های Parallel و نمایش جزئیات خطا | CI candidate؛ Sound pending |
| 89 | بستهٔ E پاس: Startup، Mouse، نوت‌ها و نوشتن/حذف TEST.txt؛ Sound calibration تست نشده | Reset یک‌بارهٔ Pico پس از CDC reconnect برای Remount قابل‌نوشتن | Hardware pass؛ Sound pending |
| 88 | بستهٔ A پاس: Restart، Resume خودکار و اجرای Startup بدون Start دستی | Resume با Marker معتبر حتی وقتی Windows هیچ USB DOWN گزارش نمی‌کند | Hardware pass |
| 87 | Build 124: Restart انجام شد ولی Startup خودکار اجرا نشد؛ Tone ذخیره شنیده نشد | حفظ USB transition حین After و تقویت/ثبت Tone ذخیره | CI candidate |
| 86 | تست سخت‌افزاری لازم است | بازخورد صوتی Fail کالیبراسیون، بازهٔ ۱۱۰–۱۳۰ دقیقه و پروفایل نور همراه بسته | CI candidate |
| 85 | تست سخت‌افزاری لازم است | ماندگاری کالیبراسیون فیزیکی بین Exportها و Telemetry منبع NVM | CI candidate |
| 84 | تست سخت‌افزاری لازم است | After/Startup مستقل، حذف تایمرهای قدیمی، Desktop skip و NVM calibration | CI candidate |
| 83 | تست سخت‌افزاری لازم است | اجرای واقعی Restart Cycle، NVM Marker، HOSTUSB و Auto Resume | CI candidate |
| 82 | Dashboard با center=13.3، spread=2.5 و live=15.8 به unknown رفت | محاسبهٔ دامنهٔ نامتقارن P5/P95 نسبت به Median | CI candidate |
| 81 | S4 + Bundle 203 دستی Catch را پاس کرد | انتشار Classroom با Runtime داخلی سازگار با S4 | CI candidate |
| 80 | S4 + Bundle 203 دستی Catch را پاس کرد | بازیابی شنیدن پیوسته بدون شکستن نرمی موس | Hardware pass |
| 79 | تست سخت‌افزاری لازم است | Peak telemetry برای WSND و Timeout غیرخطایی | CI candidate |
| 78 | Build 95: میانهٔ 51ms و 1,194 وقفهٔ حداقل 40ms | USB handshake فقط با بایت واقعی؛ حذف stall هنگام بازبودن COM | Local candidate |
| 77 | Build 94: حرکت پس از ARM 2.8.2 شکسته و Typo 7–12 تقریباً روی هر حرف اجرا شد | بازیابی Cadence 2.8.1 با Sound بین فرمان‌ها؛ Typo با فاصلهٔ کاراکتری | Local candidate |
| 76 | Bundle 175: دو Guard JSON با Debug/FAT cross-link خراب شدند | حذف Debug file write، پاسخ سریع دکمه و Read-back کامل Export | Local candidate |
| 75 | Build 74: Game ابتدا اجرا شد؛ بازگشت بعد از Lux spike شکست خورد | Game re-entry، کالیبراسیون مقاوم Game/Target و بازیابی کامل Bridge | Local candidate |
| 74 | تست سخت‌افزاری لازم است | کالیبراسیون پرتابل دو Step صوتی با GP3/GP4 و Binding Hash | Local candidate |
| 73 | تست سخت‌افزاری لازم است | انتقال پروفایل‌های نور فعلی Classroom به Pico | CI candidate |
| 72 | تست سخت‌افزاری لازم است | Proxy صدا از Pico به Pro Micro و حذف نویز Cursor | CI candidate |
| 71 | تست سخت‌افزاری لازم است | رفع اتصال سبز کاذب و کالیبراسیون صوتی روی Bridge قطع‌شده | CI candidate |
| 70 | تست سخت‌افزاری لازم است | بازیابی امن LABEL/GOTO و Light Watch | CI candidate |
| 69 | تست سخت‌افزاری حرکت بعداً Regression نشان داد | پایش Async صدا داخل Micro-step و توقف فوری Mouse پیش از F | Superseded by 77 |
| 68 | Desktop/Login/DC/Game پاس | Runner سبک Game؛ تأخیر Sound هنگام حرکت | Hardware pass؛ Sound superseded |
| 67 | Desktop و Login/DC سبک پاس؛ Calibration ذخیره شد | Runner سبک Streaming برای Login/DC | Hardware pass؛ Game superseded |
| 66 | Desktop و Natural Mouse پاس؛ Login/DC MemoryError | Natural Mouse v1 روی Baseline 50 | Mouse-stable baseline |
| 49 | Route تست A کامل شد | Runner سبک RMOUSE بدون Executor کامل | Functional pass؛ کیفیت حرکت در حال تیون |
| 48 | Import و Parse موفق؛ اجرا شکست خورد | Lazy import Parser/Executor | Superseded by 49 |
| 47 | تست A در Import شکست خورد | Fishing timeout + cadence 128-point | Superseded by 48/49 |
| 46 | Login RMOUSE و Stop تأیید شدند | Streaming RMOUSE عادی | Verified foundation |
| 45 | بازیابی Heap و حذف SCAL وسط حرکت | Stop cleanup | Verified foundation |
| 43 | Pause release، SCAL BUSY و sampled speed | Timing/UART fixes | Superseded |
| 41 | Parallel memory و پروفایل‌های نور | Streaming parallel RMOUSE | Superseded |
| 40 | Retry کالیبراسیون overlap | Calibration UX | Verified |
| 39 | Facade صحیح در Export پروژهٔ جاری | Export ordering | Verified foundation |
| 38 | Split executor اولیه | کاهش فشار Import | Superseded by 39 |

## Build 91 — Listener مشترک برای Parallel Sound

**Previous build:** 90 / Classroom release 143
**Status:** CI candidate; two-profile physical sound test required
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

Build 143 پروژهٔ `s1.amsj` را با موفقیت Export کرد، اما در اجرای Desktop هر دو شاخهٔ Parallel تقریباً هم‌زمان به Wait For Sound رسیدند. Listener اول با Threshold 130 فعال شد و Listener دوم باعث `ValueError('only one WSND listener is allowed')` و توقف Guard شد.

### Root cause

Pro Micro فقط یک ADC و یک State ماشین `ASND` دارد. Scheduler هر Wait For Sound منطقی را به‌عنوان Listener فیزیکی مستقل اجرا می‌کرد؛ بنابراین ساختار معتبر دو پروفایلی پروژه با محدودیت سخت‌افزار برخورد می‌کرد.

### Change

- همهٔ Wait For Soundهای هم‌زمان یک Parallel Group در یک Listener فیزیکی ادغام می‌شوند.
- Listener مشترک با پایین‌ترین Threshold، کوتاه‌ترین Minimum و نزدیک‌ترین Timeout شروع می‌شود.
- اگر Listener دوم در همان دور Scheduler برسد، Listener اولیه Cancel و فوراً با قرارداد مشترک Restart می‌شود.
- پس از Detection، Peak واقعی ARM خوانده می‌شود و بالاترین Threshold منطبق بر Peak برنده می‌شود.
- فقط شاخهٔ برنده ادامه پیدا می‌کند و Buzzer مربوط به همان پروفایل اجرا می‌شود.
- اگر بیش از یک پروفایل وجود داشته باشد ولی Firmware Peak telemetry ندهد، مسیر Fail-closed باقی می‌ماند.

### Validation

- تست Peak برابر ۱۴۰ تأیید کرد شاخهٔ Threshold 130 و Buzzer هشدار اجرا می‌شود.
- تست Peak برابر ۸۰ تأیید کرد شاخهٔ Threshold 30 و Buzzer موفقیت اجرا می‌شود.
- تست Listener تکی، Mouse هم‌زمان، Timeout، Cancel و ARM Async Sound همچنان پاس شد.
- خطای قدیمی `only one WSND listener may be active` از Scheduler حذف شد.
- کالیبراسیون صدای فیزیکی هنوز تست نشده و Hardware pass آن ادعا نمی‌شود.

### Next test

پس از کالیبراسیون IDهای ۱ و ۲، پروژهٔ `s1.amsj` را اجرا کنید. صدای با Peak بالاتر از پروفایل ۱ باید فقط Tone هشدار و صدای بین Thresholdهای ۲ و ۱ باید فقط Tone موفقیت را اجرا کند؛ Guard نباید متوقف شود.

## Build 90 — Buzzer داخل Parallel Sound

**Previous build:** 89 / Classroom release 139
**Status:** CI candidate; s1 export and physical sound calibration retest required
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

خروجی استاندارد پروژهٔ `s1.amsj` با پیام `plan export blocked: 2 problem(s)` متوقف شد. پروژه شامل دو شاخهٔ ForLoop در Parallel Group بود؛ هر شاخه یک Wait For Sound ساده و یک Buzzer داشت.

### Root cause

Scheduler پیکو از `BEEP` در Parallel پشتیبانی می‌کرد، اما `PlanExporter` نوع `buzzer` را نه در فهرست Cooperative leafها پذیرفته بود و نه به فرمان‌های `BEEP/DELAY` تبدیل می‌کرد. بنابراین هر یک از دو شاخه یک خطای سازگاری ایجاد می‌کرد. پنجرهٔ Current project export نیز فقط تعداد خطاها را نشان می‌داد و جزئیات را در Log پنهان می‌کرد.

### Change

- `buzzer` به مجموعهٔ Stepهای مجاز داخل Parallel Group اضافه شد.
- Preset یا Pattern سفارشی Buzzer با همان Validation موجود به `BEEP|frequency,duration` تبدیل می‌شود.
- Pause میان نت‌ها به `DELAY|min,max` قابل‌اجرای Plan تبدیل می‌شود.
- تست دقیق ساختار پروژهٔ s1 دو شاخهٔ `ForLoop → Wait For Sound → Buzzer` را پوشش می‌دهد.
- پنجرهٔ Current project Pico export تا شش خطای واقعی را مستقیماً نمایش می‌دهد.

### Validation

- قرارداد Python برای سازگاری Parallel/Buzzer پاس شد.
- Runtime موازی `BEEP` را از مسیر عمومی Executor اجرا می‌کند و Wait For Sound ساده همچنان Cooperative است.
- دو ID کالیبراسیون ۱ و ۲ در پروژه یکتا و معتبرند؛ خطا از Calibration ID نبود.
- کالیبراسیون صدای فیزیکی همچنان تست نشده و Hardware pass آن ادعا نمی‌شود.

### Next test

پروژهٔ `s1.amsj` را در Build جدید باز و Current project Pico export کنید. پس از خروجی موفق، Sound Calibration برای IDهای ۱ و ۲ را انجام دهید و اجرای هم‌زمان دو شاخه، Tone هشدار/موفقیت و Timeout را روی سخت‌افزار بررسی کنید.

## Build 89 — Remount قابل‌نوشتن پس از Restart

**Previous build:** 88 / Classroom release 136
**Status:** Hardware passed with diagnostic package E; sound calibration remains untested
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

بستهٔ A چرخه را پس از Restart زنده نگه داشت، اما `CIRCUITPY` پس از بازگشت Windows همچنان Write-protected بود. بستهٔ D با Reset زمان‌محور نیز نه Startup/Mouse را اجرا کرد و نه Read-only را رفع کرد.

### Root cause

روی RP2040 روشن‌شده از USB پایدار، Restart میزبان الزاماً Pico را Reset نمی‌کند. CircuitPython ممکن است Mass Storage را هنگام بازگشت میزبان به‌صورت Read-only معرفی کند. Reset باید بعد از بازگشت واقعی Windows انجام شود؛ تایمر ثابت به‌تنهایی با زمان بوت میزبان هم‌تراز نیست.

### Change

- Controller پس از After منتظر قطع و اتصال مجدد CDC می‌ماند.
- پنج ثانیه پس از CDC reconnect، Marker را با `RESET_DONE` علامت می‌زند و Pico را یک‌بار Reset می‌کند.
- اگر CDC reconnect تشخیص داده نشود، Fallback پس از ۱۲۰ ثانیه Reset را انجام می‌دهد.
- Boot بعدی Marker را بازیابی، از حلقهٔ Reset جلوگیری و مسیر استاندارد Startup را اجرا می‌کند.
- نوت‌های پذیرفته‌شدهٔ تست E در خروجی استاندارد حفظ شدند: یک نت بم هنگام مسلح‌شدن Watcher، دو نت پیش از Reset، سه نت پس از بازیابی Marker و ملودی صعودی پس از تکمیل Startup.
- فشار دستی Start در فاز انتظار، چرخهٔ Pending را لغو و یک Run تازه آغاز می‌کند.

### Validation

- بستهٔ E روی سخت‌افزار پاس شد: تمام نوت‌های مرحله‌ای، Startup و حرکت Mouse اجرا شدند و `TEST.txt` روی `CIRCUITPY` ساخته و حذف شد.
- Regression مسیر `CDC DOWN → UP → stable 5s → one-shot reset → boot marker → Startup` پاس شد.
- Fallback صدوبیست‌ثانیه‌ای، Manual override، USB stale، Deadline، Desktop skip و Marker reserved region پوشش داده شدند.
- Manifest مدرن ۳۲/۳۲ صحیح و تست‌های FAT isolation، NVM calibration و بازخورد صوتی کالیبراسیون پاس شدند.
- **کالیبراسیون صدای فیزیکی هنوز توسط کاربر تست نشده و Hardware pass آن ادعا نمی‌شود.**

### Next test

خروجی استاندارد **Current project Pico export** را با چرخهٔ کوتاه تست کنید و سپس کالیبراسیون صدای فیزیکی را جداگانه کامل کنید: ورود به Sound Calibration، انتخاب هر Profile، Sample/Save، خروج و اجرای یک Step صوتی واقعی. نتیجهٔ Sound calibration باید جداگانه ثبت شود.

## Build 88 — حذف وابستگی Resume به USB DOWN

**Previous build:** 87 / Classroom release 128
**Status:** Hardware passed with diagnostic package A; promoted to standard export
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

در تست Build 128، کالیبراسیون و Tone جدید پاس شد؛ اما پس از Restart و بالا آمدن Windows، Startup خودکار اجرا نشد و فشار دستی Start چرخه را از نو آغاز کرد. Bundle 225 همان Runtime منتشرشدهٔ Build 87 را داشت و ۳۲/۳۲ Hash آن صحیح بود.

### Root cause

روی این سخت‌افزار Warm Restart برق USB و وضعیت Configured پیکو را قطع نمی‌کند. در نتیجه نه `supervisor.runtime.usb_connected` و نه Pro Micro الزاماً لبهٔ `DOWN` تولید نمی‌کنند. Build 87 لبه‌های سریع DOWN/UP را حفظ می‌کرد، اما همچنان اجرای Startup را مشروط به مشاهدهٔ DOWN کرده بود؛ بنابراین با USB دائماً `UP`، Controller برای همیشه در `wait-usb` می‌ماند.

### Change

- تکمیل موفق Route After و Marker مسلح NVM اکنون مجوز معتبر Resume است، حتی اگر USB هیچ DOWN گزارش نکند.
- اگر Pro Micro روی `DOWN` قدیمی مانده باشد، Marker مسلح همراه `Pico UP` آن State منقضی را کنار می‌زند.
- در حالت USB پایدار `UP`، Controller دو ثانیه پایداری را کنترل می‌کند و سپس Route Startup را آغاز می‌کند.
- Route Startup همچنان Delay داخلی ۳۰–۶۰ ثانیه دارد؛ بنابراین آغاز Controller به معنی ارسال فوری Win+1 هنگام خاموش‌شدن Windows نیست.
- Telemetry این مسیر را با `source=marker-no-down` از مسیر دارای لبهٔ واقعی USB متمایز می‌کند.
- مسیر معمول DOWN/UP و بازیابی پس از Reset واقعی Pico بدون تغییر باقی مانده است.

### Validation

- Regression جدید سناریوی «After کامل، Marker مسلح، USB همیشه UP» را اجرا می‌کند و تأیید می‌کند Startup بدون Start دستی اجرا و Marker پاک می‌شود.
- سناریوی DOWN/UP حین After، Boot با Marker، Deadline، پایان طبیعی Game و Desktop skip همچنان پاس می‌شوند.
- Bundle 225 ارسالی مستقل بررسی شد: ۳۲/۳۲ Hash صحیح و محتوای Runtime با Build 87 یکسان بود.
- تست سخت‌افزاری بستهٔ A پاس شد: چرخه پس از Restart بدون فشار مجدد Start زنده ماند. نسخه‌های تشخیصی B و C لازم نشدند.

### Next test

خروجی عادی **Current project Pico export** از این Build به بعد همان منطق پذیرفته‌شدهٔ بستهٔ A را در `restart_cycle.py` و `SHA256SUMS.txt` قرار می‌دهد. فایل‌های تشخیصی، حرکت موس ۲۰ثانیه‌ای و Tone آزمایشی وارد خروجی استاندارد نشده‌اند؛ Route واقعی Startup پروژه بدون تغییر صادر می‌شود. تست بعدی باید با خروجی استاندارد پروژه و بازهٔ واقعی ۱۱۰–۱۳۰ دقیقه انجام شود.

## Build 87 — حفظ USB Transition و تأیید واضح Save

**Previous build:** 86 / Classroom release 124
**Status:** CI candidate; restart-resume and physical calibration retest required
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

در تست Build 124، Windows Restart شد اما پس از بازگشت، Route `startup_steps.txt` خودکار اجرا نشد و چرخه فقط با فشار دستی Start دوباره فعال شد. در کالیبراسیون Dashboard نیز پس از فشار زرد، صدای ذخیره به‌اندازهٔ کافی قابل‌تشخیص نبود.

### Root cause

USB می‌توانست در همان چند ثانیهٔ پایانی Route After از DOWN به UP برگردد. `route_tick()` در فاز After این تغییر را ثبت نمی‌کرد و `_perform_after()` نیز هنگام ورود به `wait-usb` متغیرهای `down_seen/up_since` را پاک می‌کرد؛ بنابراین Controller منتظر DOWN دیگری می‌ماند که دیگر رخ نمی‌داد. علاوه‌براین وضعیت `HOSTUSB|DOWN` در Pro Micro می‌توانست پس از بازگشت Windows stale بماند و سیگنال UP خود Pico را بپوشاند. Tone موفق قبلی فقط دو نت کوتاه ۲۷۰ms بود.

### Change

- USB DOWN/UP در طول Delayهای Route After اکنون Poll و ثبت می‌شود.
- Evidence ثبت‌شده هنگام ورود به `wait-usb` حفظ می‌شود و در `after-complete` مقدار `down-seen` گزارش می‌شود.
- اگر Pro Micro روی DOWN قدیمی بماند، USB خود Pico پس از مشاهدهٔ DOWN واقعی یا Boot با Marker معتبر می‌تواند State را به UP ارتقا دهد.
- الگوی Save موفق به سه نت صعودی و واضح‌تر با مجموع حدود ۸۰۰ms تغییر کرد.
- `CAL|save-ok` و `CAL|save-failed` همراه Stage، Profile ID و منبع NVM در Debug پایدار ثبت می‌شوند.
- فشار کوتاه زرد نیز `calibration-start ... wait=5s` را ثبت می‌کند تا مشخص باشد Sample واقعاً آغاز شده است.

### Validation

- تست Regression سناریوی `DOWN → UP` حین After و اجرای Startup پس از دو ثانیه پاس شد.
- تست Boot با Marker، Deadline، پایان طبیعی Game و Desktop skip حفظ شد.
- Bundle 220 ارسالی ۳۲/۳۲ Hash صحیح و Dashboard برابر `13.3 ± 3.0 lux` داشت.
- `boot.py` عمداً `readonly=True` نگه داشته شد: این تنظیم CircuitPython را Read-only و مالکیت نوشتن FAT را به Windows می‌دهد؛ برگرداندن آن Host را Read-only می‌کند.

### Next test

1. بازه را ۳ تا ۶ دقیقه نگه دارید و چرخه را Start کنید.
2. پس از After باید `CYCLE|usb|state=DOWN|during=after` یا DOWN معمولی، سپس `state=UP` و `startup-in=2` دیده شود.
3. پس از بازگشت Windows، بدون فشار Start باید Route Startup اجرا و بعد `startup-complete|next=login-or-dc|desktop=skip` ثبت شود.
4. در Calibration Stage 3 زرد را کوتاه بزنید و پنج ثانیه صبر کنید؛ Success باید سه نت صعودی واضح بدهد. سپس آبی کوتاه باید Stage بعد و آبی بلند باید خروج را اعلام کند.

## Build 86 — بازخورد کالیبراسیون، Deadline چرخه و پروفایل همراه

**Previous build:** 85 / Classroom release 107
**Status:** CI candidate; calibration and timed-cycle hardware retest required
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

در کالیبراسیون فیزیکی Dashboard، اگر Sample ناپایدار می‌شد فقط رخداد CDC ثبت می‌شد و کاربر هیچ صدای خطایی نمی‌شنید؛ فشار دوبارهٔ زرد هنگام Sampling نیز از نظر صوتی ساکت بود. هم‌زمان حذف زمان‌بندی‌های قدیمی، بازهٔ لازم چرخهٔ ۱۱۰ تا ۱۳۰ دقیقه را نیز از UI و Runtime حذف کرده بود. فایل `light-state-profiles.json` ارسالی هم Dashboard قدیمی `15.8 ± 1.0` را داشت و همیشه داخل ZIP برنامه نبود.

### Root cause

Wrapper صوتی فقط مسیر موفق `result=dict` را پوشش می‌داد و انتقال `sampling → None` را نادیده می‌گرفت. در معماری Build 84 همهٔ Headerهای چرخه، از جمله `RUNFOR`، با Resume/PostLaunch قدیمی یکجا Strip شدند. پروفایل‌های پیش‌فرض نیز فقط در کد بودند و فایل قابل‌ویرایش پروفایل به Output پروژه اضافه نشده بود.

### Change

- Sample ناپایدار و فشار زرد هنگام Busy اکنون Tone خطا و Telemetry پایدار تولید می‌کنند؛ Save موفق همچنان Tone صعودی قبلی را دارد.
- کنترل بازهٔ چرخه به Play Options برگشت و پیش‌فرض آن ۱۱۰ تا ۱۳۰ دقیقه است.
- فقط `RUNFOR|min,max` در `plan.txt` مدرن حفظ می‌شود؛ `AUTORESUME` و `POSTLAUNCH` قدیمی همچنان حذف می‌شوند.
- Runtime در Start یک Deadline تصادفی از بازه انتخاب می‌کند؛ در انقضا Route را تمیز Abort می‌کند و پس از آزادشدن Parser، تب After را اجرا می‌کند.
- پایان طبیعی Game نیز همچنان بلافاصله After را اجرا می‌کند.
- فایل شش‌پروفایلی داخل همهٔ بسته‌های Classroom قرار می‌گیرد. Dashboard به مقدار سخت‌افزاری `13.3 ± 3.0 lux` به‌روزرسانی شد؛ سایر بازه‌ها حفظ شدند.

### Validation

- تست Deadline در کران ۱۱۰ دقیقه، پایان طبیعی Game، Marker، Startup و Desktop skip پاس شد.
- تست Tone و Telemetry برای Sample ناپایدار، Overlap retry، CAL2 و NVM migration پاس شد.
- Hashهای Bundle 220 مستقل بررسی شدند: هر ۳۲ فایل سالم بود؛ پروفایل قدیمی Dashboard در آن تأیید شد.
- تست قرارداد UI/Exporter تأیید کرد که RUNFOR حفظ و Headerهای بازنشسته حذف می‌شوند.
- قرارداد Windows برای موجودی Bundle به‌جای قفل‌شدن روی شمارش ثابت، حداقل ۳۶ فایل و حداقل ۳۲ Hash معتبر را کنترل می‌کند؛ قرارداد پروفایل نیز مقادیر پیش‌فرض سخت‌افزاری را مستقل از مسیر Output پروژهٔ تست می‌سنجد.
- پروفایل سخت‌افزاری جدید فقط در فایل همراه `light-state-profiles.json` نگه‌داری می‌شود؛ Seedهای داخلی Phase-four به‌عنوان Fallback اضطراری و برای حفظ قرارداد Classifier دست‌نخورده باقی ماندند.

### Next test

1. Build تازه را Extract کنید و وجود `light-state-profiles.json` با Dashboard برابر `13.3 ± 3.0` را بررسی کنید.
2. برای تست سریع، بازهٔ چرخه را موقتاً ۵ تا ۶ دقیقه تنظیم و Export کنید؛ `plan.txt` باید `RUNFOR|300,360` داشته باشد و پس از Deadline تب After اجرا شود.
3. در کالیبراسیون Stage 3، زرد را بزنید: Save موفق باید Tone صعودی بدهد؛ Sample ناپایدار یا فشار زرد هنگام Busy باید Tone خطا بدهد؛ آبی بلند باید با Tone خروج از Calibration خارج شود.

## Build 85 — ماندگاری کالیبراسیون فیزیکی بین Exportها

**Previous build:** 84 / Classroom release 104
**Status:** CI candidate; Dashboard hardware retest required
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

کالیبراسیون فیزیکی Dashboard با موفقیت Save می‌شد، اما پس از Export عادی پروژه، Runtime دوباره بازهٔ فایل (`14.8..16.8`) را استفاده می‌کرد و نور واقعی نزدیک `13.3` به `unknown` می‌رفت. مانیتور نیز فقط فایل JSON را نشان می‌داد و مشخص نبود منبع مؤثر File است یا NVM.

### Root cause

فرمت CAL1 Snapshot را به Revision دقیق Bundle متصل کرده بود. هر Export Revision را عوض می‌کرد و Loader، Snapshot سالم همان برد و همان Profile IDها را رد می‌کرد. همچنین مقدار اولیهٔ Debug برای State ناشناخته `None` بود و اولین `unknown` همیشه ثبت نمی‌شد.

### Change

- فرمت CAL2 کالیبراسیون را به برد و Profile IDها متصل نگه می‌دارد، نه Revision موقت Export.
- Snapshotهای CAL1 قدیمی بدون نیاز به کالیبراسیون مجدد خوانده و در Save بعدی به CAL2 مهاجرت می‌شوند.
- `CALSTATUS` اکنون `source=nvm|file` را گزارش می‌کند و Boot تعداد Profileهای بازیابی‌شده از NVM را ثبت می‌کند.
- اولین State ناشناخته بعد از Boot، Start و Startup Resume حتماً Telemetry می‌دهد.
- تغییر کاربر از Shuffle All به Random Subset مستقل از این Patch حفظ شده است.

### Validation

- تست استقلال Revision، مهاجرت CAL1، Checksum خراب و مرزهای رزروشدهٔ NVM پاس شد.
- قراردادهای Restart Cycle، Export، FAT isolation، Guard Start و Sound/Mouse پاس شدند؛ Windows TestRunner نیز رشد کنترل‌شدهٔ Entry Point را در سقف ۵۵KB تأیید می‌کند.
- Manifest همهٔ فایل‌های Runtime تغییرکرده را با SHA-256 جدید پوشش می‌دهد.

### Next test

1. Build را بدون پاک‌کردن NVM روی برد Export کنید؛ کالیبراسیون مجدد نباید لازم باشد.
2. در Boot باید `CAL|storage=nvm|loaded=...` و در `CALSTATUS` مقدار `source=nvm` دیده شود.
3. Start در Dashboard باید `STATE/character-dashboard` و Route مربوط را ثبت کند؛ اگر `source=file` بود، یک‌بار Stage 3 را Save کنید.

## Build 84 — چرخهٔ Route-driven After/Startup و مالکیت امن CIRCUITPY

**Previous build:** 83 / Classroom release 102
**Status:** CI candidate; feature-branch packaging enabled; staged hardware test required
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

چرخهٔ Build 83 یک Deadline مستقل `RUNFOR` داشت و پس از پایان آن مستقیماً توالی داخلی Restart را اجرا می‌کرد. در نتیجه مدت Game از استپ‌های خود Game جدا شده بود، تب Restart پروژه عملاً منبع After نبود و پس از بالا آمدن Windows نیز Resume با تأخیرهای قدیمی اجرا می‌شد. همچنین `boot.py` مالکیت نوشتن FAT را به CircuitPython داده بود و Windows درایو را Read-only می‌دید.

### Root cause

چرخهٔ مدرن هنوز مدل قدیمیِ زمان‌محور را منبع حقیقت می‌دانست و پایان واقعی Route بازی را به مرحلهٔ After متصل نمی‌کرد. Resume نیز به‌جای یک Route مستقل Startup، از تأخیرها و Launch قدیمی استفاده می‌کرد. سیاست `boot.py` نیز برای ذخیرهٔ JSON کالیبراسیون، مالکیت FAT را از Windows گرفته بود.

### Change

- زمان اجرا فقط داخل `game_steps.txt` و استپ‌هایی مانند `LOOPTIME` تعریف می‌شود؛ هیچ تایمر سراسری پیش از Restart وجود ندارد.
- با پایان موفق Game، `restart_steps.txt` با عنوان **After** فوراً اجرا می‌شود و Marker قبل از آن در NVM ثبت می‌گردد.
- تب و فایل مستقل `startup_steps.txt` اضافه شد؛ پس از Restart و USB پایدار دقیقاً یک‌بار اجرا می‌شود.
- پس از Startup، Stage روی Login تنظیم می‌شود؛ Desktop اجرا نمی‌شود و جریان از Login/DC ادامه می‌یابد.
- Resume Essentials، RUNFOR، AUTORESUME و POSTLAUNCH از UI و خروجی مدرن حذف شدند؛ تنظیم‌های قدیمی فقط برای سازگاری فایل تنظیمات باقی مانده‌اند.
- CIRCUITPY به Windows واگذار شد. کالیبراسیون نور در ناحیهٔ میانی NVM با Magic، طول، Checksum و اتصال به Revision پروژه ذخیره می‌شود؛ Debug در ۰..۱۵۳۵ و Marker در ۱۶ بایت انتهایی دست‌نخورده‌اند.
- ساختار After برای روش‌های بعدی Restart آماده است؛ فعلاً محتوای تب After اجرا می‌شود و روش Alt+F4 با Hold تصادفی ۸۸–۱۸۸ms در صف توسعه باقی می‌ماند.

### Validation

- تست چرخه پایان Game → After → USB Down/Up → Startup → Login و Desktop skip پاس شد.
- تست NVM شامل Checksum، Revision binding و عدم تداخل با Debug/Restart Marker پاس شد.
- Manifest مدرن ۳۲ فایل دارد و Startup/NVM module در Hash verification قرار گرفته‌اند.
- تست‌های UI، Export، FAT isolation، Guard transitions و Runtime sound/mouse contract پاس شدند.

### Next test

1. Game با `LOOPTIME` کوتاه تمام شود و لاگ بلافاصله `CYCLE|after-start` را نشان دهد.
2. پس از Restart، لاگ `CYCLE|usb|state=UP|startup-in=2`، سپس `startup-start` و `startup-complete|next=login-or-dc|desktop=skip` را نشان دهد.
3. در Windows، CIRCUITPY قابل‌نوشتن باشد و ذخیرهٔ کالیبراسیون رویداد `CAL|storage=nvm` ایجاد کند.

## Build 83 — Restart Cycle و Auto Resume واقعی در Runtime مدرن

**Previous build:** 82
**Status:** CI candidate; staged hardware test required
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

Exporter تنظیم‌های `RUNFOR|300,600`، `AUTORESUME|1,180,300` و `POSTLAUNCH|1,1,1,3,20,40` را درست داخل `plan.txt` می‌نوشت، اما Runtime مدرن هیچ Parser یا Schedulerای برای آن‌ها نداشت. پس از بیش از ده دقیقه Game هیچ Restart رخ نمی‌داد؛ بازگشت دستی به Desktop نیز چون Guard هنوز در Session قبلی Game بود با `desktop-only-valid-at-start` رد می‌شد.

### Root cause

پس از مهاجرت به Runtime کم‌حافظهٔ Guard، ماژول‌های چرخهٔ قدیمی در Export مدرن حذف شدند و فقط Directiveها باقی ماندند. همچنین رویدادهای `EVT|HOSTUSB|DOWN/SUSPEND/UP` در UART چاپ می‌شدند ولی State آن‌ها برای Auto Resume نگه‌داری نمی‌شد.

### Change

- Parser سبک و مستقل برای Headerهای Root بدون Import کردن Plan Engine کامل اضافه شد.
- Deadline در مسیرهای طولانی نیز از طریق Control Gate بررسی و Route جاری به‌صورت Fail-safe متوقف می‌شود.
- Restart طبیعی با توالی انسانی Windows اجرا می‌شود؛ Stop دستی هرگز Restart ایجاد نمی‌کند.
- پیش از Restart، Marker و شمارندهٔ حداکثر پنج Restart در انتهای NVM ذخیره می‌شود؛ 1536 بایت Debug دست‌نخورده می‌ماند.
- Auto Resume فقط پس از Marker معتبر و USB Down/Suspend سپس UP انجام می‌شود؛ Fallback داخلی Pico نیز حفظ شده است.
- پس از تأخیر ۳ تا ۵ دقیقه، `POSTLAUNCH` با Taskbar Slot تنظیم‌شده اجرا، Guard Reset و RUNFOR تازه مسلح می‌شود.
- Start دستی هنگام انتظار، Marker قبلی را لغو و یک Session تازه ایجاد می‌کند.
- پیام تکراری `duplicate-stable-state` فقط یک بار برای هر Reason/State ثبت می‌شود.

### Validation

- تست واحد Parser، NVM Marker، Deadline، Boot Marker، USB Resume و Manual Override را کنترل می‌کند.
- قرارداد Export وجود دو ماژول Restart، HOSTUSB State و 30 Hash معتبر را بررسی می‌کند.
- تست‌های S4 Sound، Mouse، Heap calibration، FAT isolation و Game re-entry بدون تغییر باید پاس شوند.

### Next test

1. ابتدا با `RUNFOR|60,90` و `AUTORESUME|1,20,30` تست کوتاه انجام شود.
2. لاگ باید `CYCLE|armed`، `CYCLE|deadline`، `CYCLE|restart-sent`، `CYCLE|usb|state=DOWN/UP`، `CYCLE|postlaunch` و `CYCLE|resumed` را نشان دهد.
3. پس از تأیید، تنظیم واقعی ۵ تا ۱۰ دقیقه و Auto Resume سه تا پنج دقیقه دوباره Export شود.

## Build 82 — پوشش نمونه‌های نامتقارن کالیبراسیون نور

**Previous build:** 81
**Status:** CI candidate; Dashboard hardware retest pending
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

Dashboard طی نمونه‌برداری `center=13.3` و `spread=2.5` داشت، اما `tolerance=1.0` ذخیره شد. نور پایدار بعدی `15.8` بود؛ بنابراین بازهٔ 12.3 تا 14.3 آن را نپذیرفت و Guard با وجود Save موفق، `STATE/unknown` گزارش کرد.

### Root cause

فرمول قبلی نصف فاصلهٔ P10 تا P90 را به‌عنوان Tolerance دور Median قرار می‌داد. این فقط وقتی صحیح است که Median دقیقاً وسط دو Quantile باشد. در توزیع نامتقارن Dashboard، Median نزدیک لبهٔ پایین بود و نیمهٔ بالایی دامنه حذف شد.

### Change

- Envelope مقاوم از P5/P95 ساخته می‌شود تا نویز منفرد حذف، اما تغییر تکرارشونده حفظ شود.
- Tolerance برابر بیشترین فاصلهٔ `Median→P5` یا `Median→P95` به‌علاوهٔ Margin است.
- Cap پروفایل مجاور و بررسی Overlap بدون تغییر باقی مانده‌اند؛ بازه نمی‌تواند وارد Game یا Targeted شود.

### Validation

- Regression واقعی `18×13.3 + 2×15.8` باید Center برابر 13.3 و Tolerance حداقل 3.0 بسازد.
- مقدار 15.8 باید داخل پروفایل باشد و `find_profile_overlap` همچنان هیچ همپوشانی جدیدی نپذیرد.
- تست Outlier قدیمی Game باید همچنان Tolerance محدود 1.0 تا 1.5 داشته باشد.

### Next test

فقط Stage 3 یعنی Character Dashboard را دوباره نمونه‌برداری و Save کنید. پس از خروج از Calibration، Start باید در همان محیط `STATE/character-dashboard` و سپس Route مربوط را ثبت کند.

## Build 81 — همگام‌سازی Runtime داخلی Classroom با S4

**Previous build:** 80
**Status:** CI candidate; Classroom export hardware retest pending
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

ARM 2.8.2-S4 همراه با Pico Bundle 203 هماهنگ، Catch را در تست سخت‌افزاری انجام داد؛ اما آخرین Classroom Studio منتشرشده پیش از Merge شدن S4 ساخته شده بود. بنابراین Export دوبارهٔ همان پروژه با Release قدیمی، Runtime قبلی Pico را روی برد می‌نوشت و Catch از کار می‌افتاد.

### Root cause

تنظیم‌های Threshold/Minimum داخل AMSJ منتقل می‌شوند، ولی پروتکل Async Sound و پردازش `EVT|ASND` بخشی از Runtime داخلی Classroom هستند. Release عمومی موجود مربوط به قبل از Commit پایدار S4 بود؛ در نتیجه فایل پروژه به‌تنهایی نمی‌توانست Runtime را ارتقا دهد.

### Change

- Release جدید Classroom مستقیماً از شاخهٔ پایدار دارای S4 ساخته می‌شود.
- قرارداد TestRunner اکنون روی فایل واقعاً Export‌شده، وجود Start پروتکل ASND، تشخیص، Timeout و Telemetry `mode=async` را کنترل می‌کند.
- منطق Mouse، ADC، Threshold و Route تغییر نکرده است؛ این Build فقط همگام‌سازی و جلوگیری از بازگشت بسته‌بندی قدیمی است.

### Validation

- TestRunner باید ثابت کند `combined_guard_runtime.py` موجود در خروجی Classroom قرارداد کامل S4 را دارد.
- معیار تست سخت‌افزاری: با ARM 2.8.2-S4 و Export مستقیم پروژه از Classroom جدید، Threshold 76 و Minimum 20ms بدون کپی دستی Bundle Catch کند.

### Next test

پس از نصب Classroom جدید، همان پروژهٔ `p-updated-v3-fishing-parallel-natural-v1-S4-sound76-20.amsj` را مستقیم Export کنید و یک چرخهٔ Catch را بدون جایگزینی دستی فایل‌های Pico اجرا کنید.


## Build 80 — ADC پیوسته و Peak-Latch برای Sound موازی

**Previous build:** 79  
**Status:** CI candidate; hardware retest pending  
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

تستر Blocking با SCAL برای چلپ واقعی Peak حدود 143–145 و برای سکوت حداکثر 6 ثبت کرد، اما Listener موازی ASND در Route ماهیگیری فقط Peakهای 12–22 می‌دید و تقریباً از هر سه یا چهار چلپ فقط یکی را Catch می‌کرد. پایین‌آوردن Threshold از مقدار کالیبرهٔ حدود 76 به 12 فقط Workaround بود و نشان می‌داد دو مسیر اندازه‌گیری مقیاس یکسانی ندارند.

### Root cause

Build 77 برای بازیابی نرمی ARM 2.8.1، `ARM_SOUND_TICK()` را به‌درستی از حلقهٔ Micro-step موس حذف کرد، اما ASND پس از آن فقط یک `analogRead` بین فرمان‌های MMOVE انجام می‌داد. هنگام اجرای DDA، ADC عملاً گوش نمی‌داد و بیشتر قلهٔ کوتاه چلپ از دست می‌رفت؛ در مقابل SCAL در پنجره‌های 10ms صدها نمونه می‌گرفت و Peak واقعی را می‌دید.

### Change

- ARM 2.8.2-S4 ADC سخت‌افزاری ATmega32U4 را فقط هنگام ASND در حالت Free-Running فعال می‌کند.
- ISR سبک، پنجره‌های تقریباً 10ms با 96 نمونه می‌سازد؛ Peak، Sustained Duration و بیشینهٔ کل را بدون `analogRead` داخل Micro-step نگه می‌دارد.
- Cadence یک‌میلی‌ثانیه‌ای، DDA و Micro-step سه‌پیکسلی S3 دست‌نخورده‌اند.
- رویدادهای Async اکنون Peak واقعی را گزارش می‌کنند و Pico شروع/نتیجهٔ Listener موازی را در GuardHardwareMonitor ثبت می‌کند.
- HALT، Cancel، Detect و Timeout همگی ADC Interrupt را خاموش می‌کنند.

### Validation

- قرارداد Firmware وجود ADC Free-Running، ISR، پنجرهٔ 96 نمونه‌ای و نبود `ARM_SOUND_TICK()`/`analogRead` در حلقهٔ Mouse را کنترل می‌کند.
- Runtime قالب جدید `EVT|ASND|DETECTED|peak=...` و `TIMEOUT|peak=...` را می‌پذیرد و جزئیات را به لاگ Guard منتقل می‌کند.
- معیار تست سخت‌افزاری: Threshold کالیبرهٔ حدود 76 با Peak نزدیک 143 کار کند، Catch چندباره پایدار باشد و نرمی Mouse نسبت به S3 افت نکند.

## Build 79 — Peak واقعی WSND و Timeout غیرخطایی

**Previous build:** 78  
**Status:** CI green and merged; sound hardware retest pending  
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

تست SCAL صدای بازی را تا Peak 129 می‌دید، اما WSND با Thresholdهای 12، 30 و 68 همگی Timeout می‌شد. Threshold یک فوراً Route را کامل و کلید F را اجرا کرد. پاسخ `ERR|TIMEOUT|WSND` نیز به‌اشتباه کل Guard را Fail می‌کرد.

### Root cause

Listener قدیمی بیشترین دامنهٔ مشاهده‌شده را نگه نمی‌داشت؛ بنابراین اختلاف SCAL و WSND قابل اندازه‌گیری نبود. همچنین لایهٔ UART تمام پاسخ‌های `ERR`، از جمله Timeout عادی WSND، را Exception بحرانی تلقی می‌کرد.

### Change

- Listener مسدودکنندهٔ WSND بیشترین Peak واقعی همان بازه را نگه می‌دارد.
- تشخیص با `OK|WSND|DETECTED|peak=...` و Timeout با `ERR|TIMEOUT|WSND|max=...` گزارش می‌شود.
- Pico فقط Timeout همین فرمان را نتیجهٔ عادی `False` می‌داند؛ همهٔ خطاهای دیگر ARM همچنان Fail-Closed هستند.
- پیش از Listener، ID، منبع `calibrated/default`، Threshold، Minimum و Timeout مؤثر ثبت می‌شود.
- ARM به 2.8.2-S3 ارتقا یافت؛ `mouse_move_steps`، DDA، Micro-step و Cadence یک‌میلی‌ثانیه‌ای تغییر نکرده‌اند.

### Validation

- Python runtime با `py_compile` معتبر است.
- قراردادها قالب Peak، رفتار Timeout و نبود Sound sampling داخل حلقهٔ Micro-step را قفل می‌کنند.
- کامپایل Leonardo، تست Exhaustive سه‌پیکسلی و بستهٔ Windows به CI سپرده می‌شوند.

### Next test

Firmware ARM 2.8.2-S3 و Bundle جدید را نصب کنید. تست Game را با Threshold 12/20ms اجرا کنید؛ در صورت Timeout، مقدار `max=...` دقیقاً دامنهٔ دیده‌شده داخل WSND را نشان می‌دهد. اگر صدا تشخیص داده شود، `peak=...` ثبت و F اجرا می‌شود.

## Build 78 — حذف توقف ۵۰ms هنگام بازبودن COM

**Previous build:** 77
**Status:** Local candidate; hardware record analyzed; CI and hardware retest pending
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

- Record سخت‌افزاری Build 95 شامل 2,230 موقعیت و 2,229 Segment بود.
- فاصلهٔ فعال میانه 51ms، صدک 95 برابر 53ms و 1,194 فاصلهٔ حداقل 40ms ثبت شد.
- Baseline نرم Natural Mouse v1 روی Recordهای قبلی میانهٔ 15–16ms و فقط 1–2 فاصلهٔ حداقل 40ms داشت؛ بنابراین شکستگی گزارش‌شده واقعی و شدید است.

### Root cause

- حلقهٔ ARM 2.8.2-S1 در حالت Session امن‌نشده، صرفاً با بازبودن USB CDC وارد `do_handshake(40)` می‌شد؛ حتی وقتی هیچ HELLO یا بایتی در COM وجود نداشت.
- بازبودن COM توسط Arduino IDE، Serial Monitor، Classroom یا هر Scanner می‌توانست بین فرمان‌های Mouse یک انتظار حدود 40–52ms تزریق کند.
- الگوی ثبت‌شدهٔ غالب 52ms در برابر Burstهای 1ms دقیقاً با همین Timeout منطبق است.

### Change

- Firmware به `ARM 2.8.2-S2` ارتقا یافت.
- Secure USB handshake فقط وقتی اجرا می‌شود که `Serial.available()` بایت واقعی گزارش کند؛ بازبودن سادهٔ DTR/COM مسیر Serial1 و HID را متوقف نمی‌کند.
- Async Sound، DDA، Pace یک‌میلی‌ثانیه‌ای، سقف سه‌پیکسلی، Checksum، Fail-Closed و Keyboard روی Pico تغییر نکرده‌اند.

### Validation

- قرارداد Firmware وجود شرط `Serial.available()` و نبود شرط Blocking قدیمی `if(Serial)` را قفل می‌کند.
- تست Exhaustive Endpoint و سقف سه‌پیکسلی با نسخهٔ S2 حفظ می‌شود.
- Record کاربر مستقلاً Parse شد و شمارش 1,194 وقفهٔ فعال حداقل 40ms از دو مسیر محاسبه یکسان بود.

### Next test

ARM 2.8.2-S2 را روی Pro Micro فلش کنید. Classroom و Arduino Serial Monitor می‌توانند باز بمانند؛ بازبودن COM نباید دیگر حرکت را خراب کند. همان `mouse-tune-C-balanced.amsj` را با Bundle تازه اجرا و Record را ارسال کنید. معیار پذیرش: حذف قلهٔ 51–53ms، بازگشت فاصله‌ها نزدیک Baseline 15–20ms، حداکثر Micro-step سه پیکسل و Route کامل.

## Build 77 — بازیابی نرمی موس و فاصلهٔ واقعی Typo

**Previous build:** 76
**Status:** Local candidate; focused contracts passed; Windows CI and hardware retest pending
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

- پس از ARM 2.8.2 حرکت موس نسبت به Baseline سخت‌افزاری Natural Mouse v1 شکسته و خوشه‌ای حس شد.
- Record نمونه، خوشه‌های 1 تا 3 میلی‌ثانیه‌ای را پس از مکث نشان داد؛ تنظیم‌های بلند `idle` بین حرکت‌ها جدا هستند و علت Regression داخل حرکت نیستند.
- تنظیم `typoEveryMin=7` و `typoEveryMax=12` به `typos=7,12` صادر می‌شد. Runtime آن را 7 تا 12 خطا در کل TYPE می‌خواند؛ برای متن 12 کاراکتری تقریباً هر کاراکتر غلط و Backspace می‌شد.

### Root cause

- ARM 2.8.2 تابع `analogRead()` پایش Async Sound را بین تک‌تک Micro-stepهای HID اجرا می‌کرد. این کار مسیر Cadence تأییدشدهٔ ARM 2.8.1 را تغییر داد.
- نام‌های ذخیره‌شدهٔ `typoEveryMin/Max` فاصله را القا می‌کردند، اما Exporter و Runtime آن‌ها را به تعداد کل خطا تغییر داده بودند.

### Change

- ARM 2.8.2-S1 حلقهٔ DDA/Micro-step را به Cadence دقیق 2.8.1 برمی‌گرداند؛ هیچ ADC یا منطق صدا داخل حلقهٔ HID اجرا نمی‌شود.
- پایش Async Sound بین فرمان‌های MMOVE در حلقهٔ اصلی ARM ادامه دارد. با Stream حدود 8ms، تشخیص صدا همچنان سریع و غیرمسدودکننده است، بدون تزریق کار متغیر میان گزارش‌های HID.
- HVER نسخهٔ `2.8.2-S1` را اعلام می‌کند تا فریمور اصلاح‌شده با 2.8.2 قبلی اشتباه نشود.
- Exporter جدید `typochars=min,max` می‌نویسد. Runtime، Login runner، Parallel planner، Split planner و اجرای مستقیم Classroom فاصلهٔ کاراکترهای واجد شرایط را پس از هر اصلاح دوباره قرعه‌کشی می‌کنند.
- `typos=min,max` قدیمی به‌عنوان Count mode و `typo=min,max` قدیمی به‌عنوان فاصلهٔ کلمه‌ای فقط برای سازگاری Planهای قدیمی قابل خواندن می‌مانند.
- متن رابط فارسی و انگلیسی صریحاً «فاصلهٔ بین خطاها برحسب کاراکتر» را نمایش می‌دهد.

### Validation

- قرارداد Firmware ثابت می‌کند `ARM_SOUND_TICK()` داخل `mouse_move_steps` وجود ندارد، ولی Async Sound در مرز فرمان‌ها فعال است.
- تست Exhaustive تمام Deltaهای `-127..127` حفظ Endpoint دقیق و سقف سه‌پیکسلی را تأیید می‌کند.
- تست Typo روی 40 Seed ثابت می‌کند مقدار 7–12 برای متن `zodiak999999` دقیقاً یک اصلاح می‌سازد و متن نهایی صحیح می‌ماند.
- بازهٔ 7–12 روی متن 26 کاراکتری در Seedهای مختلف دو یا سه اصلاح با فاصلهٔ دوباره‌قرعه‌کشی‌شده می‌سازد.
- قراردادهای Legacy Count و Word cadence همچنان پاس می‌شوند.

### Next test

پس از انتشار، ARM 2.8.2-S1 را روی Pro Micro فلش کنید و Build جدید را در پوشه‌ای تازه اجرا کنید. ابتدا همان مسیر موس قبلی را بدون تغییر تنظیم‌ها Record بگیرید؛ نرمی باید به Baseline Natural Mouse v1 برگردد. سپس TYPE با بازهٔ 7 تا 12 را اجرا کنید؛ روی متن 12 کاراکتری باید فقط یک خطای اصلاح‌شونده دیده شود.

## Build 76 — جداسازی FAT و تأیید واقعی Export

**Previous build:** 75
**Status:** Local candidate; focused contracts pending Windows CI and hardware retest
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

- Bundle 175 از نظر ZIP/CRC سالم بود، اما <code>guard-calibration.json</code> با خطوط <code>STATE|denied</code> و <code>guard-transition.json</code> با رویدادهای GP3/GP4 و دادهٔ باینری جایگزین شده بودند.
- SHA واقعی این دو فایل با Manifest متفاوت بود؛ بنابراین Pico هیچ پروفایل معتبر Game برای تشخیص نداشت.
- دکمه‌های کالیبراسیون پیش از پخش Note، Debug را هم روی FAT و هم با نوشتن کامل 1536 بایت NVM Persist می‌کردند و پاسخ حدود یک ثانیه دیر حس می‌شد.

### Root cause

- <code>boot.py</code> برای ذخیرهٔ کالیبراسیون، CIRCUITPY را Writable نگه می‌دارد. Runtime هم‌زمان <code>guard-debug.log</code> را روی همان FAT بازنویسی می‌کرد و Classroom از USB Mass Storage فایل‌های Bundle را جایگزین می‌کرد؛ این مالکیت هم‌زمان باعث Cross-link شدن Sectorها شد.
- Export فقط موفقیت <code>File.Copy</code> را کنترل می‌کرد و بایت‌های نهایی روی درایو، Hashها یا Revision دو Guard JSON را دوباره نمی‌خواند.
- GP4 down/long/up قبل از Action با <code>persist=True</code> مسیر ذخیرهٔ Blocking را اجرا می‌کرد.

### Change

- Debug پایدار Runtime فقط در NVM نگهداری می‌شود و رویداد زنده همچنان از CDC به GuardHardwareMonitor می‌رسد؛ Runtime دیگر هیچ فایل Debug روی CIRCUITPY نمی‌نویسد.
- GP3/GP4 down، up و long فقط Live event هستند و پیش از Note یا Action ذخیرهٔ Blocking ندارند.
- Classroom پیش از Export فرمان <code>HALT|SILENT</code> می‌فرستد و Bridge را می‌بندد.
- پس از کپی، Classroom تا پنج بار 28 فایل Manifest را مستقیماً از CIRCUITPY می‌خواند و SHA-256 هر فایل را کنترل می‌کند.
- دو Guard JSON نیز Parse می‌شوند؛ Revision مشترک و وجود شش پروفایل بررسی می‌شود.
- در هر mismatch، Export موفق اعلام نمی‌شود و پیام بررسی/Reset فایل‌سیستم نمایش داده می‌شود.

### Validation

- تست رگرسیون، Guard JSON دارای متن Debug را عمداً تزریق و رد شدن Read-back را تأیید می‌کند.
- قرارداد Firmware نبودن <code>_DEBUG_FILE</code>، نبودن Remount در مسیر Debug و Live-only بودن رویدادهای فیزیکی را کنترل می‌کند.
- قرارداد Export وجود Quiesce، پنج Retry و پیام <code>28/28 hashes and Guard revisions OK</code> را قفل می‌کند.

### Next test

Build منتشرشده را در پوشهٔ تازه اجرا کنید. Bundle 175 معتبر نیست. Pico را Reset کنید و پروژه را دوباره Export کنید؛ Classroom فقط پس از پیام <code>28/28 hashes and Guard revisions OK</code> باید موفقیت نشان دهد. سپس Start در Game، ورود/انتخاب Calibration و سرعت Note دکمه‌ها را آزمایش و Bundle و Guard log جدید را ارسال کنید.

## Build 75 — بازیابی Game/Target و اتصال بدون بستن Classroom

**Previous build:** 74
**Status:** Local candidate; focused portable tests passed; Windows CI and hardware test pending
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

- لاگ سخت‌افزاری نشان داد Game در `24.2 lux` درست تشخیص داده و Route کامل شد؛ یک جهش کوتاه به `28.3` حالت را Unknown کرد و بازگشت به `25.0` با `game-not-expected` و سپس `duplicate-stable-state` برای همیشه رد شد.
- کالیبراسیون Game یا Targeted با وجود جدایی پروفایل‌های ذخیره‌شده، به‌علت Tolerance بزرگ‌شده پیام `CAL|OVERLAP` می‌داد.
- پس از `Write timeout` رابط کاربری Disconnected می‌شد، ولی Python sidecar و Serial link قبلی می‌توانستند COM31 را باز نگه دارند. Connect بعدی Pico را پیدا نمی‌کرد، به COM30 می‌افتاد و در ZIP پرتابل به‌اشتباه `ams_key.json` می‌خواست.

### Root cause

- Transition فقط ورود Game از Stage 4 یا بازگشت صریح از Targeted را قبول می‌کرد؛ بازگشت طبیعی `Game → Unknown → Game` در Stage 5 تعریف نشده بود.
- کالیبراسیون از `max-min` و `1.5 × spread` استفاده می‌کرد؛ دو Outlier می‌توانستند بازه‌ای چند برابر دامنهٔ واقعی بسازند و آن را وارد Targeted کنند.
- خطای Send فقط State رابط را عوض می‌کرد. نه لینک Python پاک می‌شد و نه Sidecar C# الزاماً Kill می‌شد؛ بنابراین Retry همان Process خراب و COM handle قبلی را دوباره استفاده می‌کرد.

### Change

- Stage 5 اکنون بازگشت پایدار Game بعد از Unknown را به‌صورت معتبر ولی بدون اجرای دوبارهٔ `game_steps.txt` می‌پذیرد (`game-reentry-after-unknown`).
- Tolerance کالیبراسیون از 80٪ مرکزی نمونه‌ها محاسبه می‌شود، Outlierهای ابتدا/انتها را کنار می‌گذارد و در مرز نزدیک‌ترین پروفایل ذخیره‌شده Cap می‌شود. کنترل مثبت همپوشانی همچنان Fail-Closed باقی مانده است.
- قبل از Connect هر Serial link قدیمی بسته می‌شود؛ خطای Connect/Send/Path لینک را پاک و رویداد Disconnected صادر می‌کند.
- C# روی خطای Transport، Sidecar خراب را Kill و Port/Firmware را پاک می‌کند تا Connect بعدی Process تمیز بسازد.
- ZIP پرتابل بدون کلید خصوصی دیگر به مسیر مستقیم COM30 سقوط نمی‌کند؛ اگر Pico Brain پیدا نشود خطای واضح `Pico brain not found` می‌دهد.

### Validation

- رگرسیون `Game → Unknown → Game` ثابت می‌کند Stage 5 حفظ می‌شود و Macro دوباره اجرا نمی‌شود.
- رگرسیون `Game → Targeted → Game` بدون تغییر پاس می‌شود.
- نمونه‌های Game دارای Outlier دیگر Tolerance مصنوعی بزرگ تولید نمی‌کنند و با Targeted همپوشانی ندارند.
- قراردادهای Cleanup لینک، Kill Sidecar و ممنوعیت fallback بدون کلید اضافه شدند.
- تست‌های متمرکز Transition، Calibration، Overlap، Retry، Start-current و Brain-first محلی پاس شدند.
- TestRunner کامل ویندوز به CI سپرده می‌شود؛ محیط محلی Linux ابزار `dotnet` ندارد.

### Next test

پس از انتشار Build، ابتدا در Game با نور پایدار Start بزنید، سپس نور را موقتاً بیرون بازه ببرید و به Game برگردانید؛ باید `game-reentry-after-unknown` ثبت شود و `game_steps.txt` دوباره اجرا نشود. Game و Targeted را جداگانه کالیبره کنید و مقادیر `center/spread/tolerance` را بفرستید. در پایان کابل یا Transport را یک‌بار هنگام اتصال مختل کنید؛ Connect بعدی باید بدون بستن Classroom، COM31 و `role=brain` را دوباره پیدا کند.

## Build 74 — کالیبراسیون پرتابل دو Step صوتی و اتصال Brain-first Classroom

**Previous build:** 73
**Status:** PR candidate; portable contracts passed; Windows CI and hardware test pending
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

- کالیبراسیون دو Step صوتی باید مستقل از کامپیوتر و مستقیماً با GP3/GP4 روی Pico انجام شود، اما شاخهٔ اولیه فایل اجرایی `sound_step_calibration.py` را فقط در Manifest و Exporter نام برده بود و خود فایل وجود نداشت.
- Classroom با انتخاب ذخیره‌شدهٔ COM30 ابتدا Pro Micro را باز می‌کرد و بلافاصله سراغ اتصال مستقیم رمزشده می‌رفت؛ در ZIP پرتابل که عمداً `ams_key.json` خصوصی ندارد، اتصال با `No such file ... bridge/ams_key.json` قطع می‌شد، با اینکه Pico Brain روی COM31 حاضر بود.

### Root cause

- قرارداد `WSNDP`، UI، Parser و Runnerها اضافه شده بودند، ولی ماژول Lazy مسئول نمونه‌گیری، Binding، Checksum، Backup و ذخیرهٔ اتمیک به Repository افزوده نشده بود.
- `detect_board_port()` با دیدن اولین پاسخ عمومی `OK/HELLO/PONG` اسکن را تمام می‌کرد؛ بنابراین پورت مستقیم COM30 می‌توانست قبل از رسیدن اسکن به پاسخ `role=brain` روی COM31 انتخاب شود. `open_link()` نیز بعد از شکست Pico روی پورت دستی، پورت‌های دیگر را برای Brain جست‌وجو نمی‌کرد.

### Change

- در تنظیمات `Wait For Sound` فیلد `Calibration ID` با دو مقدار 1 و 2 اضافه شد و Export استفادهٔ تکراری هر ID را در همهٔ تب‌ها رد می‌کند.
- Route فرمان `WSNDP|id,binding,defaultThreshold,defaultMin,timeout` تولید می‌کند؛ Binding دوازده‌رقمی از همان Step ساخته می‌شود تا Calibration قدیمی روی Step نامرتبط اعمال نشود.
- `sound_step_calibration.py` کامل شد: کشف Bindingها از Routeها، سه ثانیه سکوت، سی ثانیه صدای هدف، Threshold میانه، `minDurationMs=20`، جداسازی حداقلی سیگنال، دو پروفایل مستقل و خطاهای Fail-Closed.
- فایل کاربر `sound-step-calibration.json` دارای SHA-256 داخلی است؛ نوشتن با Temp، Readback و Backup انجام می‌شود و خرابی فایل اصلی به Backup معتبر برمی‌گردد. فایل کاربر عضو Manifest ثابت نیست و Export بعدی آن را جایگزین نمی‌کند.
- ماژول کالیبراسیون Lazy است و پس از Resolve یا خروج موفق از حافظه آزاد می‌شود تا با Plan engine روی Heap محدود Pico هم‌زمان نماند.
- اسکن Classroom اکنون پاسخ عمومی COM30 را فقط fallback نگه می‌دارد و جست‌وجو را تا یافتن `role=brain` ادامه می‌دهد. اگر پورت دستی Pico نباشد، پیش از نیاز به کلید خصوصی یک اسکن Brain-first انجام و در صورت وجود به COM31 سوییچ می‌کند.

### Hardware controls

- فقط در حالت Stop، GP3 زرد بلند: ورود یا ذخیره و خروج از Sound Calibration.
- GP4 آبی کوتاه: جابه‌جایی بین ID 1 و ID 2.
- GP3 زرد کوتاه: شروع سه ثانیه سکوت و سپس سی ثانیه صدای هدف برای ID انتخاب‌شده.
- خروج هنگام Sample نتیجهٔ ناقص را دور می‌ریزد؛ ذخیرهٔ نامعتبر مقدار قبلی را حفظ می‌کند.

### Validation

- هر 40 قرارداد Portable و همهٔ `py_compile`ها محلی پاس شدند.
- تست رفتاری COM30/COM31 ثابت می‌کند پاسخ مستقیم COM30 دیگر جلوی کشف Pico Brain روی COM31 را نمی‌گیرد.
- تست‌های جدید دو ID، محاسبه Threshold، Binding mismatch، Checksum، ذخیره و بازیابی Backup را پوشش می‌دهند.
- همهٔ 28 فایل Manifest وجود دارند و SHA-256 آن‌ها با بایت‌های فعلی برابر است.
- Build و TestRunner ویندوز به CI سپرده می‌شود؛ محیط محلی Linux ابزار `dotnet` ندارد.

### Next test

Build منتشرشده را در پوشه‌ای تازه Extract کنید. حتی اگر تنظیم قبلی COM30 است، Connect باید مرحلهٔ `pico_fallback` و اتصال به Pico Brain روی COM31 را نشان دهد و نباید `ams_key.json` بخواهد. سپس پروژه را کامل روی CIRCUITPY Export کنید؛ برای Step چلپ ID 1 و برای Step Whisper ID 2 بگذارید. در حالت Stop با GP3 بلند وارد شوید، هر ID را با GP4 انتخاب و با GP3 کوتاه نمونه‌برداری کنید؛ پس از `mode=complete` برای هر دو ID، GP3 را نگه دارید تا `mode=saved|count=2` و خروج ثبت شود. سپس هر دو Route جداگانه آزمایش شوند.

## Build 73 — انتقال پروفایل‌های نور Classroom به Pico

**Previous build:** 72
**Status:** CI candidate; Guard hardware retest pending
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

صفحهٔ وضعیت Classroom محیط Game را با پروفایل ذخیره‌شدهٔ کاربر (`25.8 ± 0.5`) و اطمینان 100٪ تشخیص می‌داد، اما Bundle روی Pico همچنان مقادیر ثابت Template (`22.5 ± 3.7` و سایر پروفایل‌های قدیمی) را داشت. بنابراین نمایش Classroom و تصمیم Guard از دو منبع متفاوت استفاده می‌کردند.

### Root cause

`ExportCurrentProject` فقط Routeها، Plan و Snapshot پروژهٔ باز را جایگزین می‌کرد. فایل‌های `guard-calibration.json` و `guard-transition.json` بدون تغییر از پوشهٔ `portable-modern-runtime` کپی می‌شدند و `light-state-profiles.json` هیچ‌گاه وارد قرارداد Pico نمی‌شد.

### Change

- Export مدرن هر شش پروفایل فعال و معتبر فعلی Classroom را دریافت می‌کند.
- Center، Tolerance و StableDuration در هر دو قرارداد `guard-calibration.json` و `guard-transition.json` نوشته می‌شوند.
- Revision جدید مشترک تولید می‌شود تا Loader اختلاف دو فایل را Fail-Closed تشخیص دهد.
- SHA256 هر دو فایل پس از تولید نهایی در Manifest بازسازی می‌شود.
- اگر یکی از شش پروفایل حذف، غیرفعال یا نامعتبر باشد، Export قبل از نوشتن روی Pico متوقف می‌شود.

### Validation

- تست رگرسیون مقادیر واقعی Game برابر `25.8 ± 0.5` را در هر دو قرارداد و Revision مشترک کنترل می‌کند.
- تست Manifest، Hash نهایی هر دو فایل پروفایل را با بایت‌های Exportشده تطبیق می‌دهد.

### Next test

با Classroom جدید پروژه را دوباره روی CIRCUITPY خروجی بگیرید. سپس روی درایو بررسی کنید `guard-calibration.json` برای Game مقدار `25.8` و `0.5` دارد. پس از Reboot، GP4 را در Game بزنید؛ Guard باید Start-at-current-state و سپس `ROUTE/start game_steps.txt` ثبت کند.

## Build 72 — کالیبراسیون واقعی صدا از مسیر Pico → Pro Micro

**Previous build:** 71
**Status:** CI candidate; sound hardware retest pending
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

لاگ واقعی نشان داد اتصال Bridge سالم بود، اما Pico برای هر دو فرمان `SCAL` و `WSND` پاسخ `ERR|UNKNOWN` می‌داد. Classroom این پاسخ نامعتبر را به‌اشتباه «quiet» تلقی کرد و Binary Search همیشه به سقف 200 و Threshold ثابت 300 رسید. هم‌زمان ACK دوره‌ای `OK|CURSOR` هر 250ms لاگ اپراتور را پر می‌کرد.

### Root cause

سنسور صدا روی Pro Micro است، اما Classroom به COM31 و Pico Brain متصل می‌شود. Host command handler پیکو فقط فرمان‌هایی مانند `PING/LUX/CURSOR` را می‌پذیرفت و `SCAL/WSND` را به UART خصوصی Pro Micro منتقل نمی‌کرد. کد کالیبراسیون نیز `ERR|UNKNOWN` را از Timeout واقعی تفکیک نمی‌کرد.

### Change

- Pico اکنون `SCAL` و `WSND` را با Timeout محدود به Pro Micro Proxy می‌کند و پاسخ واقعی را به Classroom برمی‌گرداند.
- Classroom فقط `ERR|TIMEOUT|WSND` را Quiet معتبر می‌داند؛ `UNKNOWN/EXEC` دیگر Threshold 300 تولید نمی‌کند.
- هنگام Bundle قدیمی، پیام روشن برای خروجی‌گرفتن مجدد روی CIRCUITPY نمایش داده می‌شود.
- ACK موفق `OK|CURSOR` از Serial Log مخفی شد؛ خطاهای Cursor همچنان ثبت می‌شوند.
- ARM 2.8.2، الگوریتم سنسور، Async Sound و Natural Mouse تغییر نکرده‌اند.

### Validation

- TestRunner قرارداد Proxy هر دو فرمان، تفکیک Timeout از Unknown و فیلتر Cursor ACK را قفل می‌کند.
- `code.py` با `py_compile` بررسی و SHA256 آن در Manifest بازسازی شد.

### Next test

با Classroom جدید پروژهٔ فعلی را دوباره روی CIRCUITPY خروجی بگیرید؛ صرفاً تعویض EXE کافی نیست. پس از Reboot و Connect، دکمهٔ کالیبره را در سکوت بزنید. پاسخ باید `OK|SCAL|avg=...|max=...` باشد و عدد Threshold از اندازه‌گیری واقعی بیاید.

## Build 71 — اتصال واقعی برای تست و کالیبراسیون سنسور صدا

**Previous build:** 70
**Status:** CI candidate; sound hardware retest pending
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

Classroom Studio در نوار وضعیت اتصال سبز نشان می‌داد، اما Bridge داخلی قبلاً قطع شده بود. کالیبراسیون `SCAL` با `Bridge is not connected` شکست می‌خورد و سپس همهٔ Probeهای `WSND` نیز همان لحظه و بدون تماس با سنسور به‌صورت `quiet` ثبت می‌شدند. این خطا مربوط به سنسور یا آستانهٔ 90 نبود.

### Root cause

`PythonBoardBridge` وضعیت واقعی را به `Disconnected` تغییر می‌داد، ولی `MainViewModel` به رویداد `StateChanged` متصل نبود. شرط اولیهٔ کالیبراسیون نیز فقط وضعیت نمایشی UI را کنترل می‌کرد، نه وضعیت واقعی Bridge.

### Change

- UI به `IBoardBridge.StateChanged` متصل شد و با قطع Sidecar/COM فوراً به حالت Connect برمی‌گردد.
- چراغ‌های Pico/Pro Micro و Cursor Sync هنگام قطع واقعی پاک می‌شوند.
- کالیبراسیون صدا پیش از `SCAL/WSND` وضعیت واقعی Bridge را بررسی می‌کند و به‌جای Probe جعلی، درخواست اتصال مجدد می‌دهد.
- Firmware، ARM 2.8.2، Runtime Pico، Natural Mouse و منطق Async Sound دست‌نخورده‌اند.

### Validation

- TestRunner وجود اتصال `StateChanged`، برگشت UI به Disconnected و Guard وضعیت واقعی پیش از کالیبراسیون را قفل می‌کند.
- رفتار مورد انتظار: پس از Fault، دکمه Connect نمایش داده می‌شود؛ کاربر یک‌بار Connect می‌زند و سپس همان دکمهٔ نمونه‌برداری صدا نقش تست مستقل سنسور را دارد.

### Next test

برنامه را باز کنید، Connect را بزنید و در Step «Wait For Sound» دکمهٔ نمونه‌برداری از سنسور صدا را اجرا کنید. ابتدا دو ثانیه سکوت و سپس صدای واقعی قلاب را آزمایش کنید؛ لاگ باید پاسخ `SCAL` یا Probe واقعی `WSND` را با فاصلهٔ زمانی نشان دهد، نه شش خط هم‌زمان.

## Build 70 — بازیابی امن LABEL/GOTO و Light Watch

**Previous build:** 69
**Status:** CI candidate; hardware retest pending
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

- Runner سبک Game با Route دارای `LABEL/GOTO` روی `unsupported Game command LABEL` متوقف شد.
- Classroom Studio پاسخ معتبر و کوتاه `OK|LUX|lux=...|sensor=ok` را رد می‌کرد.
- Light Watch پس از خطای WriteFile روی Handle قدیمی COM به Poll هر 250ms ادامه می‌داد.
- تلاش قبلی روی شاخهٔ آزمایشی، به‌علت ویرایش معیوب وب، متن کامل فایل‌ها را به خودشان چسباند؛ آن شاخه عمداً کنار گذاشته شد و هیچ بخشی از آن Cherry-pick نشد.

### Safe recovery

- اصلاحات از صفر روی `stable/natural-mouse-v1` و فایل‌های سالم با SHA مرجع بازسازی شدند.
- `plan_engine_game.py` اکنون نقشهٔ یکتای Label می‌سازد، `GOTO` را بدون Import Parser کامل اجرا می‌کند و Target ناموجود را Fail-Closed رد می‌کند.
- Parser نور هر دو قرارداد کامل شش‌بخشی و کوتاه چهاربخشی Pico را می‌پذیرد؛ Field تکراری، Sensor نامعتبر و Shapeهای دیگر همچنان رد می‌شوند.
- Light Watch پس از اولین Fault متوقف می‌شود و Bridge روی خطای `send/send_path` وضعیت اتصال را Disconnected می‌کند.
- Export مدرن پیش از کپی روی CIRCUITPY، Light Watch را متوقف می‌کند تا Auto-reload روی Handle باز رخ ندهد.
- Workflow، ARM 2.8.2، Async Sound، Natural Mouse v1 و Golden 100 دست‌نخورده مانده‌اند.

### Validation

- تست مستقل Game اجرای `LABEL → KEY A → GOTO → KEY C` و Skip شدن KEY B را کنترل می‌کند.
- TestRunner قرارداد Compact/Full Lux، توقف Watch، قطع Bridge و توقف Watch پیش از Deploy را قفل می‌کند.
- Manifest Helper بازی با Hash جدید بازسازی شده است.

## Build 69 — واکنش هم‌زمان Sound در حین حرکت Mouse

**Previous build:** 68
**Status:** CI candidate; hardware retest pending
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

در Build 68 مسیر Game بدون MemoryError و با حرکت نرم اجرا شد، اما Runner هنگام فعال‌بودن Mouse عمداً Poll صدا را عقب می‌انداخت. علت جلوگیری از تداخل فرمان Blocking `SCAL` با `MMOVE` و خطای `ERR|BUSY` بود. نتیجه این بود که کلید F فقط بعد از ایستادن Mouse اجرا می‌شد و Catch قلاب تأخیر داشت.

### Change

- Firmware به ARM 2.8.2 ارتقا یافت و فرمان‌های `ASND` و `ASNDCANCEL` اضافه شدند.
- Pro Micro هنگام اجرای Micro-stepهای Mouse، ورودی صوتی A0 را پیوسته و غیرمسدودکننده پایش می‌کند.
- با تشخیص صدا، Mouse در آخرین نقطهٔ ارسال‌شده فوراً متوقف می‌شود و رویداد `EVT|ASND|DETECTED` به Pico می‌رسد.
- Pico سپس کلید F را مستقل از Mouse HID می‌زند؛ MMOVEهای صف‌شده تا Arm بعدی صدا ACK و Drop می‌شوند تا قبل از F حرکت ادامه پیدا نکند.
- Firmware قدیمی همچنان از مسیر `SCAL` استفاده می‌کند، اما واکنش هم‌زمان فقط با ARM 2.8.2 فعال است.
- لایهٔ Legacy و بلااستفادهٔ `human_mouse_v3` از Firmware حذف شد تا فضای Flash برای Watcher جدید آزاد شود؛ مسیر Portable Relative بدون تغییر باقی ماند.

### Validation

- تست جدید ثابت می‌کند Sound poll در بازهٔ حرکت فعال انجام می‌شود و F حداکثر تا 40ms در شبیه‌سازی اجرا می‌گردد.
- پس از تشخیص، هیچ حرکت جدیدی بعد از F ثبت نمی‌شود.
- قرارداد Firmware وجود `ASND=1`، رویدادهای Async و Fallback قدیمی `SCAL` را هم‌زمان کنترل می‌کند.
- Hashهای Runtime و Game helper در Manifest بازسازی شدند.

### Next test

ابتدا Pro Micro را با ARM 2.8.2 و برد Arduino Leonardo فلش کنید و `ams_key.h` خصوصی فعلی را نگه دارید. سپس Bundle جدید Pico را بسازید و Route ماهیگیری را اجرا کنید. معیار پذیرش: حرکت Mouse نرم بماند، پس از صدای قلاب Mouse فوری متوقف و F بدون انتظار برای پایان مسیر اجرا شود، و `ERR|BUSY` یا MemoryError رخ ندهد.

## Build 68 — Runner سبک Game/Fishing و Stop عادی

**Previous build:** 67
**Status:** Hardware verified for Desktop/Login/DC/Game; Sound superseded by Build 69
**Commit:** `9c27b113`

### Problem observed

Build 67 مسیر Desktop و Login/DC را با `ROUTE|stage=light-route` کامل کرد و Pause/Resume نیز سالم بود. کالیبراسیون Game پس از یک Retry ذخیره شد. اما Route واقعی Game پس از Import و Parse موفق، با وجود 62,352 بایت آزاد، در Lazy-load مسیر اجرایی کامل با `MemoryError` خالی متوقف شد. Stop در Runner سبک نیز به‌اشتباه `RuntimeError('route aborted')` را به‌عنوان Guard Failure ثبت می‌کرد.

### Root cause

Game شامل `RPKG`، `LOOPTIME`، `PGROUP`، `WSND` و RMOUSE موازی است؛ Runner سبک Build 67 این ساختارها را نمی‌پذیرفت و بنابراین Parser/Executor/Parallel عمومی را روی Heap تکه‌تکه وارد می‌کرد. عدد حافظهٔ آزاد مجموع Heap بود، نه تضمین یک بلوک پیوسته برای Import/Compile ماژول بعدی.

### Change

- `plan_engine_game.py` با حجم کمتر از 10KB اضافه شد و بدون Import Parser یا Executor کامل، Route واقعی ماهیگیری را Streaming اجرا می‌کند.
- Random Package، Loop زمانی، Parallel Group، RMOUSE و WSND با همان قرارداد قبلی حفظ شدند.
- هنگام حرکت فعال Mouse، Sound poll برای جلوگیری از `ERR|BUSY` اجرا نمی‌شد؛ این محدودیت در سخت‌افزار تأخیر Catch ایجاد کرد و در Build 69 جایگزین شد.
- Stop در Runner سبک اکنون `ROUTE/aborted` عادی است و Guard Failure تولید نمی‌کند.
- Cacheهای Helper بعد از هر Route سبک نیز آزاد می‌شوند تا Start بعدی Heap تازه داشته باشد.
- Manifest و Exporter به Inventory 27فایلی و خروجی 31فایلی به‌روزرسانی شدند.

### Validation

- Route واقعی `game_steps.txt` از Bundle 161 در هر دو حالت Sound detected و Timeout شبیه‌سازی شد.
- حالت detected واکنش F را اجرا و شاخهٔ موس را لغو کرد؛ حالت Timeout بدون F پایان یافت و حرکت Streaming داشت.
- Runner جدید `plan_engine_parse` و `plan_engine_exec` را Import نمی‌کند؛ هر سه فایل Python با `py_compile` معتبرند.
- ماژول Game برابر 9.4KB و Helper Login برابر 11.8KB است.
- Hashهای `code.py`، Login helper و Game helper در Manifest جدید بازسازی شدند.

### Next test

تست سخت‌افزاری Bundle 164/166 تأیید کرد Game با `ROUTE|stage=light-route` و Heap کافی اجرا می‌شود، Pause/Resume و Stop سالم‌اند و MemoryError برنگشته است. تأخیر F هنگام حرکت به Build 69 منتقل شد.

## Build 67 — Runner سبک Streaming برای Login/DC

**Previous build:** 66
**Status:** Hardware verified for Desktop/Login/DC; Game superseded by Build 68
**Commit:** `47c992ee`


### Problem observed

Build 66 و Bundle 160 مسیر Desktop و Natural Mouse را کامل اجرا کردند، اما Route واقعی Login/DC پیش از Import Parser با `MemoryError` برای تخصیص 2930 بایت متوقف شد. حافظهٔ آزاد پیش از Import برابر 50620 بایت بود.

### Root cause

Route Login شامل `LABEL/GOTO`، دو `RMOUSE`، `TYPE` انسانی، `KEY` و `KDOWN/KUP` است. Runner سبک Build 66 این مجموعه را نمی‌پذیرفت و در نتیجه Parser کامل 29KB را روی Heap تکه‌تکه Import می‌کرد. بازگشت به Baseline نرم Build 50 اصلاحات مسیر سبک Login را همراه خود نیاورده بود.

### Change

- Module جدید `plan_engine_login.py` فقط منطق لازم Login را با حجم کمتر از 12KB فراهم می‌کند.
- `RMOUSE` همان Natural Mouse v1 و ARM 2.8.1 تأییدشده را حفظ می‌کند.
- `TYPE` شامل Typo/Correction تعدادمحور، Word/Punctuation/Think delay و متن نهایی دقیق است.
- `LABEL/GOTO`، `KEY`، `KDOWN/KUP` و Delayها بدون Import `plan_engine_parse.py` اجرا می‌شوند.
- متن Route پیش از Import Helper آزاد و `gc.collect()` اجرا می‌شود.
- Module جدید داخل Manifest قرار گرفته و پس از پایان Route همراه Cacheهای Plan آزاد می‌شود.

### Validation

- Route واقعی `p-updated-v3-fishing-parallel.amsj#LoginOrDc` با 19 فرمان روی Runner سبک شبیه‌سازی شد.
- هر دو RMOUSE در مجموع 130 نقطهٔ Streaming تولید کردند.
- پس از 3 تا 5 Typo/Correction، متن نهایی دقیقاً `zodiak999999` باقی ماند.
- KDOWN/KUP بدون کلید نگه‌داشته‌شده پایان یافت و Enter اجرا شد.
- `code.py` و `plan_engine_login.py` با `py_compile` معتبرند.
- TestRunner وجود Helper، نبود وابستگی به Parser و Manifest 26فایلی را کنترل می‌کند.
- ابزار Calibration heap نیز Inventory جدید 26فایلی را بدون تغییر رفتار کالیبراسیون بازسازی می‌کند.
- قرارداد شبیه‌سازی Calibration حضور `plan_engine_login.py` و هر 26 Hash را کنترل می‌کند.
- قرارداد Parallel/Exporter نیز ARM 2.8.1 ثابت، Helper سبک و Inventory 26فایلی را هم‌زمان قفل می‌کند.
- Workflow بسته‌بندی روی شاخهٔ پایدار `stable/natural-mouse-v1` فعال است.

### Next test

با ARM 2.8.1 بدون تغییر، Bundle جدید را از پروژهٔ کامل بسازید و Start را در Login/DC بزنید. معیار پذیرش: `ROUTE|stage=light-route` به‌جای `before-plan-engine-import`، اجرای TYPE و هر دو RMOUSE، پاسخ‌گویی Stop/Pause و نبود `MemoryError` یا کلید گیرکرده.

## Build 66 — Natural Mouse v1 روی Baseline نرم Build 50

**Previous build:** 50
**Status:** CI candidate; hardware retest pending
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

مسیرهای Batch نسخه‌های بعدی زمان هدف را بهتر نکردند و حرکت را به Burst/Gap تبدیل کردند. بازگشت آزمایشی نیز نشان داد ترکیب Runtime جدید با Firmware قدیمی Cadence اصلی Build 50 را بازتولید نمی‌کند. پروژهٔ C روی Build 50 دوباره حرکت نرم و پیوسته داد.

### Root cause

تلاش برای برابرکردن زمان اجرا با Hand Sample، بهینه‌سازی را از کیفیت Cadence دور کرد. Batch و Deadline pacing تعداد تراکنش‌ها را کاهش دادند اما فاصله‌های صفر و مکث‌های دوره‌ای ساختند. در مقابل، مسیر سبک Build 50 با ARM 2.8.1 حرکت را پیوسته نگه می‌دارد؛ بنابراین Humanization باید در سطح شکل و زمان کل مسیر انجام شود، نه در لایهٔ HID.

### Change

- Cadence، Micro-step و ARM 2.8.1 دست‌نخورده باقی ماندند.
- مدت حرکت با فاصله مقیاس می‌شود: حرکت کوتاه سریع‌تر، متوسط نزدیک بازهٔ تنظیم‌شده و بلند آهسته‌تر است.
- نقطهٔ اوج سرعت در هر حرکت کمی جلو یا عقب می‌رود؛ Endpoint و مجموع زمان حفظ می‌شوند.
- مکث میان‌مسیر فقط برای حرکت حداقل 300px مجاز است.
- Overshoot فقط برای حرکت حداقل 300px، به اندازهٔ 2–6px و با یک اصلاح 70–160ms انجام می‌شود.
- پروژهٔ `mouse-tune-C-natural-v1.amsj` با احتمال مکث 4٪، Overshoot شش‌درصدی و Curve برابر 3–22٪ اضافه شد.

### Validation

- Runner سبک روی 200 Seed بدون مسیر مطلق، بدون Import موتور کامل و با حداکثر 128 نقطه موفق شد.
- تست فاصله ثابت کرد زمان حرکت کوتاه، متوسط و بلند به‌ترتیب افزایش می‌یابد.
- تست C# تأیید می‌کند حرکت کوتاه Overshoot ندارد و حرکت بلند پس از Overshoot دقیقاً به Endpoint برمی‌گردد.
- قراردادهای قدیمی مدت حرکت با Target جدید وابسته به فاصله همگام شدند.
- Hashهای Runtime مدرن در Manifest بازسازی شدند.
- Workflow رسمی برای Branch آزمایشی فعال شد تا بستهٔ Windows و TestRunner روی GitHub بررسی شوند.

### Next test

ARM 2.8.1 حفظ شود، پروژهٔ `mouse-tune-C-natural-v1.amsj` با Build آزمایشی اجرا و Guard log و Record ارسال شود. معیار پذیرش: نرمی فعلی C حفظ شود، وقفه‌های فنی دوره‌ای برنگردند، Endpoint دقیق بماند و تنها تنوع سطح مسیر افزایش یابد.

## Build {{BUILD_NUMBER}} — Changelog اجباری و تأیید سخت‌افزاری A

**Previous build:** 49
**Status:** CI candidate; Build 49 hardware result recorded
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

اطلاعات علت شکست و اصلاح هر Build در PRها و صفحهٔ داخلی پراکنده بود. متن Release نیز برای همهٔ Buildها یک متن عمومی تکراری داشت؛ بنابراین توسعه‌دهندهٔ بعدی نمی‌توانست وضعیت فعلی را سریع بفهمد.

### Root cause

Workflow انتشار متن Release را به‌صورت ثابت تولید می‌کرد و هیچ بررسی‌ای وجود نداشت که تغییرات Build همراه با ورودی Changelog باشند.

### Change

- این فایل به‌عنوان مرجع تجمعی وضعیت سخت‌افزاری اضافه شد.
- README مستقیماً به این سند و آخرین Release اشاره می‌کند.
- Workflow برای هر Build بخش نخست این فایل را به‌عنوان Release notes استخراج می‌کند.
- هر Commit مؤثر بر Build باید همین Changelog را تغییر دهد؛ در غیر این صورت Job بسته‌بندی Fail می‌شود.
- نتیجهٔ سخت‌افزاری Build 49 و آمار Record آزمون A ثبت شد.

### Validation

- لاگ Build 49: `after-plan-engine-import=68880` و `after-plan-parse=68512`.
- Route `desktop_steps.txt` تا `ROUTE/complete` اجرا شد و `MemoryError` رخ نداد.
- Record آزمون A: 1,928 موقعیت، 1,927 Segment، گام میانه 2.00px، صدک 95 گام 2.24px و بیشینه 2.83px.
- فاصلهٔ زمانی میانه 2ms بود، اما صدک 80 برابر 52ms، صدک 95 برابر 53ms و 862 فاصلهٔ حداقل 40ms ثبت شد؛ بنابراین اندازهٔ Micro-step صحیح است ولی نرمی زمانی هنوز نیازمند مقایسهٔ B/C/D است.

### Next test

پروژه‌های B، C و D را با Build 49 جداگانه اجرا و Record هرکدام را ثبت کنید. معیار انتخاب: کاهش فاصله‌های 40ms به بالا، حفظ Micro-step حداکثر سه پیکسل و نبودن `MemoryError`.

## Build 49 — Runner سبک RMOUSE

**Previous build:** 48
**Status:** Hardware functional pass; motion quality pending
**PR/Commit:** [#16](https://github.com/noonoix/smz/pull/16) / [`2b1919dd`](https://github.com/noonoix/smz/commit/2b1919dda4082d89531723cff82876ba97a69437)
**Release:** [classroom-current-49](https://github.com/noonoix/smz/releases/tag/classroom-current-49)
**SHA256:** `2dc6f75b1c84eafd8ebcccb3ba67c99a8d9a222aacb159533879d3f1b23a56a8`

### Problem observed

Build 48 Parser را با حاشیهٔ خوب Load می‌کرد، اما `run_plan` برای Route سادهٔ A موتور کامل 19.9KB را Import می‌کرد و تخصیص 1,016 بایت شکست می‌خورد. اجرای دوم پس از Fragmentation در تخصیص 776 بایت شکست خورد.

### Root cause

Route سادهٔ `RMOUSE + LOOP + DELAY` بی‌دلیل هزینهٔ Import ماژول‌های Human، Parallel و Executor کامل را می‌پرداخت.

### Change

Runner سبک فقط برای Relative Native و مجموعهٔ محدود `PLAN/SCREEN/SPEED/DELAY/LOOP/LOOPTIME/ENDLOOP/RMOUSE` اضافه شد. Routeهای پیچیده همچنان به موتور کامل می‌روند؛ Fishing و Parallel تغییر نکردند.

### Validation

خروجی Runner سبک و موتور کامل روی 100 Seed برابر بود؛ RMOUSE روی 200 Seed و Parallel روی 17 سناریو موفق شد. تست واقعی A به `ROUTE/complete` رسید.

### Next test

B/C/D برای انتخاب Tempo و Curve مناسب مقایسه شوند.

## Build 48 — جداسازی Import Parser از Executor

**Previous build:** 47
**Status:** Partial hardware pass; superseded by 49
**PR/Commit:** [#15](https://github.com/noonoix/smz/pull/15) / [`3f2a03d7`](https://github.com/noonoix/smz/commit/3f2a03d77281825b093ac132074238bad437b5eb)
**Release:** [classroom-current-48](https://github.com/noonoix/smz/releases/tag/classroom-current-48)
**SHA256:** `048044fdf71dfc8fda0469508e15e59287e30f873e9d09c7e0f9a211fd334bec`

### Problem observed

Build 47 هنگام Import هم‌زمان Parser، Human و Executor با تخصیص 2,344 بایت شکست خورد.

### Root cause

Facade هنگام درخواست اولیهٔ `parse_plan` همهٔ ماژول‌های اجرایی را نیز Import می‌کرد.

### Change

Parser زودهنگام و Executor داخل `run_plan` به‌صورت Lazy بارگذاری شد.

### Validation

در سخت‌افزار Import با 71,328 بایت و Parse با 70,960 بایت آزاد کامل شد؛ شکست بعدی فقط Import Executor بود و در Build 49 رفع شد.

## Build 47 — Fishing timeout و Cadence موس

**Previous build:** 46
**Status:** Fishing semantics fixed; A import failed
**PR/Commit:** [#14](https://github.com/noonoix/smz/pull/14) / [`d324a5b8`](https://github.com/noonoix/smz/commit/d324a5b8afa61db8114f3c1e83a2fcd94980e10d)
**Release:** [classroom-current-47](https://github.com/noonoix/smz/releases/tag/classroom-current-47)
**SHA256:** `efe23c93ff45575b485ec0bdd1fda60f40f3c617f6c7194f2bb4111b4c6483f2`

### Problem observed

پس از Timeout صدای ماهیگیری، شاخهٔ Sound پایان می‌یافت اما Loop بی‌نهایت موس فعال می‌ماند؛ در نتیجه قلاب مجدد اجرا نمی‌شد. حرکت نیز بین نقاط Pico وقفه‌های حدود 40–55ms داشت.

### Root cause

Timeout کل Parallel Group را لغو نمی‌کرد و مسیر Streaming فقط تا 32 نقطهٔ زمانی داشت.

### Change

Timeout کل گروه را لغو می‌کند، واکنش `F` را رد می‌کند و به Loop بیرونی برمی‌گردد. Cadence هدف 8ms و سقف Streaming برابر 128 نقطه شد.

### Validation

تست‌های Parallel 17/17 و Relative RMOUSE روی 200 Seed موفق شدند. تست A بعداً فشار Import مستقل را آشکار کرد.

## Build 46 — Streaming RMOUSE عادی Login/DC

**Previous build:** 45
**Status:** Hardware verified
**PR/Commit:** [#13](https://github.com/noonoix/smz/pull/13) / [`946f736b`](https://github.com/noonoix/smz/commit/946f736bad76b65250a31faa33b8d299cbeef5cc)
**Release:** [classroom-current-46](https://github.com/noonoix/smz/releases/tag/classroom-current-46)
**SHA256:** `04febadd9d9b0d8e387f71d3b085e7b2ac81cbfd1f5f609231b055f50c84d812`

### Problem observed

Login/DC پس از Parse و `SCREEN` در نخستین RMOUSE با تخصیص 2,048 بایت شکست می‌خورد.

### Root cause

RMOUSE غیرموازی هنوز چند لیست متراکم WindMouse را در RAM می‌ساخت؛ اصلاح Streaming فقط در Parallel فعال بود.

### Change

RMOUSE نسبی عادی نیز Streaming شد و متن Route پیش از نخستین حرکت آزاد شد.

### Validation

تست سخت‌افزاری Login/DC با 51,776 بایت و Game با 49,120 بایت پس از Parse موفق شد؛ Stop به‌صورت `ROUTE/aborted` ثبت شد.

## Build 45 — حذف SCAL وسط حرکت و بازیابی Heap

**Status:** Verified foundation
**Commits:** [`1bc0c5c7`](https://github.com/noonoix/smz/commit/1bc0c5c78e2c73fa7e255963955066fdb2d2da69)، [`894bad14`](https://github.com/noonoix/smz/commit/894bad14738601b87dbf7ff91b5f01096c55ab9d)
**Release:** [classroom-current-45](https://github.com/noonoix/smz/releases/tag/classroom-current-45)
**SHA256:** `59e5056286be6000c24695e013974d023c81d68f975efb059ae514d3d9915eaf`

- Poll صوتی `SCAL|10` هنگام Stream فعال موس اجرا نمی‌شود و به نقاط توقف عمدی منتقل شد.
- `PlanAbort` ناشی از Stop دیگر Guard Failure نیست.
- ماژول‌های Plan پس از Route از Cache خارج می‌شوند تا Start بعدی Heap تازه داشته باشد.

## Build 43 — Pause، SCAL BUSY و سرعت Sample

**Status:** Superseded but retained behavior
**Commits:** [`58fff88d`](https://github.com/noonoix/smz/commit/58fff88dcf9e0a3c33a3addafca161590a46a883)، [`55d4b081`](https://github.com/noonoix/smz/commit/55d4b0818e64b259e7a2c6582b1dfb9f871fc78a)
**Release:** [classroom-current-43](https://github.com/noonoix/smz/releases/tag/classroom-current-43)
**SHA256:** `fbde9312b55de60e69324d1dedc9235ec2c2a7ef560940b97b2c107054a1b82b`

- Pause فوراً `keyboard.release_all()` می‌کند.
- پیش از SCAL صف ARM Flush و BUSY گذرا Retry می‌شود.
- Hand Sample منبع Tempo است و هزینهٔ Micro-step دوباره به زمان حرکت افزوده نمی‌شود.

## Build 41 — Parallel streaming و پروفایل نور

**Status:** Superseded
**Commit:** [`13277a93`](https://github.com/noonoix/smz/commit/13277a935b3f0520c045fa12cb11fd658ee9bf01)
**Release:** [classroom-current-41](https://github.com/noonoix/smz/releases/tag/classroom-current-41)
**SHA256:** `0b0677c9a3c1d045f1aacf299be7360fce7042be69e3e83765c7e468afb999d4`

Parallel RMOUSE از لیست متراکم به Curve Streaming منتقل شد و Parsing پاسخ SCAL کم‌Allocation شد. پروفایل‌های Character Dashboard و Game تثبیت شدند.

## Build 40 — Retry کالیبراسیون پس از Overlap

**Status:** Verified
**Commit:** [`034e46c8`](https://github.com/noonoix/smz/commit/034e46c8a175c15804c4b2861c722d86063ff0ec)
**Release:** [classroom-current-40](https://github.com/noonoix/smz/releases/tag/classroom-current-40)
**SHA256:** `a281f4ad03432f5828345ac5197581adc0930d3cf40d96a36b9446246d86bf64`

پس از رد `CAL|OVERLAP`، زرد نمونه‌گیری تازه را آغاز می‌کند؛ آبی کوتاه Stage نامعتبر را رد نمی‌کند و آبی بلند خارج می‌شود. بازخورد صوتی خطا اضافه شد.

## Build 39 — حفظ Facade مدرن در Export پروژهٔ جاری

**Status:** Verified foundation
**Commit:** [`7efce2da`](https://github.com/noonoix/smz/commit/7efce2da545648f17a102afd5a148f596c92bfa6)
**Release:** [classroom-current-39](https://github.com/noonoix/smz/releases/tag/classroom-current-39)
**SHA256:** `ca5ea9c0175ec76bc1271567f0c9c83caf382aede25d3ee885c77a0eb6221369`

Exporter Legacy پس از تولید Routeها، Facade مدرن را با Engine یکپارچهٔ 30KB جایگزین می‌کرد. ترتیب Export اصلاح و Hash نهایی Manifest دوباره ساخته شد.

## Build 38 — Split اولیهٔ Executor و Parallel

**Status:** Superseded by 39
**Commits:** [`c1ad2385`](https://github.com/noonoix/smz/commit/c1ad2385333f0a46f2c945620e5a865bc6f8630d)، [`dfe4102a`](https://github.com/noonoix/smz/commit/dfe4102a421807087a00910a6918c2a2bbe73964)، [`eaa65f96`](https://github.com/noonoix/smz/commit/eaa65f969fd2abd430c04846e1f2ba1ac18fffec)
**Release:** [classroom-current-38](https://github.com/noonoix/smz/releases/tag/classroom-current-38)
**SHA256:** `af7af2a700d17be4254248642ddc883283e105053ce1fd2b28b634f1eafd64b7`

Scheduler Parallel از Executor جدا و Lazy-load شد. Export اشتباه Facade در Build 39 اصلاح شد.

## قانون به‌روزرسانی

برای هر تغییر Build‌ساز:

1. بخش `Build {{BUILD_NUMBER}}` فعلی را با شمارهٔ واقعی Build قبلی تثبیت کنید.
2. یک بخش جدید `Build {{BUILD_NUMBER}}` در بالای تاریخچه اضافه کنید.
3. Problem، Root cause، Change، Validation و Next test را تکمیل کنید.
4. وضعیت Hardware را از CI جدا نگه دارید؛ CI سبز به‌تنهایی به معنی تأیید سخت‌افزاری نیست.
5. Workflow بدون تغییر همین فایل اجازهٔ انتشار Build جدید را نمی‌دهد.
