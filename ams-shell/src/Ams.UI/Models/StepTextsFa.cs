using System.Collections.Generic;

namespace Ams.UI.Models;

/// <summary>
/// v0.9.24 — Persian descriptions for step-dialog fields. User request: «گزینه‌ها انگلیسی
/// باشند ولی توضیحات فارسی» — option VALUES and combo items stay English; only the
/// human-facing descriptions become Persian, rendered right-aligned by StepDialog.
///
/// Lookup order: "stepType:fieldKey" (for keys that mean different things per step,
/// e.g. text/mode/path/x) → plain "fieldKey" → the original English label (fallback).
/// </summary>
public static class StepTextsFa
{
    /// <summary>Shared keys — one Persian label serves every step that uses the key.</summary>
    private static readonly Dictionary<string, string> Fa = new(StringComparer.Ordinal)
    {
        // ── v0.9.55 — renameable container heads
        ["title"] = "عنوان مجموعه (خالی = نام پیش‌فرض)",
        // ── shared human-mouse fields (mouseMove + findImage approach + randomMousePosition) ──
        ["human"] = "حرکت انسانی (مسیر WindMouse + مکث‌ها سمت اپ — خاموش = پرش فوری برد)",
        ["pauseBeforeMin"] = "مکث پیش از حرکت — حداقل (ms)",
        ["pauseBeforeMax"] = "مکث پیش از حرکت — حداکثر (ms)",
        ["pauseAfterMin"] = "مکث پس از رسیدن — حداقل (ms)",
        ["pauseAfterMax"] = "مکث پس از رسیدن — حداکثر (ms)",
        ["midPauseChance"] = "احتمال مکث میان‌مسیر ٪ (۰ = خاموش)",
        ["midPauseMin"] = "طول مکث میان‌مسیر — حداقل (ms)",
        ["midPauseMax"] = "طول مکث میان‌مسیر — حداکثر (ms)",
        ["overshootChance"] = "احتمال عبور از هدف و اصلاح برگشتی ٪ (انسانی‌تر شوت می‌کند)",
        ["curveMinPct"] = "انحنای مسیر حداقل ٪ · ۰–۱۰۰ قوس ملایم · ۱۰۱–۲۰۰ قوس واقعی",
        ["curveMaxPct"] = "انحنای مسیر حداکثر ٪ · در طول حرکت نرم تغییر می‌کند",
        ["moveTimeMin"] = "مدت حرکت — حداقل (ms) · 0/0 = بر اساس بازه‌ی سرعت (Options)",
        ["moveTimeMax"] = "مدت حرکت — حداکثر (ms)",
        ["idleEveryMin"] = "استراحت طولانی هر N حرکت — حداقل N",
        ["idleEveryMax"] = "استراحت طولانی هر N حرکت — حداکثر N",
        ["idlePauseMin"] = "طول استراحت طولانی — حداقل (ms)",
        ["idlePauseMax"] = "طول استراحت طولانی — حداکثر (ms) · ۰ = خاموش (۸۰۰–۳۰۰۰ = کالیبره‌ی انسانی)",

        // ── mouse actions ──
        ["button"] = "دکمه‌ی موس",
        ["moveMode"] = "حالت حرکت (fixed = مقصد مطلق · handSample = بازپخش نسبی نمونه از موقعیت فعلی نشانگر)",
        ["handSample"] = "نمونه‌ی نسبی ذخیره‌شده‌ی حرکت دست",
        ["action"] = "نوع کلیک",
        ["delta"] = "مقدار ویل (عدد منفی = پایین)",
        ["scrollDelta"] = "مقدار اسکرول (منفی = پایین، مثبت = بالا)",
        ["speedPct"] = "سرعت پخش نوت‌ها ٪ (۱۰۰ عادی، بیشتر = سریع‌تر)",

        // ── keyboard ──
        ["modCtrl"] = "Ctrl",
        ["modShift"] = "Shift",
        ["modAlt"] = "Alt",
        ["modWin"] = "Win",
        ["key"] = "کلید",
        ["hmin"] = "انسانی‌سازی حداقل (ms بین کلیدها — پیش‌فرض انسانی ۸۰)",
        ["hmax"] = "انسانی‌سازی حداکثر (ms بین کلیدها — پیش‌فرض انسانی ۲۲۰)",
        ["wmin"] = "مکث بین کلمات — حداقل (ms · ۰ = خاموش)",
        ["wmax"] = "مکث بین کلمات — حداکثر (ms · ۰ = خاموش)",
        ["wordPauseChance"] = "احتمال مکث کلمه ٪ · ۱۰۰ = بعد از هر کلمه (مترونوم) · ۴۰–۷۰ طبیعی است",
        ["pmin"] = "مکث نقطه‌گذاری — حداقل (ms بعد از . , ! ? ; : · ۰ = خاموش)",
        ["pmax"] = "مکث نقطه‌گذاری — حداکثر (ms)",
        ["thinkChance"] = "احتمال مکث فکرکردن ٪ به‌ازای هر کلمه (۰ = خاموش · انسان برای فکر مکث می‌کند)",
        ["thinkMin"] = "مکث فکرکردن — حداقل (ms)",
        ["thinkMax"] = "مکث فکرکردن — حداکثر (ms)",
        ["typoEveryMin"] = "فاصلهٔ بین خطاهای تایپی — حداقل تعداد کاراکتر · ۰/۰ = خاموش",
        ["typoEveryMax"] = "فاصلهٔ بین خطاهای تایپی — حداکثر تعداد کاراکتر · پس از هر اصلاح دوباره قرعه‌کشی می‌شود",

        // ── timing / loops ──
        ["minMs"] = "حداقل (ms)",
        ["maxMs"] = "حداکثر (ms)",
        ["count"] = "تعداد تکرار (حالت count)",
        ["timeValue"] = "مقدار زمان (حالت time)",
        ["timeUnit"] = "واحد زمان",
        ["minCount"] = "حداقل استپ در هر دور (randomSubset)",
        ["maxCount"] = "حداکثر استپ در هر دور (randomSubset)",

        // ── sound (waitForSound) ──
        ["threshold"] = "آستانه شروع شنود (از Calibrate استفاده کن؛ ۰ = کالیبراسیون فیزیکی ذخیره‌شده)",
        ["peakMin"] = "کف بازهٔ Peak (۰ = آستانهٔ کالیبره‌شده)",
        ["peakMax"] = "سقف بازهٔ Peak",
        ["soundPriority"] = "اولویت در بازه‌های هم‌پوشان (عدد بزرگ‌تر برنده است)",
        ["minDurationMs"] = "حداقل مدت (ms) — رویداد معمولاً ۱٫۵–۲٫۲ ثانیه است؛ ۶۰–۱۰۰ امن است",
        ["cooldownMs"] = "فاصلهٔ ایمنی پس از تشخیص صدا (ms)",
        ["calibrationId"] = "شناسهٔ کالیبراسیون همین Step صوتی (فقط ۱ یا ۲؛ در هر پروژه یکتا)",
        ["timeoutMs"] = "Timeout قدیمی (ms)",
        ["responseRoute"] = "رفتار تشخیص (inline یا Splash scoped)",
        ["timeoutMinSec"] = "حداقل Timeout هر پرتاب (ثانیه)",
        ["timeoutMaxSec"] = "حداکثر Timeout هر پرتاب (ثانیه)",
        ["armed"] = "واکنش مسلح: برد خودش هنگام تشخیص کلیک می‌کند (TRGSND)",
        ["act"] = "دکمه‌ی کلیک مسلح",
        ["reactMin"] = "واکنش — حداقل (ms)",
        ["reactMax"] = "واکنش — حداکثر (ms)",

        // ── light (waitForLight — BH1750 / GY-302 / GY-30 on the Pico) — v0.9.39 ──
        ["luxCenter"] = "مرکز روشنایی (لوکس) — همان صفحه‌ی واقعی را نشان بده و Calibrate را بزن",
        ["luxTolerance"] = "تلورانس ± (لوکس) — بین دو وضعیت حداقل ۵۰ لوکس فاصله بگذار (dead zone)",
        ["stableSec"] = "تثبیت به مدت N انیه داخل بازه، بعد شناسایی معتبر است",
        ["sampleMode"] = "حالت سنسور — hires: دقت ۱ لوکس / حدود ۱۲۰ms · lowres: ۴ لوکس / حدود ۱۶ms",

        // ── find image ──
        ["pictures"] = "فایل(های) تصویر — هر خط یکی (منطق OR)",
        ["similarity"] = "درصد شباهت",
        ["searchScope"] = "محدوده‌ی جست‌وجو",
        ["neverTimeout"] = "بدون Timeout",
        ["timeoutValue"] = "Timeout",
        ["timeoutUnit"] = "واحد Timeout",
        ["onFound"] = "وقتی پیدا شد",
        ["onTimeout"] = "وقتی Timeout شد",
        ["insertIfElse"] = "درج If-Else (فرزندان = شاخه‌ی Then)",
        ["humanMove"] = "حرکت انسانی موس به سمت هدف (مسیر WindMouse + مکث‌ها · خاموش = پرش فوری)",

        // ── misc ──
        ["message"] = "پیام خطا (با رسیدن به این استپ، ماکرو فوراً متوقف و با بازر هشدار داده می‌شود)",

        ["cmd"] = "فرمان خام به برد (جداکننده‌ی pipe — مثل WSND|90,60,20000)",
        ["args"] = "آرگومان‌ها (اختیاری)",
        ["windowState"] = "حالت پنجره",
        ["loop"] = "تکرار تا توقف دستی",
        ["secret"] = "حساس (رمز) — در لاگ مخفی می‌شود و از طریق clipboard الصاق می‌شود",

        // v0.9.29 — the shared shell fields appended to every dialog (MainViewModel __name/__delay/__delayMax)
        ["__name"] = "نام استپ",
        ["__delay"] = "تأخیر بعد از استپ (ms)",
        ["__delayMax"] = "حداکثر تأخیر (ms) — 0 = ثابت؛ اگر بزرگ‌تر از تأخیر باشد، هر اجرا یک مقدار تصادفی بینابینی می‌آید (v0.7.8)",
    };

    /// <summary>Step-specific overrides for keys that mean different things per step.</summary>
    private static readonly Dictionary<string, string> FaByStep = new(StringComparer.Ordinal)
    {
        // "text" — typeText vs comment
        ["typeText:text"] = "متن",
        ["comment:text"] = "یادداشت",

        // v0.9.46 — per-step keyboard executor (default falls back to Options)
        ["keystroke:keyboardBoard"] = "برد اجراکننده (default = قانون کلی Options · pico · promicro)",
        ["typeText:keyboardBoard"] = "برد اجراکننده (default = قانون کلی Options · pico · promicro)",
        ["keyDown:keyboardBoard"] = "برد اجراکننده (default = قانون کلی Options · pico · promicro)",
        ["keyUp:keyboardBoard"] = "برد اجراکننده (default = قانون کلی Options · pico · promicro)",

        // "mode" — typeText vs forLoop vs randomPackage
        ["typeText:mode"] = "حالت (keystrokes = کلیدبه‌کلید · clipboard = الصاق)",
        ["forLoop:mode"] = "حالت تکرار (count/time/infinite)",
        ["randomPackage:mode"] = "حالت — shuffleAll: هر فرزند یک بار با ترتیب تصادفی تازه · randomSubset: تعداد تصادفی از فرزندان",

        // x/y/w/h — mouseMove vs randomMousePosition vs findImage
        ["mouseMove:x"] = "مختصات X",
        ["mouseMove:y"] = "مختصات Y",
        ["randomMousePosition:x"] = "ناحیه X",
        ["randomMousePosition:y"] = "ناحیه Y",
        ["randomMousePosition:w"] = "عرض ناحیه",
        ["randomMousePosition:h"] = "ارتفاع ناحیه",
        ["findImage:x"] = "ناحیه X (scope=region)",
        ["findImage:y"] = "ناحیه Y",
        ["findImage:w"] = "عرض ناحیه",
        ["findImage:h"] = "ارتفاع ناحیه",

        // "path" — openFile / playAudio / runExe / playScript
        ["openFile:path"] = "مسیر فایل",
        ["buzzer:preset"] = "نوع صدا (کوتاه، دوتایی، اعلان، هشدار، موفقیت، خطا، صعودی، نزولی یا سفارشی)",
        ["buzzer:volume"] = "شدت صدا (۱ تا ۱۰۰ درصد)",
        ["buzzer:envelope"] = "لبه صدا (sharp = فوری · smooth = نرم · fade-in / fade-out)",
        ["buzzer:pattern"] = "الگوی سفارشی — فرکانس:مدت,مکث;... نمونه: 900:150,80;1200:250",
        ["playAudio:path"] = "فایل صوتی (wav / mp3)",
        ["playAudio:loop"] = "تکرار تا توقف دستی",
        ["playAudio:outputDevice"] = "دستگاه خروجی صدا — انتخاب از فهرست (۰=پیش‌فرض، -۱=خودکار)",   // v0.9.43 — dropdown of real devices
        ["runExe:path"] = "مسیر فایل اجرایی",
        ["playScript:path"] = "مسیر اسکریپت amk.",

        // "label" — label vs gotoLabel
        ["label:label"] = "نام لیبل (هدف پرش برای Go To Label)",
        ["gotoLabel:label"] = "لیبل مقصد پرش",

        // hold — click/keystroke (firmware default) vs waitForSound (plain)
        ["mouseClick:holdMin"] = "حداقل نگه‌داشت (ms) — بازه‌ی تصادفی فشردن؛ ۰ = پیش‌فرض فریم‌ور (firmware 1.7+)",
        ["mouseClick:holdMax"] = "حداکثر نگه‌داشت (ms) — ۰ = پیش‌فرض فریم‌ور (firmware 1.7+)",
        ["keystroke:holdMin"] = "حداقل نگه‌داشت (ms) — بازه‌ی تصادفی فشردن؛ ۰ = پیش‌فرض فریم‌ور (firmware 1.7+)",
        ["keystroke:holdMax"] = "حداکثر نگه‌داشت (ms) — ۰ = پیش‌فرض فریم‌ور (firmware 1.7+)",
        ["waitForSound:holdMin"] = "نگه‌داشت — حداقل (ms)",
        ["waitForSound:holdMax"] = "نگه‌داشت — حداکثر (ms)",

        // v0.9.41 - these three are step-qualified, so they belong in FaByStep;
        // v0.9.39 put them in the shared table, where Has()/Get() could never see them.
        ["waitForLight:key"] = "کلید مسلح (برد خودش می‌زند)",
        ["waitForLight:holdMin"] = "نگه‌داشت کلید — حداقل (ms)",
        ["waitForLight:holdMax"] = "نگه‌داشت کلید — حداکثر (ms)",

        // pause-before wording differs: reaction (randomMousePosition) / approach (findImage)
        ["randomMousePosition:pauseBeforeMin"] = "مکث واکنش پیش از حرکت — حداقل (ms)",
        ["randomMousePosition:pauseBeforeMax"] = "مکث واکنش پیش از حرکت — حداکثر (ms)",
        ["findImage:pauseBeforeMin"] = "مکث پیش از حرکت approach — حداقل (ms)",
        ["findImage:pauseBeforeMax"] = "مکث پیش از حرکت approach — حداکثر (ms)",

        // moveTime wording differs per step
        ["randomMousePosition:moveTimeMin"] = "مدت حرکت — حداقل (ms) · 0/0 = بازه‌ی سرعت سراسری (Options)",
        ["randomMousePosition:moveTimeMax"] = "مدت حرکت — حداکثر (ms) · هر حرکت هدف تصادفی تازه می‌گیرد؛ کف ≈ 1ms به‌ازای هر میکرواستپ",
        ["findImage:moveTimeMin"] = "مدت حرکت approach — حداقل (ms) · 0/0 = بر اساس سرعت (Options)",
        ["findImage:moveTimeMax"] = "مدت حرکت approach — حداکثر (ms)",
    };

    /// <summary>Step-aware Persian lookup. Falls back to the original English label so
    /// dialogs stay usable even for keys nobody has translated yet.</summary>
    public static string Get(string? stepType, string fieldKey, string englishFallback)
    {
        if (stepType is not null && FaByStep.TryGetValue(stepType + ":" + fieldKey, out var s)) return s;
        if (Fa.TryGetValue(fieldKey, out var g)) return g;
        return englishFallback;
    }

    /// <summary>v0.9.25 — true when the key has an explicit entry (some deliberately map to
    /// the same Latin text, e.g. modifiers stay "Ctrl"/"Shift"). TestRunner's coverage sweep.</summary>
    public static bool Has(string? stepType, string fieldKey)
        => (stepType is not null && FaByStep.ContainsKey(stepType + ":" + fieldKey)) || Fa.ContainsKey(fieldKey);
}
