using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using CampaignVault.Authoring.Models;

namespace CampaignVault.Authoring.ViewModels;

public class PathDisplayConverter : IValueConverter
{
    public static readonly PathDisplayConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string path && !string.IsNullOrWhiteSpace(path))
            return System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));

        return value?.ToString() ?? string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class SyncStateDisplayConverter : IValueConverter
{
    public static readonly SyncStateDisplayConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is SyncState state)
        {
            return state switch
            {
                SyncState.Synced => "Synced",
                SyncState.LocalOnly => "Local",
                SyncState.RemoteOnly => "Remote",
                SyncState.ModifiedLocally => "Modified",
                SyncState.ModifiedRemotely => "Remote Δ",
                SyncState.Conflict => "Conflict",
                _ => state.ToString()
            };
        }

        return value?.ToString() ?? string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

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
                "LocalOnly" => Brush.Parse("#16A34A"),        // Green
                "RemoteOnly" => Brush.Parse("#7C3AED"),       // Purple
                "Conflict" => Brush.Parse("#DC2626"),         // Red
                "Modified" => Brush.Parse("#D97706"),
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
