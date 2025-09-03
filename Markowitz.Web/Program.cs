using Markowitz.Core.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorPages()
#if DEBUG
    .AddRazorRuntimeCompilation()
#endif
    ;

builder.Services.AddSingleton<ReturnService>();
builder.Services.AddSingleton<MarkowitzOptimizer>();

var app = builder.Build();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}
app.UseStaticFiles();
app.UseRouting();
app.MapRazorPages();
app.Run();
