using BogDb.Samples.PackageEcosystem.Blazor.Components;
using BogDb.Samples.PackageEcosystem.Blazor.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// EF Core — SQLite source-of-truth for package ecosystem snapshots.
builder.Services.AddDbContextFactory<PackageEcosystemDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("PackageEcosystem")
                     ?? "Data Source=package_ecosystem.db"));

// Singleton graph service — EF feeds snapshots into BogDB on demand.
builder.Services.AddSingleton<PackageEcosystemGraphService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Eagerly warm up: seed the EF database and load all snapshots into BogDB.
using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PackageEcosystemDbContext>>();
    using var ctx = factory.CreateDbContext();
    ctx.Database.EnsureCreated();
    PackageEcosystemSeedData.Seed(ctx);
}
app.Services.GetRequiredService<PackageEcosystemGraphService>().WarmUp();

app.Run();
