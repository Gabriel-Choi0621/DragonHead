namespace DragonHead.Core;

/// <summary>한국 표준시. 서머타임이 없으므로 고정 오프셋(+09:00)으로 충분하다.</summary>
public static class Kst
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(9);

    public static DateTimeOffset Now => DateTimeOffset.UtcNow.ToOffset(Offset);

    public static DateTimeOffset ToKst(this DateTimeOffset t) => t.ToOffset(Offset);

    public static DateOnly Today => DateOnly.FromDateTime(Now.DateTime);

    public static DateTimeOffset At(DateOnly date, int hour, int minute) =>
        new(date.Year, date.Month, date.Day, hour, minute, 0, Offset);
}
