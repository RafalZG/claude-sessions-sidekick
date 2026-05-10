using ClaudeSessionsSidekick.Services;

namespace ClaudeSessionsSidekick.Tests.Services;

public class HotkeyHelperTests
{
    // ── TryParse ───────────────────────────────────────────────────

    public class TryParseTests
    {
        [Fact]
        public void TryParse_WinAltC_ParsesCorrectly()
        {
            // Act
            var result = HotkeyHelper.TryParse("Win+Alt+C", out var modifiers, out var vk);

            // Assert
            Assert.True(result);
            Assert.Equal(ModState.Win | ModState.LAlt, modifiers);
            Assert.Equal((uint)'C', vk);
        }

        [Fact]
        public void TryParse_CtrlShiftF5_ParsesCorrectly()
        {
            // Act
            var result = HotkeyHelper.TryParse("Ctrl+Shift+F5", out var modifiers, out var vk);

            // Assert
            Assert.True(result);
            Assert.Equal(ModState.LCtrl | ModState.LShift, modifiers);
            Assert.Equal((uint)0x74, vk); // VK_F5
        }

        [Fact]
        public void TryParse_AltA_MapsToLeftAlt()
        {
            // Arrange — plain "Alt" should map to LAlt (prevents AltGr conflicts)

            // Act
            var result = HotkeyHelper.TryParse("Alt+A", out var modifiers, out _);

            // Assert
            Assert.True(result);
            Assert.Equal(ModState.LAlt, modifiers);
        }

        [Fact]
        public void TryParse_CtrlZ_MapsToLeftCtrl()
        {
            // Act
            var result = HotkeyHelper.TryParse("Ctrl+Z", out var modifiers, out _);

            // Assert
            Assert.True(result);
            Assert.Equal(ModState.LCtrl, modifiers);
        }

        [Fact]
        public void TryParse_ExplicitRightModifiers()
        {
            // Arrange — explicit "RAlt" and "RCtrl" should map to right variants

            // Act
            var result = HotkeyHelper.TryParse("RCtrl+RAlt+A", out var modifiers, out _);

            // Assert
            Assert.True(result);
            Assert.Equal(ModState.RCtrl | ModState.RAlt, modifiers);
        }

        [Fact]
        public void TryParse_ExplicitLeftModifiers()
        {
            // Act
            var result = HotkeyHelper.TryParse("LCtrl+LAlt+S", out var modifiers, out _);

            // Assert
            Assert.True(result);
            Assert.Equal(ModState.LCtrl | ModState.LAlt, modifiers);
        }

        [Theory]
        [InlineData("Win+Alt+0", '0')]
        [InlineData("Ctrl+9", '9')]
        public void TryParse_NumberKeys_ReturnsCorrectVK(string input, char expectedKey)
        {
            // Act
            var result = HotkeyHelper.TryParse(input, out _, out var vk);

            // Assert
            Assert.True(result);
            Assert.Equal((uint)expectedKey, vk);
        }

        [Theory]
        [InlineData("Alt+F1", 0x70)]   // VK_F1
        [InlineData("Alt+F12", 0x7B)]  // VK_F12
        [InlineData("Ctrl+F5", 0x74)]  // VK_F5
        public void TryParse_FunctionKeys_ReturnsCorrectVK(string input, uint expectedVk)
        {
            // Act
            var result = HotkeyHelper.TryParse(input, out _, out var vk);

            // Assert
            Assert.True(result);
            Assert.Equal(expectedVk, vk);
        }

        [Theory]
        [InlineData("")]
        [InlineData("A")]
        [InlineData("JustOneToken")]
        public void TryParse_TooFewParts_ReturnsFalse(string input)
        {
            // Act
            var result = HotkeyHelper.TryParse(input, out _, out _);

            // Assert
            Assert.False(result);
        }

        [Fact]
        public void TryParse_InvalidModifier_ReturnsFalse()
        {
            // Act
            var result = HotkeyHelper.TryParse("Meta+A", out _, out _);

            // Assert
            Assert.False(result);
        }

        [Fact]
        public void TryParse_InvalidKey_ReturnsFalse()
        {
            // Act
            var result = HotkeyHelper.TryParse("Ctrl+F13", out _, out _);

            // Assert
            Assert.False(result);
        }

        [Fact]
        public void TryParse_SpacesAroundPlus_AreHandled()
        {
            // Act
            var result = HotkeyHelper.TryParse("Win + Alt + C", out var modifiers, out var vk);

            // Assert
            Assert.True(result);
            Assert.Equal(ModState.Win | ModState.LAlt, modifiers);
            Assert.Equal((uint)'C', vk);
        }

        [Fact]
        public void TryParse_CaseInsensitive()
        {
            // Act
            var result = HotkeyHelper.TryParse("win+alt+c", out var modifiers, out var vk);

            // Assert
            Assert.True(result);
            Assert.Equal(ModState.Win | ModState.LAlt, modifiers);
            Assert.Equal((uint)'C', vk);
        }

        [Fact]
        public void TryParse_BackwardCompatible_CtrlAlt_MapsToLeftOnly()
        {
            // Arrange — existing settings "Ctrl+Alt+2" should NOT fire on AltGr+2

            // Act
            var result = HotkeyHelper.TryParse("Ctrl+Alt+2", out var modifiers, out _);

            // Assert
            Assert.True(result);
            Assert.Equal(ModState.LCtrl | ModState.LAlt, modifiers);
            // AltGr sets RCtrl|RAlt which won't match LCtrl|LAlt
            Assert.False(modifiers.HasFlag(ModState.RAlt));
            Assert.False(modifiers.HasFlag(ModState.RCtrl));
        }

        [Fact]
        public void TryParse_NumpadKeys()
        {
            // Act
            var result = HotkeyHelper.TryParse("Ctrl+Num5", out _, out var vk);

            // Assert
            Assert.True(result);
            Assert.Equal((uint)0x65, vk); // VK_NUMPAD5
        }
    }

    // ── Build ──────────────────────────────────────────────────────

    public class BuildTests
    {
        [Fact]
        public void Build_TwoModifiers_FormatsCorrectly()
        {
            // Act
            var result = HotkeyHelper.Build("Win", "Alt", "C");

            // Assert
            Assert.Equal("Win+Alt+C", result);
        }

        [Fact]
        public void Build_OneModifier_FormatsCorrectly()
        {
            // Act
            var result = HotkeyHelper.Build("Ctrl", null, "Z");

            // Assert
            Assert.Equal("Ctrl+Z", result);
        }

        [Fact]
        public void Build_EmptySecondModifier_FormatsCorrectly()
        {
            // Act
            var result = HotkeyHelper.Build("Alt", "", "F5");

            // Assert
            Assert.Equal("Alt+F5", result);
        }
    }

    // ── Build → TryParse round-trip ────────────────────────────────

    [Theory]
    [InlineData("Win", "Alt", "C")]
    [InlineData("Ctrl", "Shift", "F1")]
    [InlineData("Alt", null, "Z")]
    [InlineData("Win", "Ctrl", "5")]
    [InlineData("LCtrl", "LAlt", "S")]
    [InlineData("RShift", null, "F12")]
    public void Build_ThenParse_RoundTrips(string mod1, string? mod2, string key)
    {
        // Arrange
        var built = HotkeyHelper.Build(mod1, mod2, key);

        // Act
        var parsed = HotkeyHelper.TryParse(built, out _, out _);

        // Assert
        Assert.True(parsed, $"Built hotkey '{built}' should be parseable");
    }
}
