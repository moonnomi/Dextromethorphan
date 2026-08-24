using System.Windows.Input;
using Dextromethorphan.App.UI;

namespace Dextromethorphan.Tests;

public sealed class SliderAccessibilityTests
{
    [Theory]
    [InlineData(Key.Right, ModifierKeys.None, 41)]
    [InlineData(Key.Up, ModifierKeys.None, 41)]
    [InlineData(Key.Left, ModifierKeys.None, 39)]
    [InlineData(Key.Down, ModifierKeys.None, 39)]
    [InlineData(Key.PageUp, ModifierKeys.None, 50)]
    [InlineData(Key.PageDown, ModifierKeys.None, 30)]
    [InlineData(Key.Right, ModifierKeys.Control, 50)]
    [InlineData(Key.Left, ModifierKeys.Control, 30)]
    [InlineData(Key.Home, ModifierKeys.None, 0)]
    [InlineData(Key.End, ModifierKeys.None, 100)]
    public void KeyboardPolicyProvidesFineAndCoarseAdjustments(
        Key key,
        ModifierKeys modifiers,
        double expected)
    {
        var actual = SliderKeyboardStepPolicy.TargetFor(
            key,
            modifiers,
            value: 40,
            minimum: 0,
            maximum: 100,
            smallChange: 1,
            largeChange: 10);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(Key.Left, 0, 0)]
    [InlineData(Key.Right, 100, 100)]
    [InlineData(Key.PageDown, 2, 0)]
    [InlineData(Key.PageUp, 96, 100)]
    public void KeyboardPolicyClampsToSliderRange(
        Key key,
        double value,
        double expected)
    {
        var actual = SliderKeyboardStepPolicy.TargetFor(
            key,
            ModifierKeys.None,
            value,
            minimum: 0,
            maximum: 100,
            smallChange: 5,
            largeChange: 10);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void KeyboardPolicyLeavesUnrelatedGesturesForTheirOwner()
    {
        Assert.Null(SliderKeyboardStepPolicy.TargetFor(
            Key.Space,
            ModifierKeys.None,
            0.5,
            0,
            1,
            0.01,
            0.1));
        Assert.Null(SliderKeyboardStepPolicy.TargetFor(
            Key.Right,
            ModifierKeys.Alt,
            0.5,
            0,
            1,
            0.01,
            0.1));
    }
}
