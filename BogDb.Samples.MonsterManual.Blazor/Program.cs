using BogDb.Samples.MonsterManual.Blazor.Components;
using BogDb.Samples.MonsterManual.Blazor.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// MonsterGraphService holds the in-memory BogDb graph — MUST be singleton so data
// persists across all Blazor page navigations (AddHttpClient<T> = transient = broken).
builder.Services.AddSingleton<MonsterGraphService>();
// Provide a named HttpClient that the singleton can pull via IHttpClientFactory
builder.Services.AddHttpClient("MonsterApiClient", c =>
{
    c.DefaultRequestHeaders.Add("User-Agent", "BogDB-Blazor-Sample");
    c.Timeout = TimeSpan.FromSeconds(30);
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found");
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
