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

using FluentAssertions;
using Jellyfin.Xtream.Library.Service.Pressbox;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service.Pressbox;

public class CategoryLanguageTests
{
    public static TheoryData<string, string[]> Cases => new()
    {
        { "US  NBC ᴴᴰ/ᴿᴬᵂ ⁶⁰ᶠᵖˢ", new[] { "en" } },
        { "UK  SKY SPORTS", new[] { "en" } },
        { "EN - ACTION", new[] { "en" } },
        { "NETFLIX MOVIES", new[] { "en" } },
        { "ENGLISH SERIES (TR)", new[] { "en" } },
        { "ES - PELÍCULAS", new[] { "es" } },
        { "LA - INFANTILES", new[] { "es" } },
        { "ESPAÑA SERIES", new[] { "es" } },
        { "US  LATINO", new[] { "es" } },
        { "QUÉBEC SERIES", new[] { "fr" } },
        { "BELGIUM SERIES (FR)", new[] { "fr" } },
        { "BE - DOCU-MOVIES (NL)", new[] { "nl" } },
        { "BE  GENERAL ᴴᴰ/ᴿᴬᵂ", new[] { "nl", "fr" } },
        { "CH  CHINA", new[] { "zh" } },
        { "NORDIC FILM NEW RELEASE", new[] { "sv", "da", "no", "fi" } },
        { "PT/BR SERIE", new[] { "pt" } },
        { "GREECE TURKISH SERIES", new[] { "tr" } },
        { "GR - ΕΛΛΗΝΙΚΕΣ ΤΑΙΝΕΣ", new[] { "el" } },
        { "BG - БЪЛГАРИЯ ФИЛМ", new[] { "bg" } },
        { "AR  AL FAJER ᴿᴬᵂ ▶ الفجر ⚽️", new[] { "ar" } },
        { "مسلسلات تركية مدبلجة", new[] { "ar" } },
        { "IR - PERSIAN قدیمی", new[] { "fa" } },
        { "HEBREW EN SERIES", new[] { "he" } },
        { "ASIA  24/7 BOLLYWOOD", new[] { "hi" } },
        { "ASIA  KANNADA", new[] { "kn" } },
        { "AFR  SOMALIA", new[] { "so" } },
        { "SUOMEN LAPSET", new[] { "fi" } },
        { "ÍSLANDS MOVIES", new[] { "is" } },
        { "Animation;Kids", System.Array.Empty<string>() },
        { "News", System.Array.Empty<string>() },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void LanguagesOf_ReadsTheLanguageFromTheName(string category, string[] expected)
    {
        CategoryLanguage.LanguagesOf(category).Should().Equal(expected);
    }

    [Fact]
    public void InLanguages_SelectsOnlyCategoriesThatNameAChosenLanguage()
    {
        CategoryLanguage.InLanguages("News", new[] { "en" }).Should().BeFalse("a category that names no language is not known to be in one");
        CategoryLanguage.InLanguages("ES - PELÍCULAS", new[] { "en" }).Should().BeFalse();
        CategoryLanguage.InLanguages("ES - PELÍCULAS", new[] { "en", "es" }).Should().BeTrue();
    }
}
