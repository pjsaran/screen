using Captr.Core.Interop;

using Shouldly;

namespace Captr.Core.Tests.Interop;

/// <summary>
/// The hotkey string format is a public contract (settings.json and
/// <c>captr settings set recordToggleHotkey</c>), so these pin what it accepts: every
/// combination the settings page has ever written, and nothing that would register as
/// something the user did not ask for.
/// </summary>
public class HotkeyCombinationTests
{
    [Theory]
    [InlineData("Ctrl+Alt+F9", GlobalHotkeys.Modifiers.Control | GlobalHotkeys.Modifiers.Alt, 0x78u)]
    [InlineData("ctrl + alt + f10", GlobalHotkeys.Modifiers.Control | GlobalHotkeys.Modifiers.Alt, 0x79u)]
    [InlineData("Control+Shift+R", GlobalHotkeys.Modifiers.Control | GlobalHotkeys.Modifiers.Shift, 0x52u)]
    [InlineData("Win+Alt+D5", GlobalHotkeys.Modifiers.Windows | GlobalHotkeys.Modifiers.Alt, 0x35u)]
    [InlineData("Windows+NumPad7", GlobalHotkeys.Modifiers.Windows, 0x67u)]
    [InlineData("Ctrl+Alt+OemPlus", GlobalHotkeys.Modifiers.Control | GlobalHotkeys.Modifiers.Alt, 0xBBu)]
    [InlineData("Ctrl+Alt+PageUp", GlobalHotkeys.Modifiers.Control | GlobalHotkeys.Modifiers.Alt, 0x21u)]
    [InlineData("Ctrl+Alt+Prior", GlobalHotkeys.Modifiers.Control | GlobalHotkeys.Modifiers.Alt, 0x21u)]
    [InlineData("F24", GlobalHotkeys.Modifiers.None, 0x87u)]
    public void Parses_the_names_the_settings_page_writes(string text, GlobalHotkeys.Modifiers modifiers, uint virtualKey)
    {
        HotkeyCombination.TryParse(text, out HotkeyCombination combination).ShouldBeTrue();

        combination.Modifiers.ShouldBe(modifiers);
        combination.VirtualKey.ShouldBe(virtualKey);
    }

    [Fact]
    public void A_bare_digit_is_the_digit_key_not_an_enum_value()
    {
        // Read through WPF's Key enum, "5" was the enum VALUE 5 — the keypad's Clear
        // key — so "Ctrl+Alt+5" registered something nobody could find on a keyboard.
        HotkeyCombination.TryParse("Ctrl+Alt+5", out HotkeyCombination combination).ShouldBeTrue();

        combination.VirtualKey.ShouldBe(0x35u);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl++F9")]
    [InlineData("Hyper+F9")]
    [InlineData("Ctrl+Alt+NotAKey")]
    [InlineData("Ctrl+LeftShift")]
    [InlineData("Ctrl-Alt-F9")]
    [InlineData("Ctrl+Alt+112")]
    public void Refuses_anything_that_is_not_a_combination(string? text)
    {
        HotkeyCombination.TryParse(text, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("Ctrl+Alt+F9")]
    [InlineData("Alt+R")]
    [InlineData("Win+Shift+S")]
    [InlineData("F9")]
    [InlineData("Shift+F12")]
    [InlineData("")]
    public void Accepts_combinations_that_cannot_be_typed_by_accident(string text)
    {
        HotkeyCombination.DescribeProblem(text).ShouldBeNull();
    }

    [Theory]
    [InlineData("Shift+A")]
    [InlineData("A")]
    [InlineData("Space")]
    [InlineData("Shift+D1")]
    public void Refuses_a_combination_that_would_fire_while_typing(string text)
    {
        // A global hotkey is taken from every application: Shift+A would make a
        // capital A impossible to type anywhere, and start a recording instead.
        HotkeyCombination.DescribeProblem(text).ShouldNotBeNull().ShouldContain("while you type");
    }

    [Fact]
    public void An_unreadable_combination_says_how_to_write_one()
    {
        HotkeyCombination.DescribeProblem("Ctrl+Alt+Banana").ShouldNotBeNull().ShouldContain("Ctrl+Alt+F9");
    }

    [Theory]
    [InlineData("Ctrl+Alt+F9", "alt+ctrl+f9", true)]
    [InlineData("Ctrl+Alt+PageUp", "Ctrl+Alt+Prior", true)]
    [InlineData("Ctrl+Alt+F9", "Ctrl+Alt+F10", false)]
    [InlineData("Ctrl+Alt+F9", "Ctrl+Shift+F9", false)]
    [InlineData("", "", false)]
    [InlineData("Ctrl+Alt+Banana", "Ctrl+Alt+Banana", false)]
    public void Two_settings_are_the_same_combination_however_they_are_written(string first, string second, bool same)
    {
        HotkeyCombination.AreSame(first, second).ShouldBe(same);
    }
}
