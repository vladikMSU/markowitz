using Markowitz.Core.Services;
using Markowitz.Core.Services.Optimizers;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorPages()
#if DEBUG
    .AddRazorRuntimeCompilation()
#endif
    ;

builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession();

builder.Services.AddSingleton<ReturnService>();
builder.Services.AddSingleton<IPortfolioOptimizer, ClosedFormOptimizer>();
builder.Services.AddSingleton<IPortfolioOptimizer, QpOptimizer>();
builder.Services.AddSingleton<IPortfolioOptimizer, LpCvarOptimizer>();
builder.Services.AddSingleton<IPortfolioOptimizer, HeuristicOptimizer>();
builder.Services.AddSingleton<MarkowitzOptimizer>();

var app = builder.Build();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}
app.UseStaticFiles();
app.UseRouting();
app.UseSession();
app.MapRazorPages();
app.Run();
