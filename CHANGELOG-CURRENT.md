# Classroom Studio — Current Hardware Changelog

این سند مرجع سریع وضعیت شاخهٔ پایدار `stable/natural-mouse-v1` است. ترتیب ورودی‌ها معکوس زمانی است؛ جدیدترین Build همیشه بالاتر قرار می‌گیرد.

## وضعیت فعلی در یک نگاه

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
| 79 | تست سخت‌افزاری لازم است | Peak telemetry برای WSND و Timeout غیرخطایی | CI candidate |
| 78 | Build 95: میانهٔ 51ms و 1,194 وقفهٔ حداقل 40ms | USB handshake فقط با بایت واقعی؛ حذف stall هنگام بازبودن COM | Local candidate |
| 77 | Build 94: حرکت پس از ARM 2.8.2 شکسته و Typo 7–12 تقریباً روی هر حرف اجرا شد | بازیابی Cadence 2.8.1 با Sound بین فرمان‌ها؛ Typo با فاصلهٔ کاراکتری | Local candidate |
| 76 | Bundle 175: دو Guard JSON با Debug/FAT cross-link خراب شدند | حذف Debug file write، پاسخ سریع دکمه و Read-back کامل Export | Local candidate |
| 75 | Build 74: Game ابتدا اجرا شد؛ بازگشت بعد از Lux spike شکست خورد | Game re-entry، کالیبراسیون مقاوم Game/Target و بازیابی کامل Bridge | Local candidate |
| 74 | تست سخت‌افزاری لازم است | کالیبراسیون دو اسلات صوتی | CI candidate |
| 73 | تست سخت‌افزاری لازم است | انتقال پروفایل‌های نور فعلی Classroom به Pico | CI candidate |
| 72 | تست سخت‌افزاری لازم است | Proxy صدا از Pico به Pro Micro و حذف نویز Cursor | CI candidate |
| 71 | تست سخت‌افزاری لازم است | رفع اتصال سبز کاذب و کالیبراسیون صوتی روی Bridge قطع‌شده | CI candidate |
| 70 | تست سخت‌افزاری لازم است | بازیابی امن LABEL/GOTO و Light Watch | CI candidate |
| 69 | تست سخت‌افزاری لازم است | پایش Async صدا و توقف فوری Mouse پیش از F | CI candidate |
| 68 | Desktop/Login/DC/Game پاس | Runner سبک Game؛ تأخیر Sound هنگام حرکت | Hardware pass؛ Sound superseded |
| 67 | Desktop و Login/DC سبک پاس؛ Calibration ذخیره شد | Runner سبک Streaming برای Login/DC | Hardware pass؛ Game superseded |
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
**Status:** Local candidate; hardware retest pending
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

Build 94 نشان داد حرکت با ARM 2.8.2 شکسته شده و Typo بین تقریباً همهٔ کاراکترها فعال است. Record سخت‌افزاری میانهٔ 51ms و صدک 95 برابر 53ms داشت.

### Root cause

Sound sampling داخل حلقهٔ Micro-step قرار گرفته بود و هر گزارش HID را با ADC کار اضافی قطع می‌کرد. Typo نیز فاصله را به تعداد کلمات تفسیر می‌کرد.

### Change

- ARM 2.8.2-S1 ADC sampling را از داخل Micro-step خارج کرد و فقط در مرز فرمان‌ها اجرا می‌کند.
- Typo count دوباره بر پایهٔ فاصلهٔ کاراکتری واقعی اجرا می‌شود.

### Validation

- قرارداد ARM تضمین می‌کند `ARM_SOUND_TICK()` داخل `mouse_move_steps` نیست.
- تست Exhaustive Endpoint و سقف سه‌پیکسلی سبز است.

### Next test

ARM 2.8.2-S1 را فلش و حرکت C را دوباره Record کنید.

## Build 76 — ایزوله‌سازی FAT و اعتبارسنجی Export

**Previous build:** 75
**Status:** Local candidate; hardware retest pending
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

Bundle 175 بعد از چند ذخیرهٔ Calibration دو فایل JSON را با دادهٔ Debug مخلوط کرد.

### Root cause

Debug persistence و Calibration هم‌زمان روی FAT قابل‌مشاهده از USB می‌نوشتند؛ Export نیز بایت‌های مقصد را دوباره نمی‌خواند.

### Change

- Debug file write حذف شد و فقط NVM استفاده می‌شود.
- Export تمام 28 فایل را از مقصد دوباره می‌خواند و Hash می‌کند.
- Bridge پیش از نوشتن متوقف می‌شود.

### Validation

- قرارداد FAT isolation و Read-back سبز است.

### Next test

Bundle را دوباره Export و Hashها را بررسی کنید.

## Build 75 — بازیابی Game/Target و اتصال Classroom

**Previous build:** 74
**Status:** Local candidate; hardware retest pending
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

بعد از Lux spike، Game دوباره اجرا نمی‌شد و Classroom گاهی پس از Export وصل نمی‌شد.

### Root cause

Guard re-entry و overlap قدیمی Game/Target به‌درستی مدیریت نمی‌شد و Bridge lifecycle ناقص بود.

### Change

- Game re-entry و Calibration overlap مقاوم شد.
- Bridge بعد از Export دوباره به Pico متصل می‌شود.

### Validation

- تست‌های Game/Target و reconnect سبز هستند.

### Next test

Game و Target را در چرخهٔ واقعی دوباره تست کنید.

## Build 74 — کالیبراسیون پرتابل Step صوتی

**Previous build:** 73
**Status:** CI candidate; hardware retest pending
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

Threshold ثابت برای دو صدای متفاوت مناسب نبود و Tester خارجی تنظیم را ذخیره نمی‌کرد.

### Root cause

Runtime پروفایل صوتی پایدار و Binding به Step نداشت.

### Change

- دو Calibration ID شمارهٔ 1 و 2 به Wait For Sound اضافه شد.
- GP3 بلند وارد Sound Calibration می‌شود؛ GP4 کوتاه ID را انتخاب می‌کند؛ GP3 کوتاه نمونه می‌گیرد و GP3 بلند ذخیره/خارج می‌شود.
- پروفایل‌ها در `/sound-step-calibration.json` با Binding و Checksum ذخیره می‌شوند.
- Exporter دستور `WSNDP` تولید می‌کند.

### Validation

- تست‌های Binding، Duplicate ID، فایل پشتیبان و Runtime سبز هستند.

### Next test

هر دو صدای چلپ و Whisper را جداگانه کالیبره و Route را تست کنید.

## Build 73 — انتقال پروفایل‌های نور Classroom به Pico

**Previous build:** 72
**Status:** CI candidate; Guard hardware retest pending
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

صفحهٔ وضعیت Classroom محیط Game را با پروفایل ذخیره‌شدهٔ کاربر (`25.8 ± 0.5`) و اطمینان 100٪ تشخیص می‌داد، اما Bundle روی Pico همچنان مقادیر ثابت Template (`22.5 ± 3.7` و سایر پروفایل‌های قدیمی) را داشت.

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

### Root cause

- Runner سبک Game فرمان‌های LABEL/GOTO را نمی‌شناخت.
- Parser نور فقط قرارداد طولانی را می‌پذیرفت.
- Watch پس از Fault متوقف نمی‌شد.

### Change

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

### Next test

Bundle جدید را Export و مسیر Game را دوباره اجرا کنید.

## Build 69 — واکنش هم‌زمان Sound در حین حرکت Mouse

**Previous build:** 68
**Status:** CI candidate; hardware retest pending
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

Build 68 مسیر Game بدون MemoryError و با حرکت نرم اجرا شد، اما Runner هنگام فعال‌بودن Mouse عمداً Poll صدا را عقب می‌انداخت. علت جلوگیری از تداخل فرمان Blocking `SCAL` با `MMOVE` و خطای `ERR|BUSY` بود. نتیجه این بود که کلید F فقط بعد از ایستادن Mouse اجرا می‌شد و Catch قلاب تأخیر داشت.

### Root cause

Sound polling در نقاط حرکت فعال غیرفعال بود.

### Change

- Firmware به ARM 2.8.2 ارتقا یافت و فرمان‌های `ASND` و `ASNDCANCEL` اضافه شدند.
- Pro Micro هنگام اجرای Micro-stepهای Mouse، ورودی صوتی A0 را پیوسته و غیرمسدودکننده پایش می‌کند.
- با تشخیص صدا، Mouse در آخرین نقطهٔ ارسال‌شده فوراً متوقف می‌شود و رویداد `EVT|ASND|DETECTED` به Pico می‌رسد.
- Pico سپس کلید F را مستقل از Mouse HID می‌زند؛ MMOVEهای صف‌شده تا Arm بعدی صدا ACK و Drop می‌شوند تا قبل از F حرکت ادامه پیدا نکند.

### Validation

- تست جدید ثابت می‌کند Sound poll در بازهٔ حرکت فعال انجام می‌شود و F حداکثر تا 40ms در شبیه‌سازی اجرا می‌گردد.

### Next test

Firmware و Bundle را روی سخت‌افزار واقعی تست کنید.

## قانون به‌روزرسانی

برای هر تغییر Build‌ساز:

1. بخش `Build {{BUILD_NUMBER}}` فعلی را با شمارهٔ واقعی Build قبلی تثبیت کنید.
2. یک بخش جدید `Build {{BUILD_NUMBER}}` در بالای تاریخچه اضافه کنید.
3. Problem، Root cause، Change، Validation و Next test را تکمیل کنید.
4. وضعیت Hardware را از CI جدا نگه دارید؛ CI سبز به‌تنهایی به معنی تأیید سخت‌افزاری نیست.
5. Workflow بدون تغییر همین فایل اجازهٔ انتشار Build جدید را نمی‌دهد.
