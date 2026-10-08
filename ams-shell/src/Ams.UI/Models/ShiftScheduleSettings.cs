namespace Ams.UI.Models;

public sealed class ShiftScheduleSettings
{
    public bool enabled { get; set; }
    public int dayStart { get; set; } = 480;
    public int dayEnd { get; set; } = 1200;
    public int nightStart { get; set; } = 1200;
    public int nightEnd { get; set; } = 480;
    public int maxAttempts { get; set; } = 2;
    public void Validate()
    {
        if(!enabled)return;
        if(new[]{dayStart,dayEnd,nightStart,nightEnd}.Any(v=>v<0||v>=1440)
            ||dayStart==dayEnd||nightStart==nightEnd||maxAttempts<1||maxAttempts>10)
            throw new FormatException("بازه‌های شیفت و سقف تلاش ۱ تا ۱۰ را بررسی کنید.");
        static bool Contains(int m,int start,int end)=>start<end?m>=start&&m<end:m>=start||m<end;
        if(Enumerable.Range(0,1440).Any(m=>Contains(m,dayStart,dayEnd)&&Contains(m,nightStart,nightEnd)))
            throw new FormatException("بازه‌های روز و شب نباید هم‌پوشانی داشته باشند.");
    }
}
