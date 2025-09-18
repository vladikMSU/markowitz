using CsvHelper;
using Markowitz.Core.Models;
using Markowitz.Core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Globalization;
using System.Text.Json;
using System.Linq;

namespace Markowitz.Web.Pages;

public class IndexModel : PageModel
{
    private readonly MarkowitzOptimizer _optimizer;
    private const string UploadSessionKey = "uploaded-files";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public IndexModel(MarkowitzOptimizer optimizer) => _optimizer = optimizer;

    [BindProperty] public List<IFormFile> Files { get; set; } = new();

    [BindProperty] public DateTime? Start { get; set; }
    [BindProperty] public DateTime? End { get; set; }
    [BindProperty] public double? TargetReturnAnnualPercent { get; set; }

    [BindProperty] public OptimizationMethod Method { get; set; } = OptimizationMethod.QuadraticProgramming;
    [BindProperty] public bool AllowShort { get; set; }
    [BindProperty] public double? GlobalMin { get; set; }
    [BindProperty] public double? GlobalMax { get; set; }
    [BindProperty] public double RiskFreeAnnual { get; set; }
    [BindProperty] public double? CvarAlpha { get; set; } = 0.95;
    [BindProperty] public IFormFile? ScenarioFile { get; set; }
    [BindProperty] public List<AssetConstraintInput> AssetBounds { get; set; } = new();

    public OptimizationResult? Result { get; set; }
    public List<string> UploadedFileNames { get; private set; } = new();

    public void OnGet()
    {
        var uploads = LoadStoredUploads();
        UploadedFileNames = uploads.Select(u => u.FileName).ToList();
        if (uploads.Count > 0)
        {
            var tickers = uploads.Select(u => u.Ticker)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            SyncAssetBounds(tickers);
        }
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var uploads = LoadStoredUploads();
        UploadedFileNames = uploads.Select(u => u.FileName).ToList();

        var action = Request.Form["action"].FirstOrDefault();
        var isUploadOnly = string.Equals(action, "upload", StringComparison.OrdinalIgnoreCase);
        var removeTarget = Request.Form["removeFile"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(removeTarget))
        {
            if (uploads.RemoveAll(u => string.Equals(u.FileName, removeTarget, StringComparison.OrdinalIgnoreCase)) > 0)
                SaveStoredUploads(uploads);

            UploadedFileNames = uploads.Select(u => u.FileName).ToList();
            var remainingTickers = uploads.Select(u => u.Ticker)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            SyncAssetBounds(remainingTickers);
            Result = null;
            return Page();
        }

        if (Files.Count > 0)
        {
            foreach (var file in Files)
            {
                var (upload, error) = await ParseUploadedFileAsync(file);
                if (error is not null)
                {
                    ModelState.AddModelError(string.Empty, error);
                    UploadedFileNames = uploads.Select(u => u.FileName).ToList();
                    var tickers = uploads.Select(u => u.Ticker)
                        .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    SyncAssetBounds(tickers);
                    return Page();
                }
                if (upload is null)
                    continue;

                uploads.RemoveAll(u => string.Equals(u.FileName, upload.FileName, StringComparison.OrdinalIgnoreCase));
                uploads.RemoveAll(u => string.Equals(u.Ticker, upload.Ticker, StringComparison.OrdinalIgnoreCase));
                uploads.Add(upload);
            }
            SaveStoredUploads(uploads);
        }

        UploadedFileNames = uploads.Select(u => u.FileName).ToList();

        var dict = new Dictionary<string, List<PriceBar>>(StringComparer.OrdinalIgnoreCase);
        foreach (var upload in uploads)
            dict[upload.Ticker] = upload.Bars;

        if (dict.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "Upload at least one CSV.");
            SyncAssetBounds(Array.Empty<string>());
            Result = null;
            return Page();
        }

        var orderedTickers = dict.Keys
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        SyncAssetBounds(orderedTickers);

        if (isUploadOnly)
        {
            Result = null;
            return Page();
        }

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

        if (Method == OptimizationMethod.ClosedForm && !AllowShort)
            AllowShort = true;

        var req = new OptimizationRequest
        {
            PricesByTicker = dict,
            Start = Start,
            End = End,
            TargetReturnAnnual = TargetReturnAnnualPercent / 100,
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

    private async Task<(StoredUpload? Upload, string? Error)> ParseUploadedFileAsync(IFormFile file)
    {
        if (file is null || file.Length == 0)
            return (null, $"File '{file?.FileName ?? "unknown"}' does not contain price rows.");

        var ticker = Path.GetFileNameWithoutExtension(file.FileName).Trim();
        if (string.IsNullOrWhiteSpace(ticker))
            return (null, "Ticker name could not be derived from file name.");

        using var stream = file.OpenReadStream();
        using var reader = new StreamReader(stream);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);

        if (!await csv.ReadAsync())
            return (null, $"File '{file.FileName}' does not contain price rows.");
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

        try
        {
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
                    return (null, $"Failed to parse record in file '{file.FileName}': {ex.Message}");
                }
            }

            if (prices.Count == 0)
                return (null, $"File '{file.FileName}' does not contain price rows.");

            var upload = new StoredUpload
            {
                FileName = file.FileName,
                Ticker = ticker,
                Bars = prices
            };
            return (upload, null);
        }
        catch (InvalidOperationException ex)
        {
            return (null, ex.Message);
        }
    }

    private List<StoredUpload> LoadStoredUploads()
    {
        var json = HttpContext.Session.GetString(UploadSessionKey);
        if (string.IsNullOrEmpty(json))
            return new List<StoredUpload>();

        try
        {
            return JsonSerializer.Deserialize<List<StoredUpload>>(json, JsonOptions) ?? new List<StoredUpload>();
        }
        catch
        {
            HttpContext.Session.Remove(UploadSessionKey);
            return new List<StoredUpload>();
        }
    }

    private void SaveStoredUploads(List<StoredUpload> uploads)
    {
        if (uploads.Count == 0)
        {
            HttpContext.Session.Remove(UploadSessionKey);
            return;
        }

        var json = JsonSerializer.Serialize(uploads, JsonOptions);
        HttpContext.Session.SetString(UploadSessionKey, json);
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

    private sealed class StoredUpload
    {
        public string FileName { get; set; } = string.Empty;
        public string Ticker { get; set; } = string.Empty;
        public List<PriceBar> Bars { get; set; } = new();
    }

    public class AssetConstraintInput
    {
        public string Ticker { get; set; } = string.Empty;
        public double? Lower { get; set; }
        public double? Upper { get; set; }
    }
}

