using System.Globalization;

namespace OptLife.Core;

/// <summary>
/// The language of every text OptLife shows. Each text is written in place in both languages
/// (<c>L.T("italiano", "english")</c>), so a module stays one self-contained folder and a missing
/// translation is impossible to forget. Chosen once at start-up: "auto" follows the language of Windows
/// (Italian Windows → Italian, anything else → English); "it" and "en" force it.
/// Texts already saved in the history stay in the language they were written in.
/// </summary>
public static class L
{
    public const string Auto = "auto";
    public const string Italian = "it";
    public const string English = "en";

    private static readonly CultureInfo ItalianCulture = CultureInfo.GetCultureInfo("it-IT");
    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-GB");

    private static bool _english;
    [ThreadStatic] private static bool? _override;

    /// <summary>Italian until <see cref="Use"/> is called: tests and old callers keep the texts they always had.</summary>
    public static bool IsEnglish
    {
        get => _override ?? _english;
        private set => _english = value;
    }

    /// <summary>
    /// The same text in both languages (only on this thread, for the time of the call): lets a module recognise a text
    /// it saved in the other language and show it in the current one.
    /// </summary>
    public static (string Italian, string English) Both(Func<string> text)
    {
        try
        {
            _override = false;
            var italian = text();
            _override = true;
            return (italian, text());
        }
        finally
        {
            _override = null;
        }
    }

    public static string Code => IsEnglish ? English : Italian;

    /// <summary>Culture for dates and numbers in texts ("21 novembre" / "21 November").</summary>
    public static CultureInfo Culture => IsEnglish ? EnglishCulture : ItalianCulture;

    public static string T(string italian, string english) => IsEnglish ? english : italian;

    /// <summary>Applies a setting ("auto", "it", "en"; anything else counts as "auto").</summary>
    public static void Use(string? setting) => IsEnglish = Resolve(setting, CultureInfo.CurrentUICulture) == English;

    /// <summary>Pure: what a setting means on a Windows shown in <paramref name="windows"/>.</summary>
    public static string Resolve(string? setting, CultureInfo windows) => setting?.Trim().ToLowerInvariant() switch
    {
        Italian => Italian,
        English => English,
        _ => windows.TwoLetterISOLanguageName == Italian ? Italian : English,
    };
}
