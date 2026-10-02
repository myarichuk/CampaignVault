using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CampaignVault.Data.Templates;

namespace CampaignVault.Rulesets.Creation;

/// <summary>
/// The text form of a <c>rows</c> stat block value, as the companion templates ship it and the notes show it: rows split
/// by ";", a row's columns by ",", in column order; a whole number column right after the name may share its part
/// ("Bite +3, 1d6+1 piercing, reach 5 ft."), and any parts left over go to the last column.
/// </summary>
public static partial class StatRowsText
{
    public static bool TryParse(string? text, IReadOnlyList<StatBlockColumn> columns, out JsonElement value)
    {
        value = default;
        if (columns.Count == 0)
            return false;

        var rows = new List<Dictionary<string, object>>();
        foreach (var part in (text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cells = part.Split(',', StringSplitOptions.TrimEntries).ToList();
            if (columns.Count > 1 && columns[1].Type == "int")
            {
                var cut = cells[0].LastIndexOf(' ');
                if (cut > 0 && int.TryParse(cells[0][(cut + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
                {
                    cells.Insert(1, cells[0][(cut + 1)..]);
                    cells[0] = cells[0][..cut].Trim();
                }
            }

            if (cells.Count > columns.Count)
                cells = [.. cells.Take(columns.Count - 1), string.Join(", ", cells.Skip(columns.Count - 1))];

            var row = new Dictionary<string, object>();
            for (var i = 0; i < cells.Count; i++)
            {
                if (cells[i].Length == 0)
                    continue;
                row[columns[i].Key] = columns[i].Type == "int"
                                      && int.TryParse(cells[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)
                    ? n
                    : cells[i];
            }

            rows.Add(row);
        }

        if (rows.Count == 0)
            return false;

        value = JsonSerializer.SerializeToElement(rows);
        return true;
    }

    /// <summary>The list form of a rows value: a list as it is, text read; anything else unchanged.</summary>
    public static JsonElement Normalize(JsonElement value, IReadOnlyList<StatBlockColumn>? columns) =>
        value.ValueKind == JsonValueKind.String && TryParse(value.GetString(), columns ?? [], out var parsed) ? parsed : value;

    /// <summary>The text form: what the notes show, and what <see cref="TryParse"/> reads back.</summary>
    public static string Format(JsonElement value, IReadOnlyList<StatBlockColumn>? columns)
    {
        value = Normalize(value, columns);
        if (value.ValueKind != JsonValueKind.Array || columns is not { Count: > 0 })
            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();

        var rows = new List<string>();
        foreach (var row in value.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Object))
        {
            var cells = columns.Select(c => Cell(row, c)).ToList();
            var head = cells[0];
            var rest = 1;
            if (columns.Count > 1 && columns[1].Type == "int" && cells[1].Length > 0)
            {
                head = (head + " " + cells[1]).Trim();
                rest = 2;
            }

            rows.Add(string.Join(", ", new[] { head }.Concat(cells.Skip(rest)).Where(c => c.Length > 0)));
        }

        return string.Join("; ", rows);
    }

    private static string Cell(JsonElement row, StatBlockColumn column)
    {
        if (!row.TryGetProperty(column.Key, out var v))
            return "";
        if (column.Type == "int" && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
            return n.ToString("+0;-0;+0", CultureInfo.InvariantCulture);
        return v.ValueKind switch
        {
            JsonValueKind.String => (v.GetString() ?? "").Trim(),
            JsonValueKind.Number => v.ToString(),
            _ => "",
        };
    }

    /// <summary>Damage: dice ("2d6+4") or a number, then any words ("piercing").</summary>
    public static bool IsDamage(string? text) => Damage().IsMatch(text ?? "");

    [GeneratedRegex(@"^\s*(\d+d\d+(\s*[+-]\s*\d+)?|\d+)(\s+\S.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Damage();
}
