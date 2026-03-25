using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace KevGitChanges
{
    public sealed class TreeViewIndentToMarginConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int depth)
            {
                var indent = depth * 20.0;
                return new Thickness(indent, 0, 0, 0);
            }
            return new Thickness(0);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}
