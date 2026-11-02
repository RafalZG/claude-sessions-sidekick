using System.Globalization;
using ClaudeSessionsSidekick.Services;

namespace ClaudeSessionsSidekick.Tests.Services;

public class TokenFormatTests
{
    [Theory]
    [InlineData(long.MinValue, "0")]
    [InlineData(-1, "0")]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1_000, "1.0k")]
    [InlineData(999_900, "999.9k")]
    [InlineData(1_000_000, "1.0M")]
    [InlineData(417_500_000, "417.5M")]
    [InlineData(999_000_000, "999.0M")]
    [InlineData(1_000_000_000, "1.0B")]
    [InlineData(1_095_200_000, "1.1B")]
    [InlineData(12_345_000_000, "12.3B")]
    public void Compact_PicksTheRightMagnitudeTier(long tokens, string expected)
    {
        // Arrange — decimals follow the user's culture; pin it so the
        // assertion is stable on pl-PL machines too.
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            // Act
            var formatted = TokenFormat.Compact(tokens);

            // Assert
            Assert.Equal(expected, formatted);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
