namespace FaraPokemonAssistance.Core.Csv;

/// <summary>
/// 最小限の CSV パーサー。ヘッダー行で列名を解決し、ダブルクォートで囲まれたフィールド（改行・カンマ含む）に対応する。
/// </summary>
public sealed class CsvTable
{
    private readonly Dictionary<string, int> _columns;

    public IReadOnlyList<string[]> Rows { get; }

    private CsvTable(Dictionary<string, int> columns, List<string[]> rows)
    {
        _columns = columns;
        Rows = rows;
    }

    public static CsvTable Parse(string text)
    {
        var records = ParseRecords(text);
        if (records.Count == 0)
            return new CsvTable(new Dictionary<string, int>(), new List<string[]>());

        var header = records[0];
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Length; i++)
            columns[header[i].Trim().TrimStart('﻿')] = i;

        var rows = records.Skip(1).Where(r => r.Length > 1 || (r.Length == 1 && r[0].Length > 0)).ToList();
        return new CsvTable(columns, rows);
    }

    public bool HasColumn(string name) => _columns.ContainsKey(name);

    public string Get(string[] row, string column, string fallback = "")
    {
        if (!_columns.TryGetValue(column, out var index) || index >= row.Length)
            return fallback;
        return row[index];
    }

    public int GetInt(string[] row, string column, int fallback = 0)
        => int.TryParse(Get(row, column), out var value) ? value : fallback;

    public double GetDouble(string[] row, string column, double fallback = 0)
        => double.TryParse(Get(row, column), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static List<string[]> ParseRecords(string text)
    {
        var records = new List<string[]>();
        var fields = new List<string>();
        var field = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    fields.Add(field.ToString());
                    field.Clear();
                    records.Add(fields.ToArray());
                    fields.Clear();
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            records.Add(fields.ToArray());
        }

        return records;
    }
}
