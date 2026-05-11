using System;
using System.Collections.Generic;
using System.Linq;
using BogDb.Core.Common;
using BogDb.Core.Main;

namespace BogDb.Samples.NutriGraph.Blazor.Services;

// ── Domain POCOs ──────────────────────────────────────────────────────────────

public record QueryResponse(bool IsSuccess, string Error, List<string> Columns, List<Dictionary<string, object?>> Rows, long ElapsedMs);

// ── Singleton Service ─────────────────────────────────────────────────────────

public sealed class NutriGraphService
{
    private readonly BogDatabase _db;
    private readonly BogConnection _conn;

    public NutriGraphService()
    {
        _db   = BogDatabase.CreateInMemory();
        _conn = new BogConnection(_db);
        SetupSchema();
        SeedStaticNodes();
    }

    // ── Public API ───────────────────────────────────────────────────────────

    public QueryResponse Execute(string cypher)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r  = _conn.Query(cypher);
        sw.Stop();

        if (!r.IsSuccess)
            return new QueryResponse(false, r.ErrorMessage ?? "Query failed", [], [], sw.ElapsedMilliseconds);

        var cols = r.ColumnNames.ToList();
        var rows = new List<Dictionary<string, object?>>();
        while (r.HasNext())
        {
            var row = r.GetNext();
            rows.Add(row.GetAsDictionary().ToDictionary(kv => kv.Key, kv => (object?)kv.Value));
        }
        return new QueryResponse(true, string.Empty, cols, rows, sw.ElapsedMilliseconds);
    }

    public (int Recipes, int Ingredients, int DietLabels, int Allergens, int NutrientPoints) GetStats()
    {
        int Count(string cypher)
        {
            var r = Execute(cypher);
            return r.IsSuccess && r.Rows.Count > 0 ? Convert.ToInt32(r.Rows[0].Values.First()) : 0;
        }

        return (
            Count("MATCH (r:Recipe) RETURN count(r)"),
            Count("MATCH (i:Ingredient) RETURN count(i)"),
            Count("MATCH (d:DietLabel) RETURN count(d)"),
            Count("MATCH (a:Allergen) RETURN count(a)"),
            Count("MATCH (r:Recipe) RETURN count(r)") * 7 // 7 nutrient data points per recipe
        );
    }

    // ── Schema ──────────────────────────────────────────────────────────────

    private void SetupSchema()
    {
        _conn.BeginWriteTransaction();

        _conn.EnsureNodeTable("Recipe", new()
        {
            ["id"]             = LogicalTypeID.STRING,
            ["title"]          = LogicalTypeID.STRING,
            ["image"]          = LogicalTypeID.STRING,
            ["ready_minutes"]  = LogicalTypeID.INT64,
            ["servings"]       = LogicalTypeID.INT64,
            ["calories"]       = LogicalTypeID.DOUBLE,
            ["protein_g"]      = LogicalTypeID.DOUBLE,
            ["sodium_mg"]      = LogicalTypeID.DOUBLE,
            ["magnesium_mg"]   = LogicalTypeID.DOUBLE,
            ["sugar_g"]        = LogicalTypeID.DOUBLE,
            ["vitamin_d_mcg"]  = LogicalTypeID.DOUBLE,
            ["vitamin_c_mg"]   = LogicalTypeID.DOUBLE,
        });

        _conn.EnsureNodeTable("Ingredient", new()
        {
            ["id"]    = LogicalTypeID.STRING,
            ["name"]  = LogicalTypeID.STRING,
            ["aisle"] = LogicalTypeID.STRING,
        });

        _conn.EnsureNodeTable("DietLabel", new()
        {
            ["id"]   = LogicalTypeID.STRING,
            ["name"] = LogicalTypeID.STRING,
        });

        _conn.EnsureNodeTable("Allergen", new()
        {
            ["id"]   = LogicalTypeID.STRING,
            ["name"] = LogicalTypeID.STRING,
        });

        _conn.EnsureRelTable("CONTAINS",  "Recipe", "Ingredient", new() { ["amount"] = LogicalTypeID.STRING });
        _conn.EnsureRelTable("TAGGED",    "Recipe", "DietLabel",  new());
        _conn.EnsureRelTable("FREE_FROM", "Recipe", "Allergen",   new());

        _conn.Commit();
    }

    // ── Static node seeding ─────────────────────────────────────────────────

    private void SeedStaticNodes()
    {
        _conn.BeginWriteTransaction();

        // Allergens (Spoonacular-supported intolerances)
        foreach (var name in RecipeApiService.SupportedIntolerances)
        {
            var id = AllergenId(name);
            _conn.UpsertNodeById("Allergen", id, new() { ["id"] = id, ["name"] = name });
        }

        // Common diet labels (Spoonacular returns these in recipe.diets[])
        var diets = new[] {
            "gluten free", "ketogenic", "vegetarian", "lacto-vegetarian",
            "ovo-vegetarian", "vegan", "pescetarian", "paleo",
            "primal", "low fodmap", "whole30", "dairy free"
        };
        foreach (var d in diets)
        {
            var id = DietId(d);
            _conn.UpsertNodeById("DietLabel", id, new() { ["id"] = id, ["name"] = d });
        }

        _conn.Commit();
    }

    // ── Ingest Spoonacular recipes ──────────────────────────────────────────

    public void IngestSpoonacularRecipes(List<SpoonacularRecipe> recipes, List<string>? excludedIntolerances = null)
    {
        if (recipes.Count == 0) return;

        _conn.BeginWriteTransaction();

        foreach (var recipe in recipes)
        {
            var recipeId = $"r-{recipe.Id}";

            double calories   = RecipeApiService.GetNutrient(recipe, "Calories");
            double protein    = RecipeApiService.GetNutrient(recipe, "Protein");
            double sodium     = RecipeApiService.GetNutrient(recipe, "Sodium");
            double magnesium  = RecipeApiService.GetNutrient(recipe, "Magnesium");
            double sugar      = RecipeApiService.GetNutrient(recipe, "Sugar");
            double vitaminD   = RecipeApiService.GetNutrient(recipe, "Vitamin D");
            double vitaminC   = RecipeApiService.GetNutrient(recipe, "Vitamin C");

            _conn.UpsertNodeById("Recipe", recipeId, new()
            {
                ["id"]            = recipeId,
                ["title"]         = recipe.Title,
                ["image"]         = recipe.Image ?? "",
                ["ready_minutes"] = (long)recipe.ReadyInMinutes,
                ["servings"]      = (long)recipe.Servings,
                ["calories"]      = calories,
                ["protein_g"]     = protein,
                ["sodium_mg"]     = sodium,
                ["magnesium_mg"]  = magnesium,
                ["sugar_g"]       = sugar,
                ["vitamin_d_mcg"] = vitaminD,
                ["vitamin_c_mg"]  = vitaminC,
            });

            // Ingredients
            foreach (var ing in recipe.ExtendedIngredients)
            {
                var ingId = $"ing-{ing.Id}";
                _conn.UpsertNodeById("Ingredient", ingId, new()
                {
                    ["id"] = ingId, ["name"] = ing.Name, ["aisle"] = ing.Aisle ?? ""
                });
                _conn.UpsertRelationshipById("CONTAINS", recipeId, ingId,
                    new() { ["amount"] = ing.Original ?? "" });
            }

            // Diet labels
            foreach (var diet in recipe.Diets)
            {
                var dietId = DietId(diet);
                _conn.UpsertNodeById("DietLabel", dietId, new() { ["id"] = dietId, ["name"] = diet });
                _conn.UpsertRelationshipById("TAGGED", recipeId, dietId, new());
            }

            // FREE_FROM: tag the recipe with allergens it's free from
            // Spoonacular filters OUT intolerances at query time, so if we searched with
            // intolerances=dairy,gluten, all returned recipes are free from those
            if (excludedIntolerances is { Count: > 0 })
            {
                foreach (var intol in excludedIntolerances)
                {
                    var algId = AllergenId(intol);
                    _conn.UpsertRelationshipById("FREE_FROM", recipeId, algId, new());
                }
            }

            // Also infer from diet labels
            if (recipe.Diets.Any(d => d.Contains("dairy free", StringComparison.OrdinalIgnoreCase)))
                _conn.UpsertRelationshipById("FREE_FROM", recipeId, AllergenId("Dairy"), new());
            if (recipe.Diets.Any(d => d.Contains("gluten free", StringComparison.OrdinalIgnoreCase)))
                _conn.UpsertRelationshipById("FREE_FROM", recipeId, AllergenId("Gluten"), new());
        }

        _conn.Commit();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string AllergenId(string name) => $"alg-{name.ToLower().Replace(" ", "-")}";
    private static string DietId(string name) => $"diet-{name.ToLower().Replace(" ", "-")}";

    // ── Showcase Queries ────────────────────────────────────────────────────

    public static readonly (string Label, string Cypher)[] ShowcaseQueries =
    [
        ("All recipes with full nutrition",
         "MATCH (r:Recipe)\nRETURN r.title AS Title, r.calories AS Calories, r.protein_g AS Protein_g,\n       r.sodium_mg AS Sodium_mg, r.magnesium_mg AS Magnesium_mg,\n       r.sugar_g AS Sugar_g, r.vitamin_c_mg AS VitC_mg, r.vitamin_d_mcg AS VitD_mcg\nORDER BY Calories ASC"),

        ("High protein, low sodium recipes",
         "MATCH (r:Recipe)\nWHERE r.protein_g >= 20 AND r.sodium_mg <= 600\nRETURN r.title, r.protein_g, r.sodium_mg, r.calories\nORDER BY r.protein_g DESC"),

        ("Recipes with their diet labels",
         "MATCH (r:Recipe)-[:TAGGED]->(d:DietLabel)\nRETURN r.title AS Recipe, collect(d.name) AS Diets\nORDER BY r.title"),

        ("Dairy-free recipes (via FREE_FROM)",
         "MATCH (r:Recipe)-[:FREE_FROM]->(:Allergen {name: 'Dairy'})\nRETURN r.title, r.calories, r.protein_g, r.sodium_mg\nORDER BY r.title"),

        ("Ingredient frequency across all recipes",
         "MATCH (r:Recipe)-[:CONTAINS]->(i:Ingredient)\nWITH i.name AS ingredient, COUNT(r) AS recipe_count\nRETURN ingredient, recipe_count\nORDER BY recipe_count DESC"),

        ("High vitamin C recipes (immune boost)",
         "MATCH (r:Recipe)\nWHERE r.vitamin_c_mg >= 20\nRETURN r.title, r.vitamin_c_mg, r.calories\nORDER BY r.vitamin_c_mg DESC"),

        ("Low sugar, high magnesium (metabolic health)",
         "MATCH (r:Recipe)\nWHERE r.sugar_g <= 10 AND r.magnesium_mg >= 30\nRETURN r.title, r.sugar_g, r.magnesium_mg, r.calories\nORDER BY r.magnesium_mg DESC"),

        ("Recipes sharing common ingredients",
         "MATCH (r1:Recipe)-[:CONTAINS]->(i:Ingredient)<-[:CONTAINS]-(r2:Recipe)\nWHERE r1.title < r2.title\nWITH r1.title AS Recipe_A, r2.title AS Recipe_B, collect(i.name) AS Shared_Ingredients\nWHERE size(Shared_Ingredients) >= 3\nRETURN Recipe_A, Recipe_B, Shared_Ingredients\nORDER BY size(Shared_Ingredients) DESC"),
    ];
}
