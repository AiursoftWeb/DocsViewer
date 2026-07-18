using Aiursoft.DocsViewer.Util;

namespace Aiursoft.DocsViewer.Tests;

[TestClass]
public class TranslationScriptValidatorTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // CJK (Chinese) tests
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void HasExpectedScript_ChineseText_ReturnsTrue()
    {
        var text = "这是中文翻译的内容，包含足够多的中文字符以通过检测。今天天气很好。";
        var result = TranslationScriptValidator.HasExpectedScript(text, "zh-CN");
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void HasExpectedScript_ChineseTraditional_ReturnsTrue()
    {
        var text = "這是繁體中文的翻譯內容，包含足夠多的繁體中文字符以通過檢測。";
        var result = TranslationScriptValidator.HasExpectedScript(text, "zh-TW");
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void HasExpectedScript_EnglishText_ChineseCulture_ReturnsFalse()
    {
        var text = "# Secure Boot Guide\n\nAnduinOS fully supports Secure Boot, allowing you to safely run third-party drivers.";
        var result = TranslationScriptValidator.HasExpectedScript(text, "zh-CN");
        Assert.IsFalse(result);
    }

    [TestMethod]
    public void HasExpectedScript_MixedCodeAndChinese_ReturnsTrue()
    {
        var text = "# 安装指南\n\n```bash\nsudo apt install nginx\n```\n\n安装完成后，请配置防火墙。这是中文。这是一段说明文字。" +
                   "更多中文内容在这里。系统会自动检测。还有更多的中文字符在这里以确保通过检测阈值。";
        var result = TranslationScriptValidator.HasExpectedScript(text, "zh-CN");
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void HasExpectedScript_FewCjkCharsBelowThreshold_ReturnsFalse()
    {
        var text = "This is mostly English with just one or two 中文字符 mixed in.";
        var result = TranslationScriptValidator.HasExpectedScript(text, "zh-CN");
        Assert.IsFalse(result);
    }

    [TestMethod]
    public void HasExpectedScript_ShortChineseLabel_PassesWithAdaptiveThreshold()
    {
        // "开始使用" = 4 chars. Text length = 4, threshold = Max(1, Min(10, 4/5)) = 1.
        var text = "开始使用";
        var result = TranslationScriptValidator.HasExpectedScript(text, "zh-CN");
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void HasExpectedScript_EmptyString_ReturnsNull()
    {
        var result = TranslationScriptValidator.HasExpectedScript("", "zh-CN");
        Assert.IsNull(result);
    }

    [TestMethod]
    public void HasExpectedScript_WhitespaceOnly_ReturnsNull()
    {
        var result = TranslationScriptValidator.HasExpectedScript("   \t\n   ", "zh-CN");
        Assert.IsNull(result);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Latin-script language tests (inconclusive)
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void HasExpectedScript_LatinCultureEnUS_ReturnsNull()
    {
        var result = TranslationScriptValidator.HasExpectedScript("Dies ist Deutsch", "en-US");
        Assert.IsNull(result);
    }

    [TestMethod]
    public void HasExpectedScript_LatinCultureDeDE_ReturnsNull()
    {
        var result = TranslationScriptValidator.HasExpectedScript("Dies ist Deutsch", "de-DE");
        Assert.IsNull(result);
    }

    [TestMethod]
    public void HasExpectedScript_LatinCultureFrFR_ReturnsNull()
    {
        var result = TranslationScriptValidator.HasExpectedScript("Ceci est du français", "fr-FR");
        Assert.IsNull(result);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Japanese tests
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void HasExpectedScript_JapaneseHiragana_ReturnsTrue()
    {
        var text = "これは日本語のテストです。ひらがなを含む文章を検出します。十分な長さの文章です。";
        var result = TranslationScriptValidator.HasExpectedScript(text, "ja-JP");
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void HasExpectedScript_JapaneseKatakana_ReturnsTrue()
    {
        var text = "コレハカタカナノテストデス。カタカナヲケンシュツシマス。ジュウブンナレングスノブンショウデス。";
        var result = TranslationScriptValidator.HasExpectedScript(text, "ja-JP");
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void HasExpectedScript_EnglishText_JapaneseCulture_ReturnsFalse()
    {
        var text = "This is English text with no Japanese characters at all.";
        var result = TranslationScriptValidator.HasExpectedScript(text, "ja-JP");
        Assert.IsFalse(result);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Korean tests
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void HasExpectedScript_KoreanHangul_ReturnsTrue()
    {
        var text = "이것은 한국어 테스트입니다. 한글을 포함한 문장을 감지합니다. 충분한 길이의 문장입니다.";
        var result = TranslationScriptValidator.HasExpectedScript(text, "ko-KR");
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void HasExpectedScript_EnglishText_KoreanCulture_ReturnsFalse()
    {
        var text = "This is English text with no Korean characters.";
        var result = TranslationScriptValidator.HasExpectedScript(text, "ko-KR");
        Assert.IsFalse(result);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Arabic tests
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void HasExpectedScript_Arabic_ReturnsTrue()
    {
        var text = "هذا نص عربي للاختبار. يحتوي على عدد كاف من الأحرف العربية. هذا نص عربي آخر.";
        var result = TranslationScriptValidator.HasExpectedScript(text, "ar-SA");
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void HasExpectedScript_EnglishText_ArabicCulture_ReturnsFalse()
    {
        var text = "This is English text with no Arabic characters.";
        var result = TranslationScriptValidator.HasExpectedScript(text, "ar-SA");
        Assert.IsFalse(result);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Cyrillic tests
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void HasExpectedScript_Russian_ReturnsTrue()
    {
        var text = "Это русский текст для проверки. Содержит достаточное количество кириллических символов.";
        var result = TranslationScriptValidator.HasExpectedScript(text, "ru-RU");
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void HasExpectedScript_Ukrainian_ReturnsTrue()
    {
        var text = "Це український текст для перевірки. Містить достатню кількість кириличних символів.";
        var result = TranslationScriptValidator.HasExpectedScript(text, "uk-UA");
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void HasExpectedScript_EnglishText_RussianCulture_ReturnsFalse()
    {
        var text = "This is English text with no Cyrillic characters.";
        var result = TranslationScriptValidator.HasExpectedScript(text, "ru-RU");
        Assert.IsFalse(result);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Thai tests
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void HasExpectedScript_Thai_ReturnsTrue()
    {
        var text = "นี่คือข้อความภาษาไทยสำหรับการทดสอบมีจำนวนตัวอักษรไทยเพียงพอสำหรับการตรวจจับข้อความภาษาไทยเพิ่มเติม";
        var result = TranslationScriptValidator.HasExpectedScript(text, "th-TH");
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void HasExpectedScript_EnglishText_ThaiCulture_ReturnsFalse()
    {
        var text = "This is English text with no Thai characters.";
        var result = TranslationScriptValidator.HasExpectedScript(text, "th-TH");
        Assert.IsFalse(result);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Greek tests
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void HasExpectedScript_Greek_ReturnsTrue()
    {
        var text = "Αυτό είναι ελληνικό κείμενο για δοκιμή. Περιέχει αρκετούς ελληνικούς χαρακτήρες. " +
                   "Περισσότερο ελληνικό κείμενο εδώ.";
        var result = TranslationScriptValidator.HasExpectedScript(text, "el-GR");
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void HasExpectedScript_EnglishText_GreekCulture_ReturnsFalse()
    {
        var text = "This is English text with no Greek characters.";
        var result = TranslationScriptValidator.HasExpectedScript(text, "el-GR");
        Assert.IsFalse(result);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Devanagari tests
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void HasExpectedScript_Hindi_ReturnsTrue()
    {
        var text = "यह हिंदी में एक परीक्षण पाठ है। इसमें पर्याप्त देवनागरी अक्षर हैं। " +
                   "अतिरिक्त हिंदी पाठ यहां जोड़ा गया है ताकि पर्याप्त अक्षर हों।";
        var result = TranslationScriptValidator.HasExpectedScript(text, "hi-IN");
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void HasExpectedScript_EnglishText_HindiCulture_ReturnsFalse()
    {
        var text = "This is English text with no Devanagari characters.";
        var result = TranslationScriptValidator.HasExpectedScript(text, "hi-IN");
        Assert.IsFalse(result);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IsTranslationLikelyFailed convenience wrapper
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void IsTranslationLikelyFailed_EnglishInChineseCulture_ReturnsTrue()
    {
        var result = TranslationScriptValidator.IsTranslationLikelyFailed(
            "# Secure Boot Guide\n\nAnduinOS fully supports Secure Boot.", "zh-CN");
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void IsTranslationLikelyFailed_ChineseText_ReturnsFalse()
    {
        var result = TranslationScriptValidator.IsTranslationLikelyFailed(
            "这是中文翻译的内容，包含足够多的中文字符以通过检测。今天天气很好。", "zh-CN");
        Assert.IsFalse(result);
    }

    [TestMethod]
    public void IsTranslationLikelyFailed_LatinCulture_ReturnsFalse()
    {
        // Latin-based cultures return null → treated as "not failed"
        var result = TranslationScriptValidator.IsTranslationLikelyFailed(
            "Some text in any language", "en-US");
        Assert.IsFalse(result);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Adaptive threshold tests
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void HasExpectedScript_VeryShortChineseText_ReturnsTrue()
    {
        // 5 chars → threshold = 1, so 1+ CJK still passes
        var text = "中文测试";
        var result = TranslationScriptValidator.HasExpectedScript(text, "zh-CN");
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void HasExpectedScript_MediumTextWithFewCjk_ReturnsFalse()
    {
        // 20 chars, threshold = 4, but only 1 CJK char → fails
        var text = "Hello world 中 and more English text here";
        var result = TranslationScriptValidator.HasExpectedScript(text, "zh-CN");
        Assert.IsFalse(result);
    }
}
