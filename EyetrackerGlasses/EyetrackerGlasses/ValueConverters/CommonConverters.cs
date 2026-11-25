using Avalonia.Data;
using Avalonia.Media;
using System;
using System.Globalization;

namespace EyetrackerGlasses.ValueConverters
{
    /// <summary>
    /// Инвертирует логическое значение.
    /// Пример: <c>IsEnabled="{Binding IsBusy, Converter={local:InverseBooleanConverter}}"</c>.
    /// </summary>
    public class InverseBooleanConverter : BaseValueConverter<InverseBooleanConverter>
    {
        public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value is bool flag ? !flag : BindingOperations.DoNothing;
        }

        public override object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value is bool flag ? !flag : BindingOperations.DoNothing;
        }
    }

    /// <summary>
    /// <c>true</c>, если значение не <c>null</c>.
    /// Параметр <c>"invert"</c> меняет результат на противоположный.
    /// </summary>
    public class NullToBooleanConverter : BaseValueConverter<NullToBooleanConverter>
    {
        public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool hasValue = value != null;
            return IsInverted(parameter) ? !hasValue : hasValue;
        }

        public override object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return BindingOperations.DoNothing;
        }

        internal static bool IsInverted(object? parameter)
        {
            return parameter is string text
                && string.Equals(text, "invert", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// <c>true</c>, если строка непустая.
    /// Параметр <c>"invert"</c> меняет результат на противоположный.
    /// Удобно для скрытия подписей и подсказок.
    /// </summary>
    public class StringNotEmptyToBooleanConverter : BaseValueConverter<StringNotEmptyToBooleanConverter>
    {
        public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool hasText = !string.IsNullOrWhiteSpace(value as string);
            return NullToBooleanConverter.IsInverted(parameter) ? !hasText : hasText;
        }

        public override object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return BindingOperations.DoNothing;
        }
    }

    /// <summary>
    /// Сравнивает значение перечисления с именем, переданным в параметре.
    /// <para>
    /// Нужен для переключателей: <c>IsChecked="{Binding Theme,
    /// Converter={local:EnumToBooleanConverter}, ConverterParameter=Dark}"</c>.
    /// Обратное преобразование возвращает значение перечисления, поэтому
    /// переключатель пригоден для записи.
    /// </para>
    /// </summary>
    public class EnumToBooleanConverter : BaseValueConverter<EnumToBooleanConverter>
    {
        public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value == null || parameter is not string expected)
                return false;

            return string.Equals(value.ToString(), expected, StringComparison.OrdinalIgnoreCase);
        }

        public override object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            // Снятие отметки не несёт информации о новом значении:
            // источник меняет только тот переключатель, который включили.
            if (value is not bool isChecked || !isChecked || parameter is not string expected)
                return BindingOperations.DoNothing;

            Type enumType = targetType;
            if (Nullable.GetUnderlyingType(enumType) is Type underlying)
                enumType = underlying;

            if (!enumType.IsEnum)
                return BindingOperations.DoNothing;

            return Enum.TryParse(enumType, expected, ignoreCase: true, out object? result)
                ? result
                : BindingOperations.DoNothing;
        }
    }

    /// <summary>
    /// Выбирает одну из двух иконок Font Awesome по логическому значению.
    /// <para>
    /// Имена иконок задаются параметром через точку с запятой:
    /// <c>ConverterParameter="FA_SquareCheckIcon;FA_SquareIcon"</c>.
    /// Если параметр не задан, берётся пара «отмеченный/пустой квадрат».
    /// </para>
    /// </summary>
    public class BooleanToFaIconConverter : BaseValueConverter<BooleanToFaIconConverter>
    {
        private const string DEFAULT_TRUE_KEY = "FA_SquareCheckIcon";
        private const string DEFAULT_FALSE_KEY = "FA_SquareIcon";

        public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            string trueKey = DEFAULT_TRUE_KEY;
            string falseKey = DEFAULT_FALSE_KEY;

            if (parameter is string spec)
            {
                string[] parts = spec.Split(';', StringSplitOptions.TrimEntries);
                if (parts.Length > 0 && parts[0].Length > 0)
                    trueKey = parts[0];
                if (parts.Length > 1 && parts[1].Length > 0)
                    falseKey = parts[1];
            }

            bool flag = value is bool b && b;
            string key = flag ? trueKey : falseKey;

            // Иконка хранится как строка в Styles/Fonts.axaml.
            return AppResources.GetString(key, string.Empty);
        }

        public override object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return BindingOperations.DoNothing;
        }
    }

    public class BooleanToFaIconPlayStopConverter : BaseValueConverter<BooleanToFaIconPlayStopConverter>
    {
        private PlayStopConverterData DEFAULT_TRUE_KEYS = new PlayStopConverterData { ContentFA = "FA_PlayIcon", ContentForeground = Brushes.Green, ToolTipText = "test_run" };
        private PlayStopConverterData DEFAULT_FALSE_KEYS = new PlayStopConverterData { ContentFA = "FA_StopIcon", ContentForeground = Brushes.Red, ToolTipText = "test_stop" };

        public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            //string trueKey = DEFAULT_TRUE_KEY;
            //string falseKey = DEFAULT_FALSE_KEY;
            bool flag = value is bool b && b;
            string key;
            IBrush? brush;
            if (parameter is PlayStopConverterParameterType type)
            {
                //string[] parts = spec.Split(';', StringSplitOptions.TrimEntries);
                //if (parts.Length > 0 && parts[0].Length > 0)
                //    trueKey = parts[0];
                //if (parts.Length > 1 && parts[1].Length > 0)
                //    falseKey = parts[1];
                
                switch (type)
                {
                    case PlayStopConverterParameterType.Content:

                        key = flag ? DEFAULT_TRUE_KEYS.ContentFA : DEFAULT_FALSE_KEYS.ContentFA;
                        return AppResources.GetString(key, string.Empty);

                    case PlayStopConverterParameterType.Foreground:
                        brush = flag ? DEFAULT_TRUE_KEYS.ContentForeground : DEFAULT_FALSE_KEYS.ContentForeground;
                        return brush;

                    case PlayStopConverterParameterType.ToolTip:
                        key = flag ? DEFAULT_TRUE_KEYS.ToolTipText : DEFAULT_FALSE_KEYS.ToolTipText;
                        return key;

                }
            }

            // Иконка хранится как строка в Styles/Fonts.axaml.
            return BindingOperations.DoNothing;
        }

        public override object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return BindingOperations.DoNothing;
        }
    }
}
