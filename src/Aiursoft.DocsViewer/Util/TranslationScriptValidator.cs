namespace Aiursoft.DocsViewer.Util;

/// <summary>
/// Heuristically checks whether AI-translated text contains characters
/// from the expected writing system of the target language.
///
/// This is a zero-cost guard against "the AI returned the original text
/// without translating" — a failure mode observed in ~5% of translations.
///
/// Latin-script languages (en↔de, en↔fr, etc.) share the same alphabet,
/// so they cannot be checked this way — the method returns null for them.
/// </summary>
public static class TranslationScriptValidator
{
    /// <summary>
    /// Returns <c>true</c> if <paramref name="text"/> contains enough characters
    /// from the writing system expected for <paramref name="targetCulture"/>.
    /// Returns <c>false</c> if it should but doesn't.
    /// Returns <c>null</c> when the target language uses Latin script (can't verify).
    /// </summary>
    public static bool? HasExpectedScript(string text, string targetCulture)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        return targetCulture switch
        {
            "zh-CN" or "zh-TW" or "zh-HK" => HasCjkUnified(text),
            "ja-JP"                       => HasHiraganaOrKatakana(text),
            "ko-KR"                       => HasHangul(text),
            "ar-SA"                       => HasArabic(text),
            "ru-RU" or "uk-UA"            => HasCyrillic(text),
            "th-TH"                       => HasThai(text),
            "el-GR"                       => HasGreek(text),
            "hi-IN"                       => HasDevanagari(text),
            _                             => null  // Latin-based, can't check
        };
    }

    /// <summary>
    /// Convenience: returns <c>true</c> <b>only</b> when the translation is
    /// definitely missing the expected script (i.e., the AI likely returned
    /// the original language without translating).
    ///
    /// <c>null</c> (Latin languages, can't check) is treated as "not failed".
    /// </summary>
    public static bool IsTranslationLikelyFailed(string translatedText, string targetCulture)
        => HasExpectedScript(translatedText, targetCulture) == false;

    // ── Per-script detectors ──────────────────────────────────────────

    /// <summary>
    /// Returns a sensible minimum character count for the given text length.
    /// Short text (e.g. nav titles) needs only 1–2 matching chars; long documents need 5–10.
    /// </summary>
    private static int Threshold(int textLength, int cap = 10)
        => Math.Max(1, Math.Min(cap, textLength / 5));

    private static bool HasCjkUnified(string text)
    {
        var min = Threshold(text.Length);
        var count = 0;
        foreach (var c in text)
            if (c >= '一' && c <= '鿿' && ++count >= min)
                return true;
        return false;
    }

    /// <summary>
    /// Hiragana (U+3040–U+309F) and Katakana (U+30A0–U+30FF).
    /// Japanese text almost always contains kana; kanji-only text is rare.
    /// </summary>
    private static bool HasHiraganaOrKatakana(string text)
    {
        var min = Threshold(text.Length, cap: 5);
        var count = 0;
        foreach (var c in text)
        {
            if ((c >= '぀' && c <= 'ゟ') ||
                (c >= '゠' && c <= 'ヿ'))
            {
                if (++count >= min)
                    return true;
            }
        }
        return false;
    }

    private static bool HasHangul(string text)
    {
        var min = Threshold(text.Length, cap: 5);
        var count = 0;
        foreach (var c in text)
            if (c >= '가' && c <= '힯' && ++count >= min)
                return true;
        return false;
    }

    private static bool HasArabic(string text)
    {
        var min = Threshold(text.Length, cap: 5);
        var count = 0;
        foreach (var c in text)
            if (c >= '؀' && c <= 'ۿ' && ++count >= min)
                return true;
        return false;
    }

    private static bool HasCyrillic(string text)
    {
        var min = Threshold(text.Length, cap: 5);
        var count = 0;
        foreach (var c in text)
            if (c >= 'Ѐ' && c <= 'ӿ' && ++count >= min)
                return true;
        return false;
    }

    private static bool HasThai(string text)
    {
        var min = Threshold(text.Length, cap: 5);
        var count = 0;
        foreach (var c in text)
            if (c >= '฀' && c <= '๿' && ++count >= min)
                return true;
        return false;
    }

    private static bool HasGreek(string text)
    {
        var min = Threshold(text.Length, cap: 5);
        var count = 0;
        foreach (var c in text)
            if (c >= 'Ͱ' && c <= 'Ͽ' && ++count >= min)
                return true;
        return false;
    }

    private static bool HasDevanagari(string text)
    {
        var min = Threshold(text.Length, cap: 5);
        var count = 0;
        foreach (var c in text)
            if (c >= 'ऀ' && c <= 'ॿ' && ++count >= min)
                return true;
        return false;
    }
}
