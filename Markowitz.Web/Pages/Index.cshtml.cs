using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Markowitz.Core.Models;
using Markowitz.Core.Services;
using CsvHelper;
using System.Globalization;

namespace Markowitz.Web.Pages;

public class IndexModel : PageModel
{
    private readonly MarkowitzOptimizer _optimizer;

    public IndexModel(MarkowitzOptimizer optimizer) => _optimizer = optimizer;

    [BindProperty]
    public List<IFormFile> Files { get; set; } = new();

    [BindProperty] public int? LookbackDays { get; set; } = 252;
    [BindProperty] public DateTime? Start { get; set; }
    [BindProperty] public DateTime? End { get; set; }
    [BindProperty] public double? TargetReturnAnnual { get; set; } // например 0.10

    public OptimizationResult? Result { get; set; }

    public void OnGet() {}

    public async Task<IActionResult> OnPostAsync()
    {
        if (Files.Count == 0) { ModelState.AddModelError("", "Upload at least one CSV."); return Page(); }

        var dict = new Dictionary<string, List<Markowitz.Core.Models.PriceBar>>();

        foreach (var f in Files)
        {
            if (f.Length == 0) continue;
            var ticker = Path.GetFileNameWithoutExtension(f.FileName).Trim();

            using var stream = f.OpenReadStream();
            using var reader = new StreamReader(stream);
            using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
            csv.Read();
            csv.ReadHeader();

            var list = new List<Markowitz.Core.Models.PriceBar>();
            while (await csv.ReadAsync())
            {
                var ts = csv.GetField<DateTime>("timestamp");
                var open = csv.GetField<decimal>("open");
                var high = csv.GetField<decimal>("high");
                var low  = csv.GetField<decimal>("low");
                var close= csv.GetField<decimal>("close");
                list.Add(new(ts, open, high, low, close));
            }

            dict[ticker] = list;
        }

        var req = new OptimizationRequest
        {
            PricesByTicker = dict,
            LookbackDays = LookbackDays,
            Start = Start,
            End = End,
            TargetReturnAnnual = TargetReturnAnnual
        };

        Result = _optimizer.Optimize(req);
        return Page();
    }
}
