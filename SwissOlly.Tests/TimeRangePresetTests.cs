namespace SwissOlly.Tests;

public sealed class TimeRangePresetTests
{
    [Fact]
    public void Enum_Values_Match_Expected_Minutes()
    {
        Assert.Equal(1, (int)TimeRangePreset.OneMinute);
        Assert.Equal(5, (int)TimeRangePreset.FiveMinutes);
        Assert.Equal(15, (int)TimeRangePreset.FifteenMinutes);
        Assert.Equal(2880, (int)TimeRangePreset.TwoDays);
        Assert.Equal(7200, (int)TimeRangePreset.FiveDays);
        Assert.Equal(10080, (int)TimeRangePreset.SevenDays);
        Assert.Equal(43200, (int)TimeRangePreset.ThirtyDays);
        Assert.Equal(129600, (int)TimeRangePreset.NinetyDays);
        Assert.Equal(525600, (int)TimeRangePreset.OneYear);
    }
}
