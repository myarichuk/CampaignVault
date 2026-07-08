using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using CampaignVault.Authoring.Vault.Sync;

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
        => Avalonia.AvaloniaProperty.UnsetValue;
}

public class VaultSyncStateDisplayConverter : IValueConverter
{
    public static readonly VaultSyncStateDisplayConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is VaultSyncState state)
        {
            return state switch
            {
                VaultSyncState.Synced => "Synced",
                VaultSyncState.LocalOnly => "Local",
                VaultSyncState.RemoteOnly => "Remote",
                VaultSyncState.AheadOfVault => "Ahead",
                VaultSyncState.BehindVault => "Behind",
                VaultSyncState.Conflict => "Conflict",
                VaultSyncState.DeletedLocally => "Del local",
                VaultSyncState.DeletedRemotely => "Del remote",
                VaultSyncState.Invalid => "Invalid",
                VaultSyncState.Absent => "Absent",
                _ => state.ToString()
            };
        }

        return value?.ToString() ?? string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.AvaloniaProperty.UnsetValue;
}

public class ExplorerBadgeConverter : IMultiValueConverter
{
    public static readonly ExplorerBadgeConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2)
            return string.Empty;

        var syncState = values[0] is VaultSyncState state ? state : VaultSyncState.Absent;
        var isGitDirty = values[1] is true;

        if (syncState == VaultSyncState.Invalid)
            return "Invalid";

        if (isGitDirty && syncState == VaultSyncState.Synced)
            return "Uncommitted";

        return VaultSyncStateDisplayConverter.Instance.Convert(syncState, typeof(string), null, culture);
    }
}

public class VaultSyncStateColorConverter : IValueConverter
{
    public static readonly VaultSyncStateColorConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is VaultSyncState state)
        {
            return state switch
            {
                VaultSyncState.AheadOfVault => Brush.Parse("#16A34A"),
                VaultSyncState.BehindVault => Brush.Parse("#2563EB"),
                VaultSyncState.LocalOnly => Brush.Parse("#16A34A"),
                VaultSyncState.RemoteOnly => Brush.Parse("#7C3AED"),
                VaultSyncState.Conflict => Brush.Parse("#DC2626"),
                VaultSyncState.DeletedLocally => Brush.Parse("#DC2626"),
                VaultSyncState.DeletedRemotely => Brush.Parse("#D97706"),
                VaultSyncState.Invalid => Brush.Parse("#DC2626"),
                _ => Brush.Parse("#4B5563")
            };
        }

        return Brush.Parse("#4B5563");
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.AvaloniaProperty.UnsetValue;
}

public class SyncButtonTextConverter : IValueConverter
{
    public static readonly SyncButtonTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "Disconnect Campaign Vault" : "Connect Campaign Vault";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.AvaloniaProperty.UnsetValue;
}

public class EnumToBoolConverter : IValueConverter
{
    public static readonly EnumToBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() == parameter?.ToString();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.AvaloniaProperty.UnsetValue;
}

public class MissingCampaignOpacityConverter : IValueConverter
{
    public static readonly MissingCampaignOpacityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? 1.0 : 0.5;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.AvaloniaProperty.UnsetValue;
}

public class CampaignTooltipConverter : IValueConverter
{
    public static readonly CampaignTooltipConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is false ? "Campaign folder not found" : string.Empty;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.AvaloniaProperty.UnsetValue;
}

public class NotEmptyConverter : IValueConverter
{
    public static readonly NotEmptyConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => !string.IsNullOrWhiteSpace(value?.ToString());

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.AvaloniaProperty.UnsetValue;
}

public class GreaterThanZeroConverter : IValueConverter
{
    public static readonly GreaterThanZeroConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int intValue)
            return intValue > 0;
        if (value is System.Collections.ICollection collection)
            return collection.Count > 0;
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.AvaloniaProperty.UnsetValue;
}