using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;

namespace Ziwa.App.Controls;

/// <summary>
/// Compact studio numeric field with inline unit suffix, keyboard/wheel stepping,
/// and horizontal label-scrubbing—eliminating oversized platform spinner buttons in narrow inspector columns.
/// </summary>
public sealed class StudioNumberField : TemplatedControl
{
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<StudioNumberField, string?>(nameof(Label));

    public static readonly StyledProperty<string?> UnitProperty =
        AvaloniaProperty.Register<StudioNumberField, string?>(nameof(Unit));

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<StudioNumberField, double>(
            nameof(Value),
            defaultValue: 0.0,
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<StudioNumberField, double>(nameof(Minimum), defaultValue: double.NegativeInfinity);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<StudioNumberField, double>(nameof(Maximum), defaultValue: double.PositiveInfinity);

    public static readonly StyledProperty<double> IncrementProperty =
        AvaloniaProperty.Register<StudioNumberField, double>(nameof(Increment), defaultValue: 1.0);

    public static readonly StyledProperty<string> FormatStringProperty =
        AvaloniaProperty.Register<StudioNumberField, string>(nameof(FormatString), defaultValue: "0.#");

    public static readonly StyledProperty<string?> AccessibleNameProperty =
        AvaloniaProperty.Register<StudioNumberField, string?>(nameof(AccessibleName));

    private TextBox? _textBox;
    private Control? _scrubHandle;
    private bool _isSyncingText;
    private bool _isScrubbing;
    private Point _scrubStartPoint;
    private double _scrubStartValue;

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string? Unit
    {
        get => GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
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

    public double Increment
    {
        get => GetValue(IncrementProperty);
        set => SetValue(IncrementProperty, value);
    }

    public string FormatString
    {
        get => GetValue(FormatStringProperty);
        set => SetValue(FormatStringProperty, value);
    }

    public string? AccessibleName
    {
        get => GetValue(AccessibleNameProperty);
        set => SetValue(AccessibleNameProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        if (_textBox is not null)
        {
            _textBox.LostFocus -= OnTextBoxLostFocus;
            _textBox.KeyDown -= OnTextBoxKeyDown;
            _textBox.PointerWheelChanged -= OnTextBoxPointerWheelChanged;
        }

        if (_scrubHandle is not null)
        {
            _scrubHandle.PointerPressed -= OnScrubPointerPressed;
            _scrubHandle.PointerMoved -= OnScrubPointerMoved;
            _scrubHandle.PointerReleased -= OnScrubPointerReleased;
            _scrubHandle.PointerCaptureLost -= OnScrubPointerCaptureLost;
        }

        _textBox = e.NameScope.Find<TextBox>("PART_TextBox");
        if (_textBox is not null)
        {
            _textBox.LostFocus += OnTextBoxLostFocus;
            _textBox.KeyDown += OnTextBoxKeyDown;
            _textBox.PointerWheelChanged += OnTextBoxPointerWheelChanged;
        }

        _scrubHandle = e.NameScope.Find<Control>("PART_ScrubHandle");
        if (_scrubHandle is not null)
        {
            _scrubHandle.PointerPressed += OnScrubPointerPressed;
            _scrubHandle.PointerMoved += OnScrubPointerMoved;
            _scrubHandle.PointerReleased += OnScrubPointerReleased;
            _scrubHandle.PointerCaptureLost += OnScrubPointerCaptureLost;
        }

        SyncTextFromValue();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty || change.Property == FormatStringProperty)
        {
            SyncTextFromValue();
        }
    }

    private void SyncTextFromValue()
    {
        if (_textBox is null || _isSyncingText)
        {
            return;
        }

        _isSyncingText = true;
        try
        {
            double clamped = ClampValue(Value);
            string formatted = clamped.ToString(FormatString, CultureInfo.InvariantCulture);
            if (!string.Equals(_textBox.Text, formatted, StringComparison.Ordinal))
            {
                _textBox.Text = formatted;
            }
        }
        finally
        {
            _isSyncingText = false;
        }
    }

    private void CommitText()
    {
        if (_textBox is null)
        {
            return;
        }

        if (double.TryParse(_textBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) &&
            double.IsFinite(parsed))
        {
            SetCurrentValue(ValueProperty, ClampValue(parsed));
        }

        SyncTextFromValue();
    }

    private void StepBy(double deltaSteps)
    {
        double step = Increment > 0 ? Increment : 1.0;
        double next = ClampValue(Value + (deltaSteps * step));
        SetCurrentValue(ValueProperty, Math.Round(next, 4));
        SyncTextFromValue();
    }

    private double ClampValue(double raw)
    {
        if (!double.IsFinite(raw))
        {
            return double.IsFinite(Minimum) ? Minimum : 0.0;
        }

        double min = Minimum;
        double max = Maximum;
        if (min > max)
        {
            (min, max) = (max, min);
        }

        return Math.Clamp(raw, min, max);
    }

    private void OnTextBoxLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => CommitText();

    private void OnTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        double multiplier = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10.0 : 1.0;
        if (e.Key == Key.Enter)
        {
            CommitText();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SyncTextFromValue();
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            StepBy(multiplier);
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            StepBy(-multiplier);
            e.Handled = true;
        }
    }

    private void OnTextBoxPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_textBox is null || !_textBox.IsFocused)
        {
            return;
        }

        double multiplier = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10.0 : 1.0;
        double dir = e.Delta.Y >= 0 ? 1.0 : -1.0;
        StepBy(dir * multiplier);
        e.Handled = true;
    }

    private void OnScrubPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_scrubHandle is null || !e.GetCurrentPoint(_scrubHandle).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isScrubbing = true;
        _scrubStartPoint = e.GetPosition(_scrubHandle);
        _scrubStartValue = Value;
        e.Pointer.Capture(_scrubHandle);
        e.Handled = true;
    }

    private void OnScrubPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isScrubbing || _scrubHandle is null)
        {
            return;
        }

        double dx = e.GetPosition(_scrubHandle).X - _scrubStartPoint.X;
        double step = Increment > 0 ? Increment : 1.0;
        double factor = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 5.0 : 1.0;
        double delta = Math.Round(dx / 4.0) * step * factor;
        SetCurrentValue(ValueProperty, Math.Round(ClampValue(_scrubStartValue + delta), 4));
        SyncTextFromValue();
        e.Handled = true;
    }

    private void OnScrubPointerReleased(object? sender, PointerReleasedEventArgs e) => EndScrub(e.Pointer);

    private void OnScrubPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => _isScrubbing = false;

    private void EndScrub(IPointer pointer)
    {
        if (_isScrubbing)
        {
            _isScrubbing = false;
            if (pointer.Captured == _scrubHandle)
            {
                pointer.Capture(null);
            }
        }
    }
}
