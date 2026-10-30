using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;

namespace MarqSpec.Mcp.TopstepX.Tests.Configuration;

/// <summary>
/// What may be placed in a log record: controls, line separators, bidi marks and a literal backslash are
/// escaped, the escaped text is capped, and a missing value stays missing (gh#747).
/// </summary>
public sealed class LogTextTests
{
    [Theory]
    [InlineData('\r', "\\u000D")]
    [InlineData('\n', "\\u000A")]
    [InlineData('\0', "\\u0000")]
    [InlineData('\t', "\\u0009")]
    [InlineData('\u001b', "\\u001B")]
    [InlineData('\u007f', "\\u007F")]
    [InlineData('\u0085', "\\u0085")]
    public void EachControlClass_IsEscaped(char input, string expected)
    {
        // C0 (CR, LF, NUL, TAB, ESC), DEL, and a C1 (NEL). One escape, and no raw control left.
        string sanitized = LogText.Sanitize(input.ToString());

        sanitized.Should().Be(expected);
        sanitized.Any(char.IsControl).Should().BeFalse();
    }

    [Theory]
    [InlineData('\u2028', "\\u2028")]
    [InlineData('\u2029', "\\u2029")]
    public void ALineOrParagraphSeparator_IsEscaped(char input, string expected)
    {
        // U+2028 and U+2029 break a line in some log viewers and are not in \p{Cc}.
        string sanitized = LogText.Sanitize(input.ToString());

        sanitized.Should().Be(expected);
        sanitized.Should().NotContain(input.ToString());
    }

    [Theory]
    [InlineData('\u202A', "\\u202A")]
    [InlineData('\u202B', "\\u202B")]
    [InlineData('\u202C', "\\u202C")]
    [InlineData('\u202D', "\\u202D")]
    [InlineData('\u202E', "\\u202E")]
    [InlineData('\u2066', "\\u2066")]
    [InlineData('\u2067', "\\u2067")]
    [InlineData('\u2068', "\\u2068")]
    [InlineData('\u2069', "\\u2069")]
    public void ABidiOverrideOrIsolate_IsEscaped(char input, string expected)
    {
        // Overrides U+202A–U+202E and isolates U+2066–U+2069 can hide the rest of a log line.
        string sanitized = LogText.Sanitize(input.ToString());

        sanitized.Should().Be(expected);
        sanitized.Should().NotContain(input.ToString());
    }

    [Fact]
    public void ALiteralBackslash_CannotPassAsAnEscape()
    {
        // The six characters \u000D must not survive as the escape a real CR becomes.
        LogText.Sanitize("\\u000D").Should().Be("\\u005Cu000D");
        LogText.Sanitize("\r").Should().Be("\\u000D");
        LogText.Sanitize("\\u000D").Should().NotBe(LogText.Sanitize("\r"));
        LogText.Sanitize("a\\b").Should().Be("a\\u005Cb");
    }

    [Fact]
    public void AnEightKilobyteRunOfControls_IsCappedAfterEscaping()
    {
        // 8192 controls × 6 characters is 49152. The cap applies to that expansion, not to the input.
        const int inputLength = 8192;
        string result = LogText.Sanitize(new string('\r', inputLength));

        (inputLength * 6).Should().Be(49_152);
        result.Should().EndWith(LogText.TruncationMarker);
        string body = result[..^LogText.TruncationMarker.Length];
        body.Length.Should().BeLessThanOrEqualTo(LogText.MaxSanitizedLength);
        body.Length.Should().BePositive();
        (body.Length % 6).Should().Be(0, "the cap ends on an escape boundary");
        result.Length.Should().BeLessThan(inputLength);
        result.Any(char.IsControl).Should().BeFalse();
        result.Should().NotContain("\u2028").And.NotContain("\u2029");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AMissingValue_StaysMissing(string? value)
    {
        LogText.Sanitize(value).Should().BeEmpty();
    }

    [Fact]
    public void OrdinaryText_IsUnchanged()
    {
        const string ordinary = "POST /mcp: no bearer token.";

        LogText.Sanitize(ordinary).Should().Be(ordinary);
    }

    [Fact]
    public void OrdinaryTextAtTheCap_IsNotMarked()
    {
        string value = new('a', LogText.MaxSanitizedLength);

        LogText.Sanitize(value).Should().Be(value);
    }

    [Fact]
    public void OrdinaryTextPastTheCap_IsMarkedTruncated()
    {
        string value = new('a', LogText.MaxSanitizedLength + 1);

        LogText.Sanitize(value).Should().Be(value[..LogText.MaxSanitizedLength] + LogText.TruncationMarker);
    }

    [Theory]
    [InlineData(0x200E, "\\u200E")]
    [InlineData(0x200F, "\\u200F")]
    [InlineData(0x061C, "\\u061C")]
    [InlineData(0x200B, "\\u200B")]
    [InlineData(0xFEFF, "\\uFEFF")]
    public void AnInvisibleFormatMark_IsEscaped(int codePoint, string expected)
    {
        // LRM, RLM, ALM, ZWSP and BOM hide or reorder the rest of a line and are not in \p{Cc}.
        string sanitized = LogText.Sanitize(((char)codePoint).ToString());

        sanitized.Should().Be(expected);
        sanitized.Should().NotContain(((char)codePoint).ToString());
    }

    [Theory]
    [InlineData(0xD800, "\\uD800")]
    [InlineData(0xDBFF, "\\uDBFF")]
    [InlineData(0xDC00, "\\uDC00")]
    [InlineData(0xDFFF, "\\uDFFF")]
    public void ALoneSurrogate_IsEscaped(int codePoint, string expected)
    {
        string sanitized = LogText.Sanitize(((char)codePoint).ToString());

        sanitized.Should().Be(expected);
        char.IsSurrogate(sanitized, 0).Should().BeFalse();
    }

    [Fact]
    public void AWellFormedEmoji_StaysOneCharacter()
    {
        // A pair is two Cs units. Escaping each would hide the character the cap has to keep intact.
        const string emoji = "\U0001F600";

        LogText.Sanitize("hi " + emoji).Should().Be("hi " + emoji);
    }

    [Theory]
    [InlineData(1022)]
    [InlineData(1023)]
    [InlineData(1024)]
    public void AnEmojiOnTheCapBoundary_IsNotSplit(int index)
    {
        const string emoji = "\U0001F600";
        string input = new string('a', index) + emoji + "zzz";

        string result = LogText.Sanitize(input);

        result.Should().EndWith(LogText.TruncationMarker);
        string body = result[..^LogText.TruncationMarker.Length];
        (body.Length == 0 || !char.IsHighSurrogate(body[^1])).Should().BeTrue();
        if (index == 1022)
        {
            body.Should().Be(new string('a', 1022) + emoji);
        }
        else if (index == 1023)
        {
            body.Should().Be(new string('a', 1023));
        }
        else
        {
            body.Should().Be(new string('a', 1024));
        }
    }

    [Fact]
    public void ACarriageReturnOnTheEscapeBoundary_IsNotSplit()
    {
        // 1020 ordinary characters leave four places under the cap, and a CR expands to six. The cut has
        // to end before that escape, not inside it.
        string input = new string('a', 1020) + "\r";

        string result = LogText.Sanitize(input);

        result.Should().Be(new string('a', 1020) + LogText.TruncationMarker);
        result.Any(char.IsControl).Should().BeFalse();
    }

    [Fact]
    public void ACallerSuppliedTruncationMarker_IsNotTheCap()
    {
        // The exact marker is only the suffix Cap appends. The caller's opening bracket is escaped.
        LogText.Sanitize(LogText.TruncationMarker).Should().Be(" \\u005Btruncated]");

        string input = LogText.TruncationMarker + new string('a', LogText.MaxSanitizedLength);
        string result = LogText.Sanitize(input);
        string body = result[..^LogText.TruncationMarker.Length];

        result.Should().EndWith(LogText.TruncationMarker);
        body.Should().NotContain(LogText.TruncationMarker);
        body.Should().StartWith(" \\u005Btruncated]");
    }

    [Fact]
    public void APlainMarkerAndOneWithABackslash_StayDistinct()
    {
        // Escaping the bracket, not rewriting the marker first, keeps these two inputs apart.
        string plain = LogText.Sanitize("abc [truncated]");
        string withBackslash = LogText.Sanitize("abc [\\truncated]");

        plain.Should().Be("abc \\u005Btruncated]");
        withBackslash.Should().Be("abc \\u005B\\u005Ctruncated]");
        plain.Should().NotBe(withBackslash);
    }

    [Fact]
    public void ACallerSuppliedMarkerAtTheCapBoundary_IsNotTheCap()
    {
        // The marker fills the last places under the cap. Escaping the bracket grows it past the cut,
        // and what is kept must not still read as the suffix Cap appends.
        string marker = LogText.TruncationMarker;
        int lead = LogText.MaxSanitizedLength - marker.Length;
        string input = new string('a', lead) + marker;

        string result = LogText.Sanitize(input);
        string body = result[..^marker.Length];

        result.Should().EndWith(marker);
        body.Should().Be(new string('a', lead) + " \\u005Btrunc");
        body.Should().NotContain(marker);
        body.Should().NotContain("[");
    }
}
