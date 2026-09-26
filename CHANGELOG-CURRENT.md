# Classroom Studio — Current Hardware Changelog

این سند مرجع سریع وضعیت شاخهٔ فعال `fix/portable-relative-mouse` است. ترتیب ورودی‌ها معکوس زمانی است؛ جدیدترین Build همیشه بالاتر قرار می‌گیرد.

## وضعیت فعلی در یک نگاه

- **آخرین Build دارای تست سخت‌افزاری:** Build 52 (Bundle 141)
- **وضعیت حافظه:** Chunk شدن HANDPATH تأیید شد؛ ۱۳ خط با حداکثر طول ۸۱۴ کاراکتر و بدون خط 8.7KB.
- **وضعیت کیفیت حرکت:** شکل و مقصد دقیق است، اما زمان واقعی 21.704 ثانیه در برابر هدف 9–11 ثانیه بود.
- **معماری:** Pico مسئول Keyboard/Guard/Route، و Pro Micro مسئول Mouse HID و Sound است.
- **Golden 100:** جدا و بدون تغییر باقی مانده است.

| Build | نتیجهٔ سخت‌افزاری | مسئله/تغییر اصلی | وضعیت |
| --- | --- | --- | --- |
| {{BUILD_NUMBER}} | در انتظار تست سخت‌افزاری | زمان‌بندی deadline-based برای HANDPATH | CI candidate |
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

## Build {{BUILD_NUMBER}} — زمان‌بندی واقعی HANDPATH با deadline

**Previous build:** 52  
**Status:** CI candidate; hardware retest pending  
**Commit:** `{{COMMIT_SHA}}`

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
