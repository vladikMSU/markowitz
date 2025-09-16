using CsvHelper;
using Markowitz.Core.Models;
using Markowitz.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Globalization;
using System.Linq;

namespace Markowitz.Web.Pages;

public class IndexModel : PageModel
{
    private readonly MarkowitzOptimizer _optimizer;

    public IndexModel(MarkowitzOptimizer optimizer) => _optimizer = optimizer;

    [BindProperty] public List<IFormFile> Files { get; set; } = new();

    [BindProperty] public int? LookbackDays { get; set; } = 252;
    [BindProperty] public DateTime? Start { get; set; }
    [BindProperty] public DateTime? End { get; set; }
    [BindProperty] public double? TargetReturnAnnual { get; set; }

    [BindProperty] public OptimizationMethod Method { get; set; } = OptimizationMethod.ClosedForm;
    [BindProperty] public bool AllowShort { get; set; }
    [BindProperty] public double? GlobalMin { get; set; }
    [BindProperty] public double? GlobalMax { get; set; }
    [BindProperty] public double RiskFreeAnnual { get; set; }
    [BindProperty] public double? CvarAlpha { get; set; } = 0.95;
    [BindProperty] public IFormFile? ScenarioFile { get; set; }
    [BindProperty] public List<AssetConstraintInput> AssetBounds { get; set; } = new();

    public OptimizationResult? Result { get; set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        if (Files.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "Upload at least one CSV.");
            return Page();
        }

        var dict = new Dictionary<string, List<PriceBar>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files)
        {
            if (file.Length == 0)
            {
                continue;
            }

            var ticker = Path.GetFileNameWithoutExtension(file.FileName).Trim();
            if (string.IsNullOrWhiteSpace(ticker))
            {
                ModelState.AddModelError(string.Empty, "Ticker name could not be derived from file name.");
                return Page();
            }

            using var stream = file.OpenReadStream();
            using var reader = new StreamReader(stream);
            using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);

            if (!await csv.ReadAsync())
            {
                continue;
            }
            csv.ReadHeader();

            var header = csv.HeaderRecord ?? Array.Empty<string>();
            var columnLookup = header
                .Select((name, index) => new { name, index })
                .Where(x => !string.IsNullOrWhiteSpace(x.name))
                .ToDictionary(x => x.name.Trim(), x => x.index, StringComparer.OrdinalIgnoreCase);

            int GetColumnIndex(string column)
            {
                if (!columnLookup.TryGetValue(column, out var index))
                    throw new InvalidOperationException($"File '{file.FileName}' is missing required column '{column}'.");
                return index;
            }

            int dateIndex = GetColumnIndex("Date");
            int openIndex = GetColumnIndex("Open");
            int highIndex = GetColumnIndex("High");
            int lowIndex = GetColumnIndex("Low");
            int closeIndex = GetColumnIndex("Close");

            var prices = new List<PriceBar>();
            while (await csv.ReadAsync())
            {
                try
                {
                    var ts = csv.GetField<DateTime>(dateIndex);
                    var open = csv.GetField<decimal>(openIndex);
                    var high = csv.GetField<decimal>(highIndex);
                    var low = csv.GetField<decimal>(lowIndex);
                    var close = csv.GetField<decimal>(closeIndex);
                    prices.Add(new PriceBar(ts, open, high, low, close));
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Failed to parse record in file '{file.FileName}': {ex.Message}", ex);
                }
            }

            if (prices.Count == 0)
            {
                ModelState.AddModelError(string.Empty, $"File '{file.FileName}' does not contain price rows.");
                return Page();
            }

            dict[ticker] = prices;
        }

        if (dict.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "No valid CSV data found.");
            return Page();
        }

        var orderedTickers = dict.Keys.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToArray();
        SyncAssetBounds(orderedTickers);

        List<double[]>? scenarioData = null;
        if (ScenarioFile is { Length: > 0 })
        {
            try
            {
                scenarioData = await ParseScenarioFileAsync(ScenarioFile, orderedTickers);
            }
            catch (Exception ex)
            {
                ModelState.AddModelError(string.Empty, $"Failed to parse scenario file: {ex.Message}");
                return Page();
            }
        }

        var lowerDict = AssetBounds
            .Where(a => a.Lower.HasValue)
            .ToDictionary(a => a.Ticker, a => a.Lower!.Value, StringComparer.OrdinalIgnoreCase);
        var upperDict = AssetBounds
            .Where(a => a.Upper.HasValue)
            .ToDictionary(a => a.Ticker, a => a.Upper!.Value, StringComparer.OrdinalIgnoreCase);

        var req = new OptimizationRequest
        {
            PricesByTicker = dict,
            LookbackDays = LookbackDays,
            Start = Start,
            End = End,
            TargetReturnAnnual = TargetReturnAnnual,
            Method = Method,
            AllowShort = AllowShort,
            GlobalMinWeight = GlobalMin,
            GlobalMaxWeight = GlobalMax,
            RiskFreeAnnual = RiskFreeAnnual,
            CvarAlpha = CvarAlpha,
            LowerBounds = lowerDict.Count > 0 ? lowerDict : null,
            UpperBounds = upperDict.Count > 0 ? upperDict : null,
            ScenarioReturns = scenarioData
        };

        try
        {
            Result = _optimizer.Optimize(req);
        }
        catch (Exception ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
        }

        return Page();
    }

    private void SyncAssetBounds(string[] tickers)
    {
        var existing = AssetBounds.ToDictionary(a => a.Ticker ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        AssetBounds = tickers
            .Select(t => existing.TryGetValue(t, out var entry)
                ? new AssetConstraintInput { Ticker = t, Lower = entry.Lower, Upper = entry.Upper }
                : new AssetConstraintInput { Ticker = t })
            .ToList();
    }

    private static async Task<List<double[]>> ParseScenarioFileAsync(IFormFile file, string[] tickers)
    {
        using var stream = file.OpenReadStream();
        using var reader = new StreamReader(stream);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);

        if (!await csv.ReadAsync())
            throw new InvalidOperationException("Scenario file is missing a header row.");
        csv.ReadHeader();

        var header = csv.HeaderRecord ?? Array.Empty<string>();
        var lookup = header
            .Select((name, index) => new { name, index })
            .Where(x => !string.IsNullOrWhiteSpace(x.name))
            .ToDictionary(x => x.name.Trim(), x => x.index, StringComparer.OrdinalIgnoreCase);

        int GetIndex(string column)
        {
            if (!lookup.TryGetValue(column, out var index))
                throw new InvalidOperationException($"Scenario file is missing column '{column}'.");
            return index;
        }

        var indices = tickers.Select(GetIndex).ToArray();
        var rows = new List<double[]>();
        while (await csv.ReadAsync())
        {
            var row = new double[tickers.Length];
            for (int j = 0; j < tickers.Length; j++)
                row[j] = csv.GetField<double>(indices[j]);
            rows.Add(row);
        }

        if (rows.Count == 0)
            throw new InvalidOperationException("Scenario file does not contain any scenarios.");

        return rows;
    }

    public class AssetConstraintInput
    {
        public string Ticker { get; set; } = string.Empty;
        public double? Lower { get; set; }
        public double? Upper { get; set; }
    }
}

