# Classroom Studio — Current Hardware Changelog

این سند مرجع سریع وضعیت شاخهٔ فعال `fix/portable-relative-mouse` است. ترتیب ورودی‌ها معکوس زمانی است؛ جدیدترین Build همیشه بالاتر قرار می‌گیرد.

## وضعیت فعلی در یک نگاه

- **آخرین Build دارای تست سخت‌افزاری:** Build 60 با ARM 2.8.6 و Bundle 151
- **وضعیت حافظه:** Route سبک با 77,680 بایت آزاد کامل شد و MemoryError، ACK timeout یا Guard Failure رخ نداد.
- **وضعیت کیفیت حرکت:** زمان فعال 16.770 ثانیه بود، اما 67.3٪ فاصله‌ها صفر و 43 وقفه حداقل 40ms بودند؛ Batch فعلی Burst/Gap قابل‌احساس می‌سازد.
- **معماری:** Pico مسئول Keyboard/Guard/Route، و Pro Micro مسئول Mouse HID و Sound است.
- **Golden 100:** جدا و بدون تغییر باقی مانده است.

| Build | نتیجهٔ سخت‌افزاری | مسئله/تغییر اصلی | وضعیت |
| --- | --- | --- | --- |
| {{BUILD_NUMBER}} | در انتظار تست سخت‌افزاری | ARM 2.8.7؛ پخش Micro-step روی Deadline مطلق | CI candidate |
| 60 | Route کامل؛ 16.770s؛ مقصد دقیق | Batch پنج‌تایی CKSUM را رفع کرد، اما Burst/Gap باقی ماند | Hardware functional; tempo/quality failed |
| 59 | HVER صحیح؛ Route با ERR\|CKSUM متوقف شد | Frame شش‌تایی 68B از RX 64B بزرگ‌تر بود | Hardware failed; superseded |
| 58 | مسیر کامل؛ 17.332s؛ Bundle سالم | ARM 2.8.4 پروتکل کوتاه MR | Tempo failed; superseded |
| 57 | مسیر کامل؛ 17.595s؛ Bundle سالم | ARM 2.8.3 DDA حداقل امن و Flush کمتر | Tempo improved; superseded |
| 56 | مسیر کامل؛ 19.921s؛ مقصد دقیق | ARM 2.8.2 حذف sleep صریح | Tempo failed; superseded |
| 55 | مسیر کامل؛ مقصد دقیق؛ 21.495s | deadline-based HANDPATH؛ محدودکنندهٔ ARM آشکار شد | Tempo failed; superseded |
| 52 | مسیر کامل؛ مقصد دقیق؛ 21.704s | Chunk کم‌حافظهٔ HANDPATH | Memory pass؛ tempo superseded |
| 51 | MemoryError پیش از Import در Route 8.7KB | بازهٔ تصادفی زمان بازپخش Hand Sample | Superseded by next build |
| 50 | A و B کامل؛ Record تست C تحلیل شد | Changelog اجباری؛ همان Runtime Build 49 | C انسانی‌ترین؛ B نرم‌ترین |
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

## Build {{BUILD_NUMBER}} — ARM 2.8.7 با Deadline pacing

**Previous build:** 60
**Status:** CI candidate; hardware retest pending
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

Build 60 و ARM 2.8.6 خطای CKSUM را رفع کردند و Route کامل شد، اما حرکت به‌صورت Burstهای سریع و Gapهای قابل‌احساس اجرا شد.

### Evidence

- Record شامل 5,415 موقعیت و 5,414 Segment بود.
- زمان فعال 16.770s شد؛ فقط 3.2٪ بهتر از 17.332s در Build 58 و همچنان خارج از هدف 9–11s.
- 3,645 فاصله، برابر 67.3٪، صفر میلی‌ثانیه بودند.
- 43 وقفه حداقل 40ms، 11 وقفه حداقل 60ms و بیشینه 91ms ثبت شد.
- مقصد دقیق ماند: `(934,499)` تا `(1141,621)`، یعنی Delta برابر `(207,122)`.
- Plan شامل 1,176 Delta و 10.001s Timing است؛ DDA برای آن حدود 5,417 گزارش HID می‌سازد.

### Root cause

MB تا Deadline کامل هر Delta صبر می‌کرد و سپس تمام Micro-stepهای DDA همان Delta را با `paceMs=0` پشت‌سرهم می‌فرستاد. کاهش Ack زمان Wire را کم کرد، اما Cadence را به «انتظار سپس Burst» تبدیل کرد.

### Change

- ARM به نسخهٔ 2.8.7 ارتقا یافت.
- Micro-stepهای هر رکورد MB روی بازهٔ مطلق میکروثانیه‌ای همان رکورد پخش می‌شوند.
- کف یک‌میلی‌ثانیه‌ای به‌صورت Deadline اعمال می‌شود، نه `delay(1)` ثابت؛ زمان مصرف‌شده در `USB_Send` جزو فاصله حساب می‌شود.
- اگر Delta متراکم از بودجهٔ نمونه جلو بزند، بدهی Deadline به رکورد بعدی منتقل می‌شود و زمان‌بندی از نو به Burst تبدیل نمی‌شود.
- سقف سه‌پیکسل، Delta و Endpoint دقیق، ترتیب گزارش‌ها، Batch پنج‌تایی 58 بایتی، checksum و MR/MMOVE fallback حفظ شده‌اند.
- Release فایل `ARM-2.8.7-source.zip` را منتشر می‌کند.

### Validation

- شبیه‌ساز قرارداد، فاصلهٔ حداقل 1ms میان Deadline گزارش‌ها، Delta دقیق، سقف سه‌پیکسل و انتقال بدهی از Delta متراکم را کنترل می‌کند.
- تست‌های Exhaustive کامل `-127..127` برای Endpoint و سقف گزارش بدون تغییر باقی مانده‌اند.
- تمام قراردادهای Portable، Hashها، Windows TestRunner، ARM compile، Plan2، Golden و Security باید پیش از Merge سبز باشند.

### Next test

ARM 2.8.7 را فلش، Build جدید را اجرا و Bundle را از همان `11hand3.amsj` بازسازی کن. Guard باید HVER نسخهٔ 2.8.7 را نشان دهد. معیار اصلی کاهش شدید فاصله‌های صفر و وقفه‌های حداقل 40ms، حفظ Endpoint `(207,122)`، نبود CKSUM و نزدیک‌شدن زمان فعال به 9–11s است.

## Build 60 — ARM 2.8.6 با Batch پنج‌تایی

**Previous build:** 59
**Status:** hardware route passed; tempo/quality failed; superseded by ARM 2.8.7
**Commit:** `06558a72a7d846054d4117a526625b1fa3a34f79`

### Problem observed

Build 59 و Bundle 150 قابلیت `MB=1` را صحیح مذاکره کردند و HVER روی COM31 دیده شد، اما Route در میانهٔ HANDPATH با `ARM MB rejected: ERR|CKSUM` متوقف شد.

### Evidence

- هر 25 Hash Bundle 150 معتبر بود و MemoryError رخ نداد.
- حافظهٔ آزاد light-route برابر 77,664 بایت بود.
- بافر RX استاندارد HardwareSerial روی AVR برابر 64 بایت است.
- Frame شش‌تایی MB برابر 68 بایت بود: چهار بایت `#XX|`، سه بایت `MB|`، شصت کاراکتر Hex و newline.
- Record با 4,537 موقعیت ناقص ماند و برای Tempo یا Endpoint نهایی معتبر نیست.

### Root cause

Frame شش‌تایی چهار بایت از بافر RX سخت‌افزاری بزرگ‌تر بود. هنگام پردازش طولانی DDA، بخشی از Frame بعدی در Ring Buffer 64 بایتی جا نمی‌شد و Checksum عمداً Frame ناقص را رد می‌کرد.

### Change

- ARM به نسخهٔ 2.8.6 ارتقا یافت.
- سقف Batch از شش به پنج رکورد کاهش یافت.
- Decoder نیز حداکثر 25 بایت Payload، دقیقاً پنج رکورد، می‌پذیرد.
- Frame پنج‌تایی کامل 58 بایت است و شش بایت حاشیه زیر RX 64-byte دارد.
- checksum، Deadline مطلق، Timing، Delta علامت‌دار، DDA سه‌پیکسلی، Stop Gate و MR/MMOVE fallback تغییر نکرده‌اند.
- Release فایل `ARM-2.8.6-source.zip` را منتشر می‌کند.

### Validation

- تست قراردادی اندازهٔ Frame الزام `<=64` را قفل می‌کند.
- پنج Delta در یک Frame هنوز تعداد ACK را برای Plan 1,176-Segment حدود 80٪ کاهش می‌دهد.
- تمام 37 قرارداد Portable، 25 Hash، Windows TestRunner، ARM compile، Plan2، Golden و Security باید پیش از Merge سبز باشند.

### Next test

ARM 2.8.6 را فلش، Build جدید را اجرا و Bundle را از همان `11hand3.amsj` بازسازی کن. Guard باید HVER نسخهٔ 2.8.6 را نشان دهد، `ERR|CKSUM` نباید تکرار شود و Route باید کامل شود. Guard، Record و Bundle را برای سنجش Tempo ارسال کن.

### Hardware result

Bundle 151 و ARM 2.8.6 Route را بدون CKSUM، MemoryError یا ACK timeout کامل کردند. زمان فعال 16.770s و مقصد `(207,122)` دقیق بود، اما 67.3٪ فاصله‌ها صفر، 43 وقفه حداقل 40ms و بیشینه وقفه 91ms بود. علت، صبر کامل هر رکورد MB و سپس اجرای Burst همهٔ Micro-stepهای آن رکورد با `paceMs=0` بود.

## Build 59 — ARM 2.8.5 با Batch محدود HANDPATH

**Previous build:** 58
**Status:** hardware CKSUM failed; superseded by ARM 2.8.6
**Commit:** `cf6ec8c371fb1a490b520fa0d2396fb99bb14277`

### Problem observed

Build 58 با ARM 2.8.4 و Bundle 148 کامل شد، اما پروتکل کوتاه MR زمان فعال را فقط از 17.595 به 17.332 ثانیه رساند؛ بهبود 263ms یا 1.5٪ بود و هدف 9–11 ثانیه همچنان پاس نشد.

### Evidence

- هر 25 Hash معتبر بود؛ Plan شامل 1,176 Segment، زمان منبع 10.001s و بازهٔ هدف 9–11s است.
- Record شامل 5,416 موقعیت و 5,415 گزارش قابل مشاهده بود.
- Delta نهایی `(207,122)` بدون clipping، گام میانه 2.24px و بیشینه دقیقاً 3px بود.
- فقط 11 فاصلهٔ فعال حداقل 40ms و هیچ فاصلهٔ 100ms یا بیشتر ثبت نشد.
- HVER در COM31 دیده نشد، چون Runtime آن را با `print()` به Console می‌فرستاد، نه Data channel مانیتور Guard.

### Root cause

کاهش 34٪ طول فرمان متنی کافی نبود. مسیر هنوز برای هر یک از 1,176 Segment یک فرمان، Parse و Ack جدا داشت و ARM نیز در مجموع 5,415 گزارش HID تولید می‌کرد.

### Change

- ARM به نسخهٔ 2.8.5 ارتقا یافت و `HVER` قابلیت `MB=1` را اعلام می‌کند.
- HANDPATH در فریم‌های checksumدار حداکثر شش Delta و حداکثر 64ms زمان هدف ارسال می‌شود.
- هر رکورد پنج‌بایتی Hex شامل Delay، dx و dy علامت‌دار است؛ یک `OK|MB` جای Ackهای جداگانه را می‌گیرد.
- ARM داخل هر Batch از Deadline مطلق استفاده می‌کند تا هزینهٔ USB دوباره روی Timing نمونه جمع نشود.
- هر Batch حداکثر 64ms است و Pico بین Batchها Gate دکمه‌ها را بررسی می‌کند.
- ARMهای بدون `MB=1` خودکار به MR یا MMOVE قبلی برمی‌گردند.
- پاسخ HVER اکنون با Debug event رسمی روی کانال Data ثبت می‌شود.
- MMOVE مطلق/Stream قدیمی با `ERR|ABS` Fail-Closed شد؛ معماری Portable مبدأ مطلق قابل اعتماد ندارد. Relative MR/MMOVE، HMOVE صریح، Drag، Click، Wheel و Sound حفظ شدند.
- Release فایل `ARM-2.8.5-source.zip` را منتشر می‌کند.

### Validation

- شش Delta نمونه با 74 بایت Wire منتقل می‌شوند؛ شش MR جدا 138 بایت بودند، یعنی حدود 46٪ کاهش بیشتر.
- Batch دقیقاً ترتیب، Timing و Delta علامت‌دار را نگه می‌دارد؛ سقف DDA سه‌پیکسلی تغییر نکرده است.
- کامپایل محلی Leonardo برابر 27,600 از 28,672 بایت Flash و 2,148 از 2,560 بایت RAM است.
- قراردادهای fallback، Batch حداکثر شش‌تایی/64ms، Endpoint دقیق و Fail-Closed قفل شده‌اند.

### Hardware result

Bundle 150 هر 25 Hash معتبر داشت و HVER نسخهٔ 2.8.5 با `MB=1` روی COM31 ثبت شد. حافظهٔ light-route برابر 77,664 بایت بود، اما Frame شش‌تایی 68 بایتی از RX 64-byte AVR عبور کرد و Route با `ERR|CKSUM` متوقف شد. نتیجهٔ Record ناقص است و برای Tempo معتبر نیست.

## Build 58 — ARM 2.8.4 با پروتکل کوتاه MR

**Previous build:** 57
**Status:** hardware route passed; tempo failed; superseded by ARM 2.8.5
**Commit:** `86966a1b5f8015ce51e8b7870fcf28afc31f7a9e`

### Problem observed

Build 57 با ARM 2.8.3 زمان فعال را از 19.921 به 17.595 ثانیه کاهش داد، اما بازپخش Route هدف 9–11 ثانیه هنوز 6.6 تا 8.6 ثانیه کندتر بود.

### Evidence

- Bundle 145 هر 25 Hash معتبر، 1,176 Segment، زمان منبع 10.001s و بازهٔ دقیق 9–11s داشت.
- Record شامل 5,416 موقعیت بود؛ گام میانه 2.24px و صدک 95 برابر 3px ثبت شد.
- فقط 12 وقفهٔ حداقل 40ms و هیچ وقفهٔ فعال 100ms وجود نداشت.
- Cursor به لبهٔ راست صفحه رسید؛ بنابراین Delta نهایی Record برای سنجش مقصد معتبر نبود و تست بعدی باید از مرکز صفحه شروع شود.

### Root cause

برای هر Segment، لینک 57,600 baud همچنان قاب متنی طولانی `#XX|MMOVE|dx,dy,rel,2` و پاسخ `OK|MMOVE` را منتقل می‌کرد. با 1,176 Segment، حجم فرمان/Ack اجازه نمی‌داد Pacing هدف با USB reports هم‌پوشانی کافی داشته باشد.

### Change

- ARM به نسخهٔ 2.8.4 ارتقا یافت و در HVER قابلیت `MR=1` را اعلام می‌کند.
- Pico در صورت وجود Capability از فرمان کوتاه `MR|dx,dy` و پاسخ `OK|MR` استفاده می‌کند.
- قاب Checksum، صف دوفرمانی، DDA امن و سقف سه‌پیکسلی حفظ می‌شوند.
- ARMهای قدیمی بدون `MR=1` به‌طور خودکار همان MMOVE Legacy را دریافت می‌کنند.
- قابلیت اختیاری و Bridge-only فرمان `HSETCUR` از Firmware 2.8.4 حذف شد؛ مسیر Portable هیچ‌وقت آن را فراخوانی نمی‌کند و حرکت مطلق همچنان Fail-Closed است.
- لاگ نسخه با قالب استاندارد Timestamp چاپ می‌شود.
- Release فایل `ARM-2.8.4-source.zip` را منتشر می‌کند.

### Validation

- حجم نمونهٔ Wire از 35 به 23 بایت، حدود 34 درصد، کاهش یافت.
- تست Exhaustive جمع دقیق Delta و سقف سه‌پیکسلی را برای همهٔ بردارهای `-127..127` حفظ می‌کند.
- قرارداد Capability، fallback Legacy و هر دو Ack در Runtime قفل شده‌اند.
- هر 37 قرارداد Portable و Manifest بیست‌وپنج‌فایلی باید پیش از Merge سبز باشند.

### Hardware result

Bundle 148 هر 25 Hash معتبر داشت. Route بدون MemoryError و ACK timeout کامل شد. زمان فعال 17.332s، مکث انتهایی 2.141s و کل Record برابر 19.473s بود. Delta نهایی `(207,122)` بدون clipping ثبت شد؛ گام میانه 2.24px و بیشینه 3px بود. MR فقط 1.5٪ نسبت به Build 57 بهبود داد، بنابراین Batch محدود به‌عنوان گام بعدی انتخاب شد.

## Build 57 — ARM 2.8.3 با Throughput بیشتر

**Previous build:** 56
**Status:** hardware tempo improved but failed; superseded by ARM 2.8.4
**Commit:** `a2279251327fc67ca1cf9a6511ea8fde681b4515`

### Problem observed

تست واقعی Build 56 با ARM 2.8.2 کامل شد، اما Record برای بازهٔ هدف 9–11 ثانیه، 19.921 ثانیه زمان فعال ثبت کرد. Guard نیز Route را حدود 21 ثانیه نشان داد.

### Evidence

- 5,842 موقعیت و 5,841 Segment ثبت شد.
- Delta نهایی `(207,122)`، گام میانه 2px و بیشینه 2.83px بود.
- فقط 9 وقفهٔ حداقل 40ms و هیچ وقفهٔ فعال حداقل 100ms وجود نداشت.
- حذف sleep نسخهٔ 2.8.2 اثر داشت، اما نسبت به 21.495 ثانیه فقط 7.3 درصد بهبود ایجاد کرد.

### Root cause

ARM برای بیشتر Deltaهای Hand Sample حدود پنج گزارش HID تولید می‌کرد و پس از هر فرمان `OK|MMOVE` را با Flush مسدودکنندهٔ UART می‌فرستاد. صف دوفرمانی Pico نمی‌توانست زمان USB reports و پایان فیزیکی هر Ack را به‌اندازهٔ کافی هم‌پوشان کند.

### Change

- ARM به نسخهٔ 2.8.3 ارتقا یافت.
- DDA کمترین تعداد گزارش امن را انتخاب می‌کند: حرکت تک‌محور تا 3px و حرکت ترکیبی حداکثر `(2,2)` یا 2.83px است.
- جمع Delta، ترتیب Segmentها و مقصد دقیقاً حفظ می‌شوند.
- پاسخ‌های لینک داخلی در بافر محدود UART قرار می‌گیرند؛ خود <code>println</code> هنگام پُرشدن بافر Back-pressure می‌دهد و Flush اضافی حذف شده است.
- نخستین پاسخ HVER با `EVT|DEBUG|ARM|...` در Guard log ثبت می‌شود.
- Release فایل مستقیم `ARM-2.8.3-source.zip` و Hash آن را منتشر می‌کند.

### Validation

- پیمایش Exhaustive همهٔ Deltaهای `-127..127` جمع دقیق و سقف حداکثر سه‌پیکسلی را تأیید می‌کند.
- تعداد گزارش‌های Exhaustive نسبت به ARM 2.8.2 از 3,173,660 به 2,774,528 کاهش یافت.
- قرارداد Runtime چاپ نسخهٔ واقعی HVER را قفل می‌کند.
- Workflow AutoCycle کامپایل Leonardo/ATmega32U4 را قبل از Merge الزامی می‌کند.

### Next test

ARM 2.8.3 را فلش، Bundle را با Build جدید از همان `11hand3.amsj` بساز و Guard log همراه Record را ارسال کن. Guard باید `OK|HVER|2.8.3` و زمان فعال نزدیک‌تر به 9–11 ثانیه نشان دهد.

## Build 56 — ARM 2.8.2 با Pacing طبیعی USB

**Previous build:** 55
**Status:** hardware tempo failed; superseded by ARM 2.8.3
**Commit:** `30e673e066f5084ea59de2eddb43ee6ac8686d2b`

### Problem observed

در Bundle 142، Runtime مبتنی بر deadline داخل Bundle حضور داشت و هر 25 Hash معتبر بود، اما بازپخش نمونهٔ هدف 9–11 ثانیه همچنان 21.495 ثانیه طول کشید.

### Evidence

- مسیر و مقصد نهایی دقیق باقی ماندند و سقف Micro-step برابر 2.83px بود.
- Deadline پیکو دیگر Delay ثبت‌شده را بعد از کار سخت‌افزار دوباره اضافه نمی‌کرد؛ بااین‌حال ARM 2.8.1 برای هر Micro-step هم Back-pressure طبیعی USB و هم `delay(1)` نرم‌افزاری پرداخت می‌کرد.

### Root cause

مسیر نسبی ARM 2.8.1 پس از هر گزارش HID یک میلی‌ثانیه صبر می‌کرد، درحالی‌که `USB_Send` نیز گزارش بعدی را تا آماده‌شدن Endpoint نگه می‌دارد. در HANDPATH پرتراکم این دو Pacing متوالی زمان اجرا را تقریباً دو برابر کردند.

### Change

- نسخهٔ Firmware به ARM 2.8.2 ارتقا یافت.
- فقط در `MMOVE|dx,dy,rel,2`، تأخیر صریح یک‌میلی‌ثانیه‌ای حذف شد و Endpoint USB مرجع Pacing باقی ماند.
- هندسهٔ DDA، مجموع Delta، ترتیب Segmentها و سقف زیر سه‌پیکسل تغییر نکردند.
- مسیرهای Absolute، Stream قدیمی، Click، Drag، Wheel و Sound بدون تغییر ماندند.
- Release اکنون علاوه بر Classroom Studio، فایل مستقیم `ARM-2.8.2-source.zip` و Hash آن را منتشر می‌کند.

### Validation

- پیمایش کامل ورودی‌های `-127..127` جمع دقیق Delta و Micro-stepهای حداکثر سه‌پیکسلی را بررسی می‌کند.
- قرارداد Firmware قفل می‌کند که مسیر Relative از Pace صفر استفاده کند و `delay(1)` در این تابع برنگردد.
- Workflow AutoCycle، Firmware را برای Leonardo/ATmega32U4 کامپایل می‌کند.

### Next test

ARM 2.8.2 را روی Pro Micro فلش کنید، سپس همان `11hand3.amsj` و بازهٔ 9–11 ثانیه را اجرا کنید. Record باید زمان فعال نزدیک 9–11 ثانیه، مقصد ثابت و Micro-stepهای زیر سه پیکسل نشان دهد.

## Build 55 — زمان‌بندی واقعی HANDPATH با deadline

**Previous build:** 52
**Status:** hardware tempo failed; superseded by ARM 2.8.2
**Commit:** `493a65ff190fb2268c8adfc599e41dd430f44c21`

### Problem observed

در Record مربوط به Bundle 141، مسیر کامل و دقیق اجرا شد اما بخش فعال حرکت 21.704 ثانیه طول کشید؛ درحالی‌که بازهٔ خواسته‌شده 9 تا 11 ثانیه بود.

### Evidence

- 1176 Segment منبع با زمان ثبت‌شدهٔ 10001ms به 13 خط کم‌حافظه تبدیل شده بود.
- جابه‌جایی خروجی `(207,122)` در برابر منبع `(207,123)` و حداکثر Micro-step برابر 2.83px بود؛ پس شکل مسیر و ARM 2.8.1 درست کار کردند.

### Root cause

- ARM هر Delta را به گزارش‌های حداکثر سه‌پیکسلی تقسیم می‌کند. زمان UART، Back-pressure و HID علاوه بر Delay ضبط‌شده محاسبه می‌شد و زمان کل تقریباً دو برابر می‌شد.

### Change

- HANDPATH اکنون به‌جای افزودن Delay ثابت بعد از کار سخت‌افزار، تا deadline تجمعی هر Segment صبر می‌کند.
- زمان مصرف‌شده توسط ARM/UART/HID از Delay بعدی کم می‌شود؛ Deltaها، Micro-stepها و مقصد تغییر نمی‌کنند.
- همین منطق در Runner سبک Pico، Executor کامل، Parallel Scheduler، اجرای مستقیم Windows و ScriptGenerator همگام شد.
- Pause همچنان زمان فعال Route را مصرف نمی‌کند.

### Validation

- شبیه‌سازی 1000 Segment با 10ms زمان هدف و 10ms هزینهٔ سخت‌افزار، به‌جای 20 ثانیه در 10.01 ثانیه پایان یافت.
- TestRunner قرارداد deadline در RunEngine و ScriptGenerator را به‌صورت صریح قفل می‌کند.
- `py_compile` برای هر سه Runtime مدرن موفق شد.
- اندازهٔ `plan_engine_exec.py` برابر 19,585 بایت و زیر سقف 20KB باقی ماند.
- Manifest هر سه فایل Runtime تغییرکرده بازسازی شد.

### Next test

Bundle جدید را از همان `11hand3.amsj` بسازید. زمان فعال حرکت باید نزدیک بازهٔ 9 تا 11 ثانیه باشد و جابه‌جایی نهایی و نرمی Micro-stepهای سه‌پیکسلی حفظ شود.

## Build 52 — Chunk کم‌حافظهٔ HANDPATH

**Previous build:** 51  
**Status:** hardware passed; tempo issue found  
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

Bundle 140 در Desktop پس از خواندن Route با `MemoryError` برای تخصیص 8745 بایت متوقف شد. فایل Route شامل یک خط `HANDPATH` تقریباً 8.7KB بود.

### Root cause

خود Route با موفقیت خوانده شد، اما `splitlines()` برای خط بزرگ HANDPATH یک بلوک پیوسته هم‌اندازه می‌خواست. Heap در آن لحظه 52,688 بایت آزاد داشت، ولی به‌علت Fragmentation بلوک پیوستهٔ 8,745 بایتی موجود نبود.

### Change

- Exporter مسیر ضبط‌شده را بدون حذف Segmentها به خط‌های حداکثر 96 Segment تقسیم می‌کند.
- بازهٔ ۹ تا ۱۱ ثانیه به‌صورت تناسبی بین Chunkها توزیع می‌شود و مجموع Min/Max دقیقاً حفظ می‌شود.
- Runtime سبک Pico اکنون `mt=min,max` را مستقیم اجرا و Delay هر Chunk را تجمعی Scale می‌کند.
- پیش از اجرای Route سبک، نسخهٔ کامل متن Route حذف و `gc.collect()` اجرا می‌شود.
- قرارداد قدیمی تک‌خطی همچنان پذیرفته می‌شود.

### Validation

- Route آزمایشی 240 Segment به سه خط HANDPATH کمتر از 1KB تبدیل شد.
- Runtime مدرن با `py_compile` معتبر است.
- Manifest برای `code.py` جدید بازسازی شد.
- هیچ Segment، Delta یا Micro-step حذف نشده است.

### Next test

Bundle را با Build جدید کامل بازسازی کنید و همان `11hand3.amsj` را اجرا کنید. پس از `after-route-read` باید `light-route` دیده شود؛ تخصیص 8745 بایت نباید تکرار شود و حرکت باید کامل شود.

## Build 51 — بازهٔ زمان بازپخش Hand Sample

**Previous build:** 50  
**Status:** CI candidate; hardware test pending  
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

در حالت `handSample` فیلدهای عمومی Human Mouse مخفی بودند و دو مقدار صفرِ پایین Dialog فقط Delay بعد از Step را کنترل می‌کردند. خود مسیر ده‌ثانیه‌ای همیشه با Timing ثابت Payload بازپخش می‌شد؛ بنابراین تکرار آن داخل Loop یا Random Package می‌توانست ریتم قابل‌تشخیص ایجاد کند.

### Root cause

قرارداد `HANDPATH` فقط `delay,dx,dy` داشت و هیچ بازهٔ Min/Max برای مدت کل بازپخش تعریف نمی‌کرد. رابط نیز کنترل اختصاصی این بازه را نمایش نمی‌داد.

### Change

- دو فیلد اختصاصی «حداقل/حداکثر زمان بازپخش نمونه» فقط در حالت `handSample` نمایش داده می‌شوند.
- پس از نمونه‌گیری ۱۰ثانیه‌ای، پیش‌فرض ۹۰۰۰ تا ۱۱۰۰۰ms تنظیم می‌شود.
- Exporter قرارداد سازگار `HANDPATH|mt=min,max|...` تولید می‌کند.
- Desktop، ScriptGenerator، Executor عادی Pico و Parallel Scheduler در هر اجرا یک مدت تازه انتخاب و Delayهای ثبت‌شده را متناسب Scale می‌کنند؛ Deltaها و شکل مسیر تغییر نمی‌کنند.
- پروژه‌ها و Planهای قدیمی بدون `mt` همچنان Timing اصلی Payload را اجرا می‌کنند.

### Validation

- Codec و Deltaهای نمونه دست‌نخورده باقی ماندند.
- Validator بازهٔ ۱ تا ۶۰ ثانیه و ترتیب Min/Max را کنترل می‌کند.
- Scaling تجمعی، مجموع Delayها را دقیقاً به مدت تصادفی انتخاب‌شده می‌رساند و خطای گردکردن بین Segmentها جمع نمی‌شود.
- تست قرارداد Export، نمایش فیلدهای اختصاصی و مسیر Desktop به‌روزرسانی شد.

### Next test

یک Hand Sample ده‌ثانیه‌ای را چند بار داخل Random Package اجرا کنید. زمان هر اجرا باید بین ۹ تا ۱۱ ثانیه تغییر کند، پایان Relative Delta ثابت بماند و حرکت روی Pico بدون `MemoryError` یا وقفهٔ مصنوعی کامل شود.

## Build 50 — ثبت نتایج سخت‌افزاری B و C

**Previous build:** 50  
**Status:** CI candidate; Build 50 tuning results B/C recorded  
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

تست A از نظر حافظه موفق بود، اما Record آن 862 فاصلهٔ حداقل 40ms داشت. B وقفه‌ها را رفع کرد، ولی برای معیار اصلی «انسانی‌بودن» باید تنوع زمانی و انحنای C نیز با B مقایسه می‌شد.

### Root cause

حرکت انسانی نباید زمان و هندسهٔ ثابت داشته باشد. B با `mt=900..1400` و `curve=0..8` عمدی آهسته و نرم است؛ C با `mt=600..900` و `curve=3..18` دامنهٔ سرعت و انحنای بیشتری دارد.

### Change

- کد Runtime تغییر نکرد.
- نتیجهٔ تست B با `curve=0..8` و `mt=900..1400` و Record تست C در Changelog ثبت شد.
- Bundle ارسالی B بررسی شد و هر 25 Hash در Manifest معتبر بود.
- B به‌عنوان نرم‌ترین گزینهٔ آهسته و C به‌عنوان انسانی‌ترین گزینهٔ فعلی علامت‌گذاری شدند.

### Validation

- لاگ Build 50: پیش از Route مقدار 74,080، پس از Import مقدار 68,896 و پس از Parse مقدار 68,512 بایت آزاد بود.
- Route `desktop_steps.txt` تا `ROUTE/complete` اجرا شد و `MemoryError` رخ نداد.
- Record B: 2,342 موقعیت، 2,341 Segment و 28,863ms زمان ثبت‌شده.
- گام میانه 2.24px، صدک 95 و بیشینه هر دو 2.83px بودند؛ سقف Micro-step حفظ شد.
- فاصلهٔ زمانی میانه 13ms، صدک 80 برابر 14ms و صدک 95 برابر 15ms بود.
- فقط 13 فاصلهٔ حداقل 40ms ثبت شد؛ نسبت به 862 مورد A حدود 98.5٪ کاهش داشت و تقریباً به مکث‌های عمدی Route محدود شد.
- Record C شامل 2,339 موقعیت، 2,338 Segment و 32,864ms زمان ثبت‌شده بود.
- در C فاصلهٔ زمانی میانه 2ms، صدک 80 و 95 هر دو 14ms و فقط 13 فاصلهٔ حداقل 40ms بود.
- گام C میانه 1.41px، صدک 95 و بیشینه 2.83px بود.
- تنوع فعال C بیشتر از B بود: ضریب تغییرات فاصله‌ها 0.88 در برابر 0.67 و Entropy نرمال‌شده 0.607 در برابر 0.594. این اختلاف کوچک اما همراه با Curve گسترده‌تر، C را از نظر الگوی غیررباتی جلو می‌اندازد.

### Next test

پروژهٔ D را با Build 50 اجرا و Guard log، Record و Bundle را ثبت کنید. معیار اصلی انسانی‌بودن است: دامنهٔ زمانی/انحنا باید متغیر باشد، وقفه‌های ناخواسته پایین بمانند و Micro-step از سه پیکسل عبور نکند.

## Build 50 — Changelog اجباری و تأیید سخت‌افزاری A

**Previous build:** 49  
**Status:** CI passed; A hardware result recorded  
**Commit:** [`535cc4cf`](https://github.com/noonoix/smz/commit/535cc4cf77238b357c8cfcfe31ff3788caaf17e8)  
**Release:** [classroom-current-50](https://github.com/noonoix/smz/releases/tag/classroom-current-50)  
**SHA256:** `1e6d2fe654a7020701b26470d5348eae9c45cb5b1dd8f9399ec7616962169cf4`

### Problem observed

اطلاعات علت شکست و اصلاح هر Build در PRها و صفحهٔ داخلی پراکنده بود و متن Release عمومی و تکراری بود.

### Root cause

Workflow انتشار متن ثابت داشت و تغییر Build بدون ورودی Changelog قابل انتشار بود.

### Change

Changelog تجمعی، لینک README، کنترل اجباری Workflow و Release notes تولیدشده از جدیدترین ورودی اضافه شدند.

### Validation

Build 50 نخستین Release با متن واقعی Changelog بود. نتیجهٔ سخت‌افزاری A نیز بدون `MemoryError` تا `ROUTE/complete` رسید.

### Next test

B، C و D برای انتخاب Tempo و Curve مناسب مقایسه شوند.

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
