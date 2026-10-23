// Copyright (C) 2024  Roland Breitschaft

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Xtream.Library.Service.Pressbox;

/// <summary>
/// A language a provider category can be in.
/// </summary>
/// <param name="Id">Short language id, e.g. "en".</param>
/// <param name="Name">Display name, e.g. "English".</param>
public sealed record CategoryLanguageInfo(string Id, string Name);

/// <summary>
/// Reads which language a provider category is in from its name, the way providers write them:
/// a country or language tag in front ("US| NEWS", "|EN| ACTION", "PT/BR - SERIE"), a country or
/// language spelled out ("ESPAÑA SERIES", "HINDI WEB SERIES", "BELGIUM KIDS (FR)"), or the writing
/// itself (Arabic, Greek, Hebrew...). A name that says none of that but is written in English
/// ("NETFLIX MOVIES") is English. Ported from PressBox <c>languages.ts</c>.
/// </summary>
public static partial class CategoryLanguage
{
    private static readonly LanguageRow[] Table =
    [
        new("en", "English", ["US", "USA", "UK", "GB", "EN", "ENG", "CA", "AU", "IE", "NZ"], ["ENGLISH"], ["CANADA", "AUSTRALIA", "IRELAND", "CARIBBEAN"]),
        new("es", "Spanish", ["ES", "SP", "MX", "LA", "LAT", "CO", "CL", "PE", "VE", "BO", "EC", "UY", "PY", "GT", "HN", "SV", "NI", "PA", "DO", "CU", "PR"], ["SPANISH", "ESPAÑOL", "ESPANOL", "CASTELLANO", "LATINO", "LATINA", "LATAM"], ["LATIN", "ESPAÑA", "ESPANA", "SPAIN", "MEXICO", "MÉXICO", "ARGENTINA", "COLOMBIA", "CHILE", "PERU", "VENEZUELA", "BOLIVIA", "ECUADOR", "URUGUAY", "PARAGUAY"]),
        new("fr", "French", ["FR", "FRA", "QC"], ["FRENCH", "FRANÇAIS", "FRANCAIS", "VOSTFR", "VF", "(FR)"], ["FRANCE", "QUÉBEC", "QUEBEC", "HAITI"]),
        new("de", "German", ["DE", "GER", "AT"], ["GERMAN", "DEUTSCH", "DOKU", "(DE)"], ["GERMANY", "DEUTSCHLAND", "AUSTRIA", "ÖSTERREICH", "JOYN"]),
        new("it", "Italian", ["IT", "ITA"], ["ITALIAN", "ITALIANO", "(IT)"], ["ITALY", "ITALIA"]),
        new("pt", "Portuguese", ["PT", "BR", "POR", "PT/BR"], ["PORTUGUESE", "PORTUGUÊS", "(PT)"], ["PORTUGAL", "BRAZIL", "BRASIL", "PT/BR"]),
        new("nl", "Dutch", ["NL"], ["DUTCH", "NEDERLANDS", "VLAAMS", "(NL)"], ["NETHERLANDS", "NEDERLAND", "HOLLAND", "VIDEOLAND", "SURINAME"]),
        new("sv", "Swedish", ["SE", "SWE"], ["SWEDISH", "SVENSK", "SVENSKA", "(SE)", "(SV)"], ["SWEDEN", "SVERIGE"]),
        new("da", "Danish", ["DK", "DAN"], ["DANISH", "DANSK", "DANSKE", "(DK)"], ["DENMARK", "DANMARK"]),
        new("no", "Norwegian", ["NO", "NOR"], ["NORWEGIAN", "NORSK", "(NO)"], ["NORWAY", "NORGE"]),
        new("fi", "Finnish", ["FI", "FIN"], ["FINNISH", "SUOMEN", "(FI)"], ["FINLAND", "SUOMI"]),
        new("is", "Icelandic", ["IS"], ["ICELANDIC", "ÍSLENSK", "ÍSLANDS"], ["ICELAND", "ÍSLAND"]),
        new("pl", "Polish", ["PL", "POL"], ["POLISH", "POLSKI", "(PL)"], ["POLAND", "POLSKA"]),
        new("tr", "Turkish", ["TR", "TUR"], ["TURKISH", "TURKSIH", "TÜRKÇE", "(TR)"], ["TURKEY", "TÜRKIYE", "TURKIYE"]),
        new("el", "Greek", ["GR", "GRE", "CY"], ["GREEK", "ΕΛΛΗΝΙΚΑ", "(GR)"], ["GREECE", "CYPRUS"]),
        new("he", "Hebrew", ["IL"], ["HEBREW"], ["ISRAEL"]),
        new("ru", "Russian", ["RU", "RUS"], ["RUSSIAN", "RUSSAIN", "(RU)"], ["RUSSIA"]),
        new("uk", "Ukrainian", ["UA"], ["UKRAINIAN"], ["UKRAINE"]),
        new("ar", "Arabic", ["AR", "ARB"], ["ARABIC", "(AR)"], ["ARABIA", "MENA", "EGYPT", "MOROCCO", "ALGERIA", "TUNISIA", "LEBANON", "SYRIA", "IRAQ", "JORDAN"]),
        new("fa", "Persian", ["IR"], ["PERSIAN", "FARSI", "DARI"], ["IRAN", "AFGHANISTAN"]),
        new("ku", "Kurdish", ["KU", "KRD"], ["KURDISH", "SORANI", "BADINI"], ["KURDISTAN"]),
        new("hi", "Hindi", ["IN", "IND"], ["HINDI", "BHOJPURI"], ["INDIA", "INDIAN", "BOLLYWOOD"]),
        new("ta", "Tamil", [], ["TAMIL"], []),
        new("te", "Telugu", [], ["TELUGU", "TELEGU"], []),
        new("pa", "Punjabi", [], ["PUNJABI"], []),
        new("kn", "Kannada", [], ["KANNADA"], []),
        new("ml", "Malayalam", [], ["MALAYALAM"], []),
        new("mr", "Marathi", [], ["MARATHI"], []),
        new("gu", "Gujarati", [], ["GUJARATI"], []),
        new("ne", "Nepali", [], ["NEPALI"], ["NEPAL"]),
        new("si", "Sinhala", [], ["SINHALA"], []),
        new("am", "Amharic", [], ["AMHARIC"], ["ETHIOPIA", "ETHOPIA", "ERITREA"]),
        new("ur", "Urdu", ["PK"], ["URDU"], ["PAKISTAN"]),
        new("bn", "Bengali", ["BN"], ["BENGALI", "BANGLA", "BANGAL"], ["BANGLADESH"]),
        new("sq", "Albanian", ["AL"], ["ALBANIAN", "SHQIP", "SHQIPTAR"], ["ALBANIA", "KOSOVO"]),
        new("sh", "Serbo-Croatian", ["EXYU", "HR", "SR", "RS", "BH", "BA", "CG", "ME"], ["SERBIAN", "CROATIAN", "BOSNIAN", "BALKAN"], ["SERBIA", "CROATIA", "BOSNIA", "MONTENEGRO", "YUGOSLAVIA"]),
        new("mk", "Macedonian", ["MK"], ["MACEDONIAN"], ["MACEDONIA"]),
        new("sl", "Slovenian", ["SI"], ["SLOVENIAN"], ["SLOVENIA"]),
        new("bg", "Bulgarian", ["BG"], ["BULGARIAN"], ["BULGARIA", "BULGARIYA"]),
        new("ro", "Romanian", ["RO"], ["ROMANIAN"], ["ROMANIA", "MOLDOVA"]),
        new("hu", "Hungarian", ["HU"], ["HUNGARIAN", "MAGYAR"], ["HUNGARY"]),
        new("cs", "Czech", ["CZ"], ["CZECH"], []),
        new("sk", "Slovak", ["SK"], ["SLOVAK"], ["SLOVAKIA"]),
        new("lt", "Lithuanian", ["LT"], ["LITHUANIAN"], ["LITHUANIA"]),
        new("lv", "Latvian", ["LV"], ["LATVIAN"], ["LATVIA"]),
        new("et", "Estonian", ["EE"], ["ESTONIAN"], ["ESTONIA"]),
        new("ka", "Georgian", ["GE"], ["GEORGIAN"], []),
        new("hy", "Armenian", ["AM"], ["ARMENIAN"], ["ARMENIA"]),
        new("az", "Azerbaijani", ["AZ"], ["AZERBAIJANI", "AZERI"], ["AZERBAIJAN"]),
        new("uz", "Uzbek", ["UZ"], ["UZBEK"], ["UZBEKISTAN"]),
        new("kk", "Kazakh", ["KZ"], ["KAZAKH"], ["KAZAKHSTAN", "KAZACHSTAN"]),
        new("fil", "Filipino", ["PH"], ["TAGALOG", "FILIPINO", "PINOY"], ["PHILIPPINES"]),
        new("id", "Indonesian", ["ID"], ["INDONESIAN", "BAHASA"], ["INDONESIA"]),
        new("ms", "Malay", ["MY"], ["MALAY"], ["MALAYSIA"]),
        new("mt", "Maltese", ["MT"], ["MALTESE"], ["MALTA"]),
        new("so", "Somali", ["SO"], ["SOMALI"], ["SOMALIA"]),
        new("zh", "Chinese", ["CN", "HK", "TW"], ["CHINESE", "MANDARIN", "CANTONESE"], ["CHINA", "HONGKONG", "TAIWAN"]),
        new("ja", "Japanese", ["JP"], ["JAPANESE"], ["JAPAN"]),
        new("ko", "Korean", ["KR"], ["KOREAN"], ["KOREA"]),
        new("th", "Thai", ["TH"], ["THAI"], ["THAILAND"]),
        new("vi", "Vietnamese", ["VI", "VN"], ["VIETNAMESE"], ["VIETNAM"]),
    ];

    // Places that speak more than one language, or a region: what's in them, unless the name says which.
    private static readonly Dictionary<string, string[]> Shared = new(StringComparer.Ordinal)
    {
        ["BE"] = ["nl", "fr"],
        ["BELGIUM"] = ["nl", "fr"],
        ["BELGIQUE"] = ["nl", "fr"],
        ["CH"] = ["de", "fr", "it"],
        ["SWITZERLAND"] = ["de", "fr", "it"],
        ["SWISS"] = ["de", "fr", "it"],
        ["NORDIC"] = ["sv", "da", "no", "fi"],
        ["SCANDINAVIA"] = ["sv", "da", "no", "fi"],
        ["AF"] = ["en"],
        ["AFR"] = ["en"],
        ["AFRICA"] = ["en"],
    };

    // Writing that is one language's own, when nothing else says.
    private static readonly (Regex Script, string Id)[] Scripts =
    [
        (ArabicScript(), "ar"),
        (HebrewScript(), "he"),
        (GreekScript(), "el"),
        (CyrillicScript(), "ru"),
        (ThaiScript(), "th"),
        (HangulScript(), "ko"),
        (KanaScript(), "ja"),
        (HanScript(), "zh"),
        (DevanagariScript(), "hi"),
        (BengaliScript(), "bn"),
        (TamilScript(), "ta"),
        (GeorgianScript(), "ka"),
        (ArmenianScript(), "hy"),
    ];

    private static readonly Dictionary<string, string[]> Tags = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> Spoken = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string[]> Places = new(StringComparer.Ordinal);

    static CategoryLanguage()
    {
        foreach (var l in Table)
        {
            foreach (var t in l.Tags)
            {
                Tags[t] = [l.Id];
            }

            foreach (var w in l.Language)
            {
                Spoken[w] = l.Id;
            }

            foreach (var w in l.Place)
            {
                Places[w] = [l.Id];
            }
        }
    }

    /// <summary>
    /// Gets every language this detector knows, in table order.
    /// </summary>
    public static IReadOnlyList<CategoryLanguageInfo> All { get; } = Table.Select(r => new CategoryLanguageInfo(r.Id, r.Name)).ToArray();

    /// <summary>
    /// Returns the language ids a category belongs to; empty when the name says nothing about language.
    /// </summary>
    /// <param name="category">The category name, as shown (provider separators may already be cleaned).</param>
    /// <returns>Zero or more language ids.</returns>
    public static IReadOnlyList<string> LanguagesOf(string? category)
    {
        var name = (category ?? string.Empty).Trim();
        if (name.Length == 0 || GenreList().IsMatch(name) || name == "Undefined")
        {
            return Array.Empty<string>();
        }

        string? said = null;
        string[]? place = null;
        string[]? shared = null;

        void From(string word, Dictionary<string, string[]> map)
        {
            if (Shared.TryGetValue(word, out var s))
            {
                shared ??= s;
            }
            else if (map.TryGetValue(word, out var p))
            {
                place ??= p;
            }
        }

        var tagMatch = LeadingTag().Match(name);
        if (tagMatch.Success)
        {
            From(tagMatch.Groups[1].Value, Tags);
        }

        var upper = name.ToUpperInvariant();
        foreach (Match m in Words().Matches(upper))
        {
            var word = m.Value;
            if (said is null && Spoken.TryGetValue(word, out var lang))
            {
                said = lang;
            }

            From(word, Places);
        }

        // Two-word places.
        if (place is null && SriLanka().IsMatch(upper))
        {
            place = ["si"];
        }

        if (said is not null)
        {
            return [said];
        }

        if (place is not null)
        {
            return place;
        }

        if (shared is not null)
        {
            return shared;
        }

        foreach (var (script, id) in Scripts)
        {
            if (script.IsMatch(name))
            {
                return [id];
            }
        }

        return HasLatinPair().IsMatch(name) ? ["en"] : Array.Empty<string>();
    }

    /// <summary>
    /// Whether the category belongs to any of the given languages.
    /// </summary>
    /// <param name="category">The category name.</param>
    /// <param name="ids">Language ids to test against.</param>
    /// <returns>True when at least one language of the category is in <paramref name="ids"/>.</returns>
    public static bool InLanguages(string? category, IReadOnlyCollection<string> ids)
    {
        return LanguagesOf(category).Any(ids.Contains);
    }

    // A tag in front: two to four capitals (or "PT/BR"), then the provider's separator.
    [GeneratedRegex(@"^\s*[\[(|]?\s*([A-Z]{2,4}(?:/[A-Z]{2})?)\s*[\])]?(?:\s{2,}|\s*[|:]|\s*-\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingTag();

    // Words, a parenthesised code ("(FR)") kept whole.
    [GeneratedRegex(@"\([A-Z]{2}\)|PT/BR|[\p{L}+]+", RegexOptions.CultureInvariant)]
    private static partial Regex Words();

    // The public playlist's genres ("Animation;Kids") say nothing of language.
    [GeneratedRegex(@"^[A-Z][a-z]+(?:;[A-Z][a-z]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex GenreList();

    [GeneratedRegex(@"\bSRI LANKA\b", RegexOptions.CultureInvariant)]
    private static partial Regex SriLanka();

    [GeneratedRegex("[A-Za-z]{2}", RegexOptions.CultureInvariant)]
    private static partial Regex HasLatinPair();

    [GeneratedRegex(@"\p{IsArabic}", RegexOptions.CultureInvariant)]
    private static partial Regex ArabicScript();

    [GeneratedRegex(@"\p{IsHebrew}", RegexOptions.CultureInvariant)]
    private static partial Regex HebrewScript();

    [GeneratedRegex(@"\p{IsGreek}", RegexOptions.CultureInvariant)]
    private static partial Regex GreekScript();

    [GeneratedRegex(@"\p{IsCyrillic}", RegexOptions.CultureInvariant)]
    private static partial Regex CyrillicScript();

    [GeneratedRegex(@"\p{IsThai}", RegexOptions.CultureInvariant)]
    private static partial Regex ThaiScript();

    [GeneratedRegex(@"[\p{IsHangulJamo}\p{IsHangulSyllables}\p{IsHangulCompatibilityJamo}]", RegexOptions.CultureInvariant)]
    private static partial Regex HangulScript();

    [GeneratedRegex(@"[\p{IsHiragana}\p{IsKatakana}]", RegexOptions.CultureInvariant)]
    private static partial Regex KanaScript();

    [GeneratedRegex(@"\p{IsCJKUnifiedIdeographs}", RegexOptions.CultureInvariant)]
    private static partial Regex HanScript();

    [GeneratedRegex(@"\p{IsDevanagari}", RegexOptions.CultureInvariant)]
    private static partial Regex DevanagariScript();

    [GeneratedRegex(@"\p{IsBengali}", RegexOptions.CultureInvariant)]
    private static partial Regex BengaliScript();

    [GeneratedRegex(@"\p{IsTamil}", RegexOptions.CultureInvariant)]
    private static partial Regex TamilScript();

    [GeneratedRegex(@"\p{IsGeorgian}", RegexOptions.CultureInvariant)]
    private static partial Regex GeorgianScript();

    [GeneratedRegex(@"\p{IsArmenian}", RegexOptions.CultureInvariant)]
    private static partial Regex ArmenianScript();

    private sealed record LanguageRow(string Id, string Name, string[] Tags, string[] Language, string[] Place);
}
