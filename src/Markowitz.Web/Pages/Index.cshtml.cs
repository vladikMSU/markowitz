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
    private const string VisualizationSessionKey = "visualization-data";

    private const double SecondsPerYear = 365.25 * 24 * 3600;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public IndexModel(MarkowitzOptimizer optimizer) => _optimizer = optimizer;

    [BindProperty] public List<IFormFile> Files { get; set; } = new();

    [BindProperty] public DateTime? Start { get; set; }
    [BindProperty] public DateTime? End { get; set; }
    [BindProperty] public double? CoreTargetReturnAnnualPercent { get; set; }
    [BindProperty] public double? ProTargetReturnAnnualPercent { get; set; }
    [BindProperty] public OptimizationTarget Target { get; set; } = OptimizationTarget.MinVolatility;
    [BindProperty] public OptimizationMethod Method { get; set; } = OptimizationMethod.QuadraticProgramming;
    [BindProperty] public bool AllowShort { get; set; }
    [BindProperty] public double? GlobalMin { get; set; }
    [BindProperty] public double? GlobalMax { get; set; }
    [BindProperty] public double RiskFreeAnnual { get; set; }
    [BindProperty] public double? CvarAlpha { get; set; } = 0.95;
    [BindProperty] public IFormFile? ScenarioFile { get; set; }
    [BindProperty] public List<AssetConstraintInput> AssetBounds { get; set; } = new();
    [BindProperty] public string ActiveTab { get; set; } = "basic";
    [BindProperty] public bool TargetCardExpanded { get; set; }
    [BindProperty] public bool PerAssetExpanded { get; set; }
    [BindProperty] public bool UseIntersectionStatistics { get; set; }

    public OptimizationResult? Result { get; set; }
    public PortfolioVisualization? Visualization { get; private set; }
    public List<string> UploadedFileNames { get; private set; } = new();

    public List<UploadedFileSummary> UploadedFiles { get; private set; } = new();

    public void OnGet()
    {
        ActiveTab = NormalizeActiveTab(ActiveTab);
        var uploads = LoadStoredUploads();
        RefreshUploadedFiles(uploads, UseIntersectionStatistics);
        if (uploads.Count > 0)
        {
            var tickers = uploads.Select(u => u.Ticker)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            SyncAssetBounds(tickers);
        }

        Visualization = uploads.Count > 0 ? LoadStoredVisualization() : null;
        if (uploads.Count == 0)
            SaveStoredVisualization(null);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        ActiveTab = NormalizeActiveTab(ActiveTab);
        var uploads = LoadStoredUploads();
        Visualization = LoadStoredVisualization();

        var action = Request.Form["action"].FirstOrDefault();
        var removeTarget = Request.Form["removeFile"].FirstOrDefault();

        if (string.Equals(action, "toggle-intersection", StringComparison.OrdinalIgnoreCase))
        {
            ModelState.Remove(nameof(UseIntersectionStatistics));
            UseIntersectionStatistics = !UseIntersectionStatistics;
            RefreshUploadedFiles(uploads, UseIntersectionStatistics);
            Result = null;
            return Page();
        }
        if (!string.IsNullOrWhiteSpace(removeTarget))
        {
            if (uploads.RemoveAll(u => string.Equals(u.FileName, removeTarget, StringComparison.OrdinalIgnoreCase)) > 0)
                SaveStoredUploads(uploads);

            RefreshUploadedFiles(uploads, UseIntersectionStatistics);
            var remainingTickers = uploads.Select(u => u.Ticker)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            SyncAssetBounds(remainingTickers);
            Result = null;
        }

        if (Files.Count > 0)
        {
            foreach (var file in Files)
            {
                var (upload, error) = await ParseUploadedFileAsync(file);
                if (error is not null)
                {
                    ModelState.AddModelError(string.Empty, error);
                    RefreshUploadedFiles(uploads, UseIntersectionStatistics);
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

        RefreshUploadedFiles(uploads, UseIntersectionStatistics);

        var dict = new Dictionary<string, List<PriceBar>>(StringComparer.OrdinalIgnoreCase);
        foreach (var upload in uploads)
            dict[upload.Ticker] = upload.Bars;

        if (dict.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "Upload at least one CSV.");
            SyncAssetBounds(Array.Empty<string>());
            Result = null;
            Visualization = null;
            SaveStoredVisualization(null);
            return Page();
        }

        var orderedTickers = dict.Keys
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        SyncAssetBounds(orderedTickers);

        var activeTabIsPro = string.Equals(ActiveTab, "pro", StringComparison.OrdinalIgnoreCase);
        var requestMethod = activeTabIsPro ? Method : OptimizationMethod.QuadraticProgramming;

        List<double[]>? scenarioData = null;
        if (activeTabIsPro && ScenarioFile is { Length: > 0 })
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

        if (Method != OptimizationMethod.QuadraticProgramming)
        {
            Target = OptimizationTarget.MinVolatility;
        }

        Dictionary<string, double> lowerDict;
        Dictionary<string, double> upperDict;

        if (activeTabIsPro)
        {
            lowerDict = AssetBounds
                .Where(a => a.Lower.HasValue)
                .ToDictionary(a => a.Ticker, a => a.Lower!.Value, StringComparer.OrdinalIgnoreCase);
            upperDict = AssetBounds
                .Where(a => a.Upper.HasValue)
                .ToDictionary(a => a.Ticker, a => a.Upper!.Value, StringComparer.OrdinalIgnoreCase);

            if (GlobalMin is double gMin && GlobalMax is double gMax && gMin > gMax)
            {
                ModelState.AddModelError(string.Empty, "Global min weight cannot exceed global max weight.");
            }

            foreach (var bound in AssetBounds)
            {
                if (!string.IsNullOrWhiteSpace(bound.Ticker) &&
                    bound.Lower.HasValue && bound.Upper.HasValue &&
                    bound.Lower.Value > bound.Upper.Value)
                {
                    ModelState.AddModelError(string.Empty, $"Lower bound for '{bound.Ticker}' cannot exceed its upper bound.");
                }
            }
        }
        else
        {
            lowerDict = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            upperDict = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        }

        var targetReturnPercent = activeTabIsPro ? ProTargetReturnAnnualPercent : CoreTargetReturnAnnualPercent;
        var requestTarget = activeTabIsPro
            ? Target
            : (targetReturnPercent.HasValue ? OptimizationTarget.TargetReturn : OptimizationTarget.MinVolatility);

        if (activeTabIsPro && requestMethod == OptimizationMethod.QuadraticProgramming &&
            requestTarget == OptimizationTarget.TargetReturn && ProTargetReturnAnnualPercent is null)
        {
            ModelState.AddModelError(string.Empty, "Provide a target return percentage for the selected optimization target.");
        }

        if (!ModelState.IsValid)
        {
            Result = null;
            Visualization = LoadStoredVisualization();
            return Page();
        }

        var req = new OptimizationRequest
        {
            PricesByTicker = dict,
            Start = Start,
            End = End,
            TargetReturnAnnual = targetReturnPercent.HasValue ? targetReturnPercent.Value / 100 : null,
            RiskFreeAnnual = activeTabIsPro ? RiskFreeAnnual : 0.0,
            Method = requestMethod,
            Target = requestTarget,
            AllowShort = AllowShort,
            GlobalMinWeight = activeTabIsPro ? GlobalMin : null,
            GlobalMaxWeight = activeTabIsPro ? GlobalMax : null,
            CvarAlpha = activeTabIsPro && requestMethod == OptimizationMethod.CvarLinearProgramming ? CvarAlpha : null,
            LowerBounds = activeTabIsPro && lowerDict.Count > 0 ? lowerDict : null,
            UpperBounds = activeTabIsPro && upperDict.Count > 0 ? upperDict : null,
            ScenarioReturns = activeTabIsPro ? scenarioData : null
        };

        var visualizationRequest = new OptimizationRequest
        {
            PricesByTicker = dict,
            Start = Start,
            End = End,
            TargetReturnAnnual = null,
            RiskFreeAnnual = activeTabIsPro ? RiskFreeAnnual : 0.0,
            Method = OptimizationMethod.QuadraticProgramming,
            Target = OptimizationTarget.MaxReturn,
            AllowShort = AllowShort,
            GlobalMinWeight = activeTabIsPro ? GlobalMin : null,
            GlobalMaxWeight = activeTabIsPro ? GlobalMax : null,
            LowerBounds = activeTabIsPro && lowerDict.Count > 0 ? lowerDict : null,
            UpperBounds = activeTabIsPro && upperDict.Count > 0 ? upperDict : null,
            ScenarioReturns = activeTabIsPro ? scenarioData : null,
            CvarAlpha = activeTabIsPro && requestMethod == OptimizationMethod.CvarLinearProgramming ? CvarAlpha : null,
            LookbackDays = req.LookbackDays,
            PeriodsPerYearOverride = req.PeriodsPerYearOverride
        };

        if (!string.Equals(action, "optimize", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Visualization = _optimizer.GenerateVisualization(visualizationRequest);
            }
            catch
            {
                Visualization = null;
            }

            SaveStoredVisualization(Visualization);
        }

        if (string.Equals(action, "optimize", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Result = _optimizer.Optimize(req);
            }
            catch (Exception ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
            }
        }
        else
        {
            Result = null;
        }

        return Page();
    }



    private void RefreshUploadedFiles(List<StoredUpload> uploads, bool useIntersectionStatistics)
    {
        UploadedFileNames = uploads.Select(u => u.FileName).ToList();

        IReadOnlyList<DateTime>? alignedTimeline = null;
        if (useIntersectionStatistics)
            alignedTimeline = BuildAlignedTimeline(uploads);

        UploadedFiles = uploads
            .Select(upload => BuildUploadedFileSummary(upload, alignedTimeline))
            .ToList();
    }

    private List<DateTime> BuildAlignedTimeline(List<StoredUpload> uploads)
    {
        if (uploads.Count == 0)
            return new List<DateTime>();

        HashSet<DateTime>? intersection = null;
        foreach (var upload in uploads)
        {
            if (upload.Bars is null || upload.Bars.Count == 0)
                return new List<DateTime>();

            var tickerDates = new HashSet<DateTime>(upload.Bars.Select(b => b.Timestamp));
            if (intersection is null)
                intersection = tickerDates;
            else
                intersection.IntersectWith(tickerDates);

            if (intersection.Count == 0)
                return new List<DateTime>();
        }

        if (intersection is null || intersection.Count == 0)
            return new List<DateTime>();

        var ordered = intersection
            .OrderBy(d => d)
            .ToList();

        if (Start.HasValue)
            ordered = ordered.Where(d => d >= Start.Value).ToList();
        if (End.HasValue)
            ordered = ordered.Where(d => d <= End.Value).ToList();

        return ordered;
    }

    private static UploadedFileSummary BuildUploadedFileSummary(StoredUpload upload, IReadOnlyList<DateTime>? alignedTimeline)
    {
        var summary = new UploadedFileSummary
        {
            FileName = upload.FileName
        };

        if (upload.Bars is null || upload.Bars.Count == 0)
            return summary;

        var sorted = new SortedDictionary<DateTime, double>();
        foreach (var bar in upload.Bars.OrderBy(b => b.Timestamp))
            sorted[bar.Timestamp] = (double)bar.Close;

        if (sorted.Count == 0)
            return summary;

        IReadOnlyList<DateTime> timeline;
        if (alignedTimeline is not null)
        {
            if (alignedTimeline.Count == 0)
                return summary;

            foreach (var date in alignedTimeline)
            {
                if (!sorted.ContainsKey(date))
                    return summary;
            }

            timeline = alignedTimeline;
            summary.StartDate = timeline[0];
            summary.EndDate = timeline[timeline.Count - 1];
        }
        else
        {
            var ordered = sorted.Keys.ToList();
            if (ordered.Count == 0)
                return summary;

            timeline = ordered;
            summary.StartDate = ordered[0];
            summary.EndDate = ordered[ordered.Count - 1];
        }

        if (timeline.Count < 2)
            return summary;

        var closes = new List<double>(timeline.Count);
        foreach (var date in timeline)
        {
            if (!sorted.TryGetValue(date, out var close))
                return summary;
            closes.Add(close);
        }

        PopulateSummaryMetrics(summary, timeline, closes);
        return summary;
    }

    private static void PopulateSummaryMetrics(
        UploadedFileSummary summary,
        IReadOnlyList<DateTime> timeline,
        IReadOnlyList<double> closes)
    {
        if (closes.Count < 2)
            return;

        var returns = new double[closes.Count - 1];

        for (int i = 1; i < closes.Count; i++)
        {
            var previous = closes[i - 1];
            var current = closes[i];
            if (!double.IsFinite(previous) || !double.IsFinite(current) || Math.Abs(previous) < 1e-12)
                return;

            var periodReturn = (current / previous) - 1.0;
            if (!double.IsFinite(periodReturn))
                return;

            returns[i - 1] = periodReturn;
        }

        var durationSeconds = (timeline[timeline.Count - 1] - timeline[0]).TotalSeconds;
        if (durationSeconds <= 0)
            return;

        var periodsPerYear = returns.Length * SecondsPerYear / durationSeconds;
        if (!double.IsFinite(periodsPerYear) || periodsPerYear <= 0)
            return;

        var mean = returns.Average();
        summary.AverageAnnualReturn = mean * periodsPerYear;

        if (returns.Length >= 2)
        {
            double varianceSum = 0;
            for (int i = 0; i < returns.Length; i++)
            {
                var diff = returns[i] - mean;
                varianceSum += diff * diff;
            }

            var variancePeriod = varianceSum / (returns.Length - 1);
            var varianceAnnual = variancePeriod * periodsPerYear;
            summary.AnnualVolatility = Math.Sqrt(Math.Max(varianceAnnual, 0.0));
        }
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

    private PortfolioVisualization? LoadStoredVisualization()
    {
        var json = HttpContext.Session.GetString(VisualizationSessionKey);
        if (string.IsNullOrEmpty(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<PortfolioVisualization>(json, JsonOptions);
        }
        catch
        {
            HttpContext.Session.Remove(VisualizationSessionKey);
            return null;
        }
    }

    private void SaveStoredVisualization(PortfolioVisualization? visualization)
    {
        if (visualization is null)
        {
            HttpContext.Session.Remove(VisualizationSessionKey);
            return;
        }

        var json = JsonSerializer.Serialize(visualization, JsonOptions);
        HttpContext.Session.SetString(VisualizationSessionKey, json);
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

    private static string NormalizeActiveTab(string? tab)
    {
        if (string.Equals(tab, "pro", StringComparison.OrdinalIgnoreCase))
            return "pro";
        if (string.Equals(tab, "visualizations", StringComparison.OrdinalIgnoreCase))
            return "visualizations";
        return "basic";
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

    public sealed class UploadedFileSummary
    {
        public string FileName { get; set; } = string.Empty;
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public double? AverageAnnualReturn { get; set; }
        public double? AnnualVolatility { get; set; }
    }
}


