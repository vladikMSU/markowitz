using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using Markowitz.Core.Models;

namespace Markowitz.Core.Services;

public class CsvParsingService
{
    private static readonly string[] DateFormats = new[]
    {
        "yyyy-MM-dd",
        "yyyy-MM-dd HH:mm:ss",
        "MM/dd/yyyy",
        "MM/dd/yyyy HH:mm:ss",
        "dd.MM.yyyy",
        "dd.MM.yyyy HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:ssZ"
    };

    public List<PriceBar> Parse(Stream csvStream)
    {
        var cfg = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            PrepareHeaderForMatch = args => args.Header.Trim().ToLowerInvariant(),
            MissingFieldFound = null,
            BadDataFound = null,
            DetectDelimiter = true
        };

        using var reader = new StreamReader(csvStream);
        using var csv = new CsvReader(reader, cfg);

        // >>> ВАЖНО: сначала Read() и ReadHeader() <<<
        if (!csv.Read())
            return new List<PriceBar>();      // пустой файл/поток

        csv.ReadHeader();                      // теперь можно GetField("date") и т.д.

        var rows = new List<(DateTime dt, decimal open, decimal high, decimal low, decimal close)>();

        while (csv.Read())
        {
            var dateStr = csv.GetField("date");
            if (string.IsNullOrWhiteSpace(dateStr)) continue;

            if (!TryParseDate(dateStr, out var dt))
                throw new FormatException($"Cannot parse Date: '{dateStr}'");

            decimal close = csv.GetField<decimal>("close");
            decimal high  = csv.GetField<decimal>("high");
            decimal low   = csv.GetField<decimal>("low");
            decimal open  = csv.GetField<decimal>("open");
            rows.Add((dt.Date, open, high, low, close));
        }

        // дедуп по датам + сортировка
        var list = rows
            .GroupBy(r => r.dt)
            .Select(g => g.Last())
            .OrderBy(r => r.dt)
            .Select(r => new PriceBar(r.dt, r.open, r.high, r.low, r.close))
            .ToList();

        return list;
    }

    private static bool TryParseDate(string s, out DateTime dt)
    {
        // Пытаемся как UTC/локаль-инвариант
        if (DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out dt))
            return true;

        if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out dt))
            return true;

        return false;
    }
}
