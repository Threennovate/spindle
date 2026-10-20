using System.Globalization;

namespace Spindle.Windows.Model;

/// <summary>
/// The English Spindle writes dates in: en-GB with the short month names the Mac and web show. Windows' ICU writes
/// September as "Sept" there; Spindle writes "Sep".
/// </summary>
public static class BritishEnglish
{
    public static CultureInfo Culture { get; } = Make();

    private static CultureInfo Make()
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("en-GB").Clone();
        var invariant = CultureInfo.InvariantCulture.DateTimeFormat;
        culture.DateTimeFormat.AbbreviatedMonthNames = invariant.AbbreviatedMonthNames;
        culture.DateTimeFormat.AbbreviatedMonthGenitiveNames = invariant.AbbreviatedMonthGenitiveNames;
        return CultureInfo.ReadOnly(culture);
    }
}
