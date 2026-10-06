using System.Globalization;
using System.Windows.Data;
using OneSLogExporter.Core.Models;

namespace OneSLogExporter.Gui;

/// <summary>
/// Показывает в ячейке таблицы многострочное или очень длинное значение одной строкой.
/// Без него строка таблицы растягивается на высоту всего текста поля.
/// </summary>
public sealed class SingleLineTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        SingleLineText.From(value as string ?? value?.ToString());

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
