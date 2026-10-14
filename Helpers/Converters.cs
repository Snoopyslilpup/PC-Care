using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace WhateverITPCCare.Helpers;

/// <summary>Hex colour string (e.g. "#FF34C759") -> SolidColorBrush.</summary>
public class StringToBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value?.ToString() ?? "#FF8CA0B8")); }
        catch { return Brushes.Gray; }
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>null / empty -> Collapsed, otherwise Visible.</summary>
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool has = value is string s ? !string.IsNullOrWhiteSpace(s) : value != null;
        bool invert = string.Equals(p?.ToString(), "invert", StringComparison.OrdinalIgnoreCase);
        if (invert) has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>bool -> Visibility (true=Visible). Pass "invert" to flip.</summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool b = value is bool v && v;
        if (string.Equals(p?.ToString(), "invert", StringComparison.OrdinalIgnoreCase)) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}
