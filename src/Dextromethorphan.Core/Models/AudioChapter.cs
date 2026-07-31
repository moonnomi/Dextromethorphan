namespace Dextromethorphan.Core.Models;

public sealed record AudioChapter(
    string Title,
    TimeSpan Start,
    TimeSpan End)
{
    public string StartText =>
        Start.ToString(Start.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");
}
