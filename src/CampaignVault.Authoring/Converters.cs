using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace CampaignVault.Authoring.ViewModels;

public class StatusColorConverter : IValueConverter
{
    public static readonly StatusColorConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string status)
        {
            return status switch
            {
                "ModifiedLocally" => Brush.Parse("#D97706"),  // Orange
                "ModifiedRemotely" => Brush.Parse("#2563EB"), // Blue
                "AddedLocally" => Brush.Parse("#16A34A"),     // Green
                "AddedRemotely" => Brush.Parse("#7C3AED"),    // Purple
                "Conflict" => Brush.Parse("#DC2626"),         // Red
                "Modified" => Brush.Parse("#D97706"),
                "Added Remote" => Brush.Parse("#2563EB"),
                "Added Local" => Brush.Parse("#16A34A"),
                "Deleted" => Brush.Parse("#DC2626"),
                _ => Brush.Parse("#4B5563")                   // Gray
            };
        }
        return Brush.Parse("#4B5563");
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class SyncButtonTextConverter : IValueConverter
{
    public static readonly SyncButtonTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool connected && connected)
        {
            return "Disconnect Remote Vault";
        }
        return "Connect Remote Vault";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class EnumToBoolConverter : IValueConverter
{
    public static readonly EnumToBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value?.ToString() == parameter?.ToString();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
