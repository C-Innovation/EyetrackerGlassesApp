using System.Windows.Input;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace EyetrackerGlasses;

/// <summary>
/// Переключатель-«таблетка» с анимированным ползунком.
/// Наследуется от ToggleButton, поэтому работают IsChecked, клавиатура (Space/Enter), фокус и т.д.
/// Масштабируется под размер (Width/Height), заданный у элемента.
/// </summary>
public class PillSwitch : ToggleButton
{
    // Геометрия «дизайнерского» размера (внутри Viewbox, масштабируется автоматически)
    private const double TrackInnerWidth = 104; // 110 - 2 * 3 (рамка)
    private const double KnobSize = 38;
    private const double KnobGap = 5;

    private static readonly IBrush DefaultOn = new SolidColorBrush(Color.Parse("#00B050"));
    private static readonly IBrush DefaultOff = new SolidColorBrush(Color.Parse("#FF0000"));

    /// <summary>Цвет ползунка во включённом состоянии (по умолчанию зелёный).</summary>
    public static readonly StyledProperty<IBrush?> OnColorProperty =
        AvaloniaProperty.Register<PillSwitch, IBrush?>(nameof(OnColor), DefaultOn);

    /// <summary>Цвет ползунка в выключенном состоянии (по умолчанию красный).</summary>
    public static readonly StyledProperty<IBrush?> OffColorProperty =
        AvaloniaProperty.Register<PillSwitch, IBrush?>(nameof(OffColor), DefaultOff);

    /// <summary>
    /// Команда, вызываемая при переключении пользователем.
    /// В качестве параметра передаётся новое состояние (bool).
    /// </summary>
    public static readonly StyledProperty<ICommand?> ToggledCommandProperty =
        AvaloniaProperty.Register<PillSwitch, ICommand?>(nameof(ToggledCommand));

    // Вычисляемые свойства для шаблона
    public static readonly DirectProperty<PillSwitch, IBrush?> KnobBrushProperty =
        AvaloniaProperty.RegisterDirect<PillSwitch, IBrush?>(nameof(KnobBrush), o => o.KnobBrush);

    public static readonly DirectProperty<PillSwitch, Thickness> KnobMarginProperty =
        AvaloniaProperty.RegisterDirect<PillSwitch, Thickness>(nameof(KnobMargin), o => o.KnobMargin);

    private IBrush? _knobBrush;
    private Thickness _knobMargin;

    public PillSwitch()
    {
        UpdateKnob();
    }

    public IBrush? OnColor
    {
        get => GetValue(OnColorProperty);
        set => SetValue(OnColorProperty, value);
    }

    public IBrush? OffColor
    {
        get => GetValue(OffColorProperty);
        set => SetValue(OffColorProperty, value);
    }

    public ICommand? ToggledCommand
    {
        get => GetValue(ToggledCommandProperty);
        set => SetValue(ToggledCommandProperty, value);
    }

    public IBrush? KnobBrush
    {
        get => _knobBrush;
        private set => SetAndRaise(KnobBrushProperty, ref _knobBrush, value);
    }

    public Thickness KnobMargin
    {
        get => _knobMargin;
        private set => SetAndRaise(KnobMarginProperty, ref _knobMargin, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsCheckedProperty ||
            change.Property == OnColorProperty ||
            change.Property == OffColorProperty)
        {
            UpdateKnob();
        }
    }

    private void UpdateKnob()
    {
        var on = IsChecked == true;
        KnobBrush = on ? OnColor : OffColor;
        KnobMargin = new Thickness(on ? TrackInnerWidth - KnobSize - KnobGap : KnobGap, 0, 0, 0);
    }

    /// <summary>Вызывается при клике/нажатии клавиши. К этому моменту IsChecked уже переключён.</summary>
    protected override void OnClick()
    {
        base.OnClick();

        var state = IsChecked == true;
        var command = ToggledCommand;
        if (command?.CanExecute(state) == true)
            command.Execute(state);
    }
}
