# Classroom Studio — Current Hardware Changelog

این سند مرجع سریع وضعیت شاخهٔ پایدار `stable/natural-mouse-v1` است. ترتیب ورودی‌ها معکوس زمانی است؛ جدیدترین Build همیشه بالاتر قرار می‌گیرد.

## وضعیت فعلی در یک نگاه

- **Baseline سخت‌افزاری انتخاب‌شده:** Build 50 + ARM 2.8.1 + پروژهٔ C
- **وضعیت حرکت:** Natural Mouse v1 در Build 66 و Bundle 160 از نظر نرمی، Endpoint و حافظهٔ Desktop تأیید شده است.
- **مسئلهٔ باز:** Login/DC در Import Parser کامل با تخصیص 2930 بایت شکست خورد؛ Build 67 آن را به Runner سبک منتقل می‌کند.
- **اصل معماری:** Cadence و Micro-step تأییدشدهٔ Build 50 دست‌نخورده می‌ماند؛ Humanization فقط در سطح مسیر اعمال می‌شود.
- **معماری:** Pico مسئول Keyboard/Guard/Route، و Pro Micro مسئول Mouse HID و Sound است.
- **Golden 100:** جدا و بدون تغییر باقی مانده است.

| Build | نتیجهٔ سخت‌افزاری | مسئله/تغییر اصلی | وضعیت |
| --- | --- | --- | --- |
| {{BUILD_NUMBER}} | تست سخت‌افزاری لازم است | Runner سبک Streaming برای Login/DC | CI candidate |
| 66 | Desktop و Natural Mouse پاس؛ Login/DC MemoryError | Natural Mouse v1 روی Baseline 50 | Mouse-stable؛ Login superseded |
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

## Build {{BUILD_NUMBER}} — Runner سبک Streaming برای Login/DC

**Previous build:** 66
**Status:** CI candidate; Login/DC hardware retest pending
**Commit:** `{{COMMIT_SHA}}`

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
