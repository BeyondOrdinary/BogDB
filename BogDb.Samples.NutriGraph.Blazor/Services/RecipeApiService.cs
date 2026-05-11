using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BogDb.Samples.NutriGraph.Blazor.Services;

// ── Spoonacular API response DTOs ─────────────────────────────────────────────

public class SpoonacularSearchResponse
{
    [JsonPropertyName("results")] public List<SpoonacularRecipe> Results { get; set; } = [];
    [JsonPropertyName("totalResults")] public int TotalResults { get; set; }
}

public class SpoonacularRecipe
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("image")] public string Image { get; set; } = "";
    [JsonPropertyName("readyInMinutes")] public int ReadyInMinutes { get; set; }
    [JsonPropertyName("servings")] public int Servings { get; set; }
    [JsonPropertyName("diets")] public List<string> Diets { get; set; } = [];
    [JsonPropertyName("nutrition")] public SpoonacularNutrition? Nutrition { get; set; }
    [JsonPropertyName("extendedIngredients")] public List<SpoonacularIngredient> ExtendedIngredients { get; set; } = [];
}

public class SpoonacularNutrition
{
    [JsonPropertyName("nutrients")] public List<SpoonacularNutrient> Nutrients { get; set; } = [];
}

public class SpoonacularNutrient
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("amount")] public double Amount { get; set; }
    [JsonPropertyName("unit")] public string Unit { get; set; } = "";
}

public class SpoonacularIngredient
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("aisle")] public string Aisle { get; set; } = "";
    [JsonPropertyName("original")] public string Original { get; set; } = "";
}

// ── Service ───────────────────────────────────────────────────────────────────

public class RecipeApiService
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _baseUrl;

    // Supported intolerances per Spoonacular docs
    public static readonly string[] SupportedIntolerances =
    [
        "Dairy", "Egg", "Gluten", "Grain", "Peanut",
        "Seafood", "Sesame", "Shellfish", "Soy",
        "Sulfite", "Tree Nut", "Wheat"
    ];

    public RecipeApiService(HttpClient http, IConfiguration config)
    {
        _http = http;
        _apiKey = config["Spoonacular:ApiKey"] ?? "";
        _baseUrl = config["Spoonacular:BaseUrl"] ?? "https://api.spoonacular.com";
    }

    public bool HasApiKey => !string.IsNullOrWhiteSpace(_apiKey);

    /// <summary>
    /// Search Spoonacular complexSearch with full nutrition data.
    /// intolerances: comma-separated list of intolerance names to exclude
    /// </summary>
    public async Task<List<SpoonacularRecipe>> SearchRecipesAsync(
        string query, string? intolerances = null, int number = 10)
    {
        if (!HasApiKey) return [];

        var url = $"{_baseUrl}/recipes/complexSearch?" +
                  $"apiKey={_apiKey}" +
                  $"&query={Uri.EscapeDataString(query)}" +
                  $"&number={number}" +
                  $"&addRecipeNutrition=true" +
                  $"&addRecipeInformation=true" +
                  $"&fillIngredients=true";

        if (!string.IsNullOrWhiteSpace(intolerances))
            url += $"&intolerances={Uri.EscapeDataString(intolerances)}";

        try
        {
            var response = await _http.GetFromJsonAsync<SpoonacularSearchResponse>(url,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return response?.Results ?? [];
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Spoonacular API error: {ex.Message}");
            return [];
        }
    }

    /// <summary>Extract a specific nutrient value from the nutrition block.</summary>
    public static double GetNutrient(SpoonacularRecipe recipe, string nutrientName)
    {
        if (recipe.Nutrition?.Nutrients is null) return 0;
        var match = recipe.Nutrition.Nutrients
            .FirstOrDefault(n => n.Name.Equals(nutrientName, StringComparison.OrdinalIgnoreCase));
        return match?.Amount ?? 0;
    }
}
