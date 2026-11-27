using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;

namespace Ziwa.App.Controls;

/// <summary>
/// Standardized slider row with title, live unit readout, and double-click reset to default.
/// </summary>
public sealed class StudioSliderRow : TemplatedControl
{
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<StudioSliderRow, string?>(nameof(Label));

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<StudioSliderRow, double>(
            nameof(Value),
            defaultValue: 0.0,
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<StudioSliderRow, double>(nameof(Minimum), defaultValue: 0.0);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<StudioSliderRow, double>(nameof(Maximum), defaultValue: 100.0);

    public static readonly StyledProperty<double> TickFrequencyProperty =
        AvaloniaProperty.Register<StudioSliderRow, double>(nameof(TickFrequency), defaultValue: 1.0);

    public static readonly StyledProperty<double> DefaultValueProperty =
        AvaloniaProperty.Register<StudioSliderRow, double>(nameof(DefaultValue), defaultValue: 0.0);

    public static readonly StyledProperty<double> DisplayScaleProperty =
        AvaloniaProperty.Register<StudioSliderRow, double>(nameof(DisplayScale), defaultValue: 1.0);

    public static readonly StyledProperty<string> DisplayFormatProperty =
        AvaloniaProperty.Register<StudioSliderRow, string>(nameof(DisplayFormat), defaultValue: "0.##");

    public static readonly StyledProperty<string?> UnitProperty =
        AvaloniaProperty.Register<StudioSliderRow, string?>(nameof(Unit));

    public static readonly StyledProperty<string> FormattedValueProperty =
        AvaloniaProperty.Register<StudioSliderRow, string>(nameof(FormattedValue), defaultValue: "0");

    private Control? _resetTarget;

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double TickFrequency
    {
        get => GetValue(TickFrequencyProperty);
        set => SetValue(TickFrequencyProperty, value);
    }

    public double DefaultValue
    {
        get => GetValue(DefaultValueProperty);
        set => SetValue(DefaultValueProperty, value);
    }

    public double DisplayScale
    {
        get => GetValue(DisplayScaleProperty);
        set => SetValue(DisplayScaleProperty, value);
    }

    public string DisplayFormat
    {
        get => GetValue(DisplayFormatProperty);
        set => SetValue(DisplayFormatProperty, value);
    }

    public string? Unit
    {
        get => GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public string FormattedValue
    {
        get => GetValue(FormattedValueProperty);
        private set => SetValue(FormattedValueProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (_resetTarget is not null)
        {
            _resetTarget.DoubleTapped -= OnResetDoubleTapped;
        }

        _resetTarget = e.NameScope.Find<Control>("PART_ResetArea");
        if (_resetTarget is not null)
        {
            _resetTarget.DoubleTapped += OnResetDoubleTapped;
        }

        UpdateFormattedValue();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty ||
            change.Property == DisplayScaleProperty ||
            change.Property == DisplayFormatProperty ||
            change.Property == UnitProperty)
        {
            UpdateFormattedValue();
        }
    }

    private void UpdateFormattedValue()
    {
        double scaled = Value * DisplayScale;
        string num = scaled.ToString(DisplayFormat, CultureInfo.InvariantCulture);
        FormattedValue = string.IsNullOrEmpty(Unit) ? num : $"{num}{Unit}";
    }

    private void OnResetDoubleTapped(object? sender, TappedEventArgs e)
    {
        SetCurrentValue(ValueProperty, Math.Clamp(DefaultValue, Minimum, Maximum));
        e.Handled = true;
    }
}
