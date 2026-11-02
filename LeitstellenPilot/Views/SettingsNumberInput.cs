using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace LeitstellenPilot.Views;

public sealed class SettingsNumberInput : NumericUpDown
{
    static SettingsNumberInput() => ParsingNumberStyleProperty.OverrideDefaultValue<SettingsNumberInput>(NumberStyles.Integer);

    protected override Type StyleKeyOverride => typeof(NumericUpDown);

    protected override void OnTextChanged(string? oldValue, string? newValue)
    {
        // Empty text is a normal editing step, not a new value for a required setting.
        if (!string.IsNullOrWhiteSpace(newValue)) base.OnTextChanged(oldValue, newValue);
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        RestoreValueText();
        base.OnLostFocus(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter) RestoreValueText();
        base.OnKeyDown(e);
    }

    private void RestoreValueText()
    {
        if (Value is { } value) SetCurrentValue(TextProperty, value.ToString(FormatString, NumberFormat));
    }
}
