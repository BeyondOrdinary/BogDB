# BogDb.Samples.NutriGraph.Blazor

**Theme:** Dietary intelligence — pull real recipe nutrition from the Spoonacular API and combine 7 numeric thresholds with allergen and diet-label graph patterns in a single Cypher traversal.
**Stack:** Blazor Interactive Server · BogDB in-memory graph · Spoonacular Food API · .NET 9

---

## What this sample demonstrates

A nutrition graph that's empty at startup (apart from a small set of static reference nodes) and grows as you search. Every search hits `complexSearch` on the Spoonacular API with `addRecipeNutrition=true`, the per-serving nutrient block is flattened into properties on a `Recipe` node, and ingredients, diet labels, and allergens are connected as separate node tables. The Dietary Lab page then turns slider settings into structural `MATCH … FREE_FROM` / `TAGGED` patterns plus numeric `WHERE` predicates — all in one Cypher query.

| Entity | Source | Details |
|---|---|---|
| `Recipe` | Spoonacular `complexSearch` | id, title, image, ready_minutes, servings, calories, protein_g, sodium_mg, magnesium_mg, sugar_g, vitamin_d_mcg, vitamin_c_mg |
| `Ingredient` | Per-recipe `extendedIngredients` | id, name, aisle |
| `DietLabel` | Pre-seeded + recipe `diets[]` | 12 common labels (vegan, ketogenic, paleo, low fodmap, whole30, …) |
| `Allergen` | Pre-seeded from supported intolerances | 12 entries (Dairy, Egg, Gluten, Grain, Peanut, Seafood, Sesame, Shellfish, Soy, Sulfite, Tree Nut, Wheat) |
| `CONTAINS` | Per-recipe ingest | `Recipe → Ingredient`, carries the original `amount` string |
| `TAGGED` | Per-recipe ingest | `Recipe → DietLabel` |
| `FREE_FROM` | Inferred from query intolerances + diet flags | `Recipe → Allergen` — recipes returned with `intolerances=dairy,gluten` are tagged free-from those allergens |

> The graph is empty until you run a Recipe Search. The 12 `DietLabel` and 12 `Allergen` reference nodes are seeded in the service constructor; everything else arrives via the API.

---

## The graph-native moment

A relational version of *"find me dairy-free, vegan recipes with at least 25 g protein, less than 800 mg sodium, and less than 10 g sugar"* needs a four-way join across recipes, recipe_diet, recipe_allergen, and the nutrition table — plus careful `NOT EXISTS` for the allergen exclusion.

In BogDB the diet and allergen constraints are *graph patterns*, and the nutrient thresholds are simple `WHERE` predicates on node properties:

```cypher
MATCH (r:Recipe)
MATCH (r)-[:FREE_FROM]->(:Allergen {name: 'Dairy'})
MATCH (r)-[:TAGGED]->(:DietLabel {name: 'vegan'})
WHERE r.protein_g >= 25
  AND r.sodium_mg <= 800
  AND r.sugar_g <= 10
RETURN DISTINCT r.title, r.calories, r.protein_g
ORDER BY r.calories ASC
```

The Dietary Lab page generates exactly this kind of query from the slider/chip UI — allergens and diet chips become extra `MATCH` lines, slider values become `WHERE` predicates.

---

## Pages

### Dashboard (`/`)
- 5 stat cards: Recipes, Ingredients, Diet Labels, Allergens, Nutrient Data Points (`recipes × 7`).
- API key warning callout if `Spoonacular:ApiKey` is unset in `appsettings.json`.
- "How It Works" walkthrough and a literal rendering of the graph model.

### Recipe Search (`/search`)
- Free-form search box + 4 quick-search chips (Chicken, Salmon, Pasta, Salad).
- 12 toggleable allergen chips — each adds an `intolerances=…` filter to the Spoonacular request.
- Pressing **Fetch Recipes** calls `RecipeApiService.SearchRecipesAsync(...)`, then `NutriGraphService.IngestSpoonacularRecipes(...)` upserts everything into the graph.
- Re-queries the graph (`MATCH (r:Recipe)-[:CONTAINS]->(i:Ingredient) … RETURN …`) to prove the ingest worked, and renders the result set as a colour-coded nutrition table.
- Sodium and sugar values are colour-banded (green/amber/red) by threshold.

### Dietary Lab (`/diet-lab`)
- 10 allergen chips (`FREE_FROM` exclusions) + 7 diet-label chips (`TAGGED` inclusions).
- 7 nutrient sliders: max sodium, min magnesium, max sugar, max calories, min protein, min vitamin D, min vitamin C.
- The **Generated Graph Traversal** panel updates live as you adjust filters — the same Cypher that gets executed when you press **Filter Graph**.
- Each slider shows a clinical reference line (e.g. *"Hypertension safe: < 1500 mg"*, *"AHA limit: < 25 g women, < 36 g men"*, *"Bone health RDA: 15–20 mcg"*).
- Result table renders matching recipes with all 7 nutrient columns.

### Cypher Editor (`/query`)
- Free-form Cypher textarea with **Run Query** and **Clear** buttons.
- Output rendered as a table, capped at 200 rows. Elapsed milliseconds displayed.
- 8 pre-loaded **Showcase Queries** covering aggregation, pattern matching, diet-label collection, allergen filtering, ingredient frequency, vitamin C scans, low-sugar/high-magnesium scans, and shared-ingredient pair detection (`(r1)-[:CONTAINS]->(i)<-[:CONTAINS]-(r2)`).

---

## Key APIs demonstrated

| API | Used in |
|---|---|
| `BogDatabase.CreateInMemory()` | `NutriGraphService` constructor |
| `EnsureNodeTable` | Schema — `Recipe` (12 properties), `Ingredient`, `DietLabel`, `Allergen` |
| `EnsureRelTable` | `CONTAINS` (with `amount` edge property), `TAGGED`, `FREE_FROM` |
| `UpsertNodeById` | Reference seeding (allergens + diets) and per-search recipe/ingredient ingest |
| `UpsertRelationshipById` | All three relationship types created during ingest |
| `BeginWriteTransaction()` / `Commit()` | Schema, static seed, and each batch ingest are separate transactions |
| `conn.Query(cypher)` | Dashboard stats, search re-query, lab traversal, free-form editor |
| `MATCH (a)-[:R]->(b)` patterns | All diet/allergen/ingredient joins |
| `WHERE` predicates on numeric properties | All 7 nutrient threshold filters |
| `collect()` / `DISTINCT` | Diet aggregation per recipe + shared-ingredient pairing |
| `(r1)-[:CONTAINS]->(i)<-[:CONTAINS]-(r2)` | Showcase query #8 — find recipes sharing ≥ 3 ingredients |
| `OPTIONAL MATCH` | Search re-query (recipes may or may not have diet labels) |

---

## Architecture patterns

1. **Static seed, dynamic growth.** `SeedStaticNodes()` writes the 12 + 12 reference nodes inside a single transaction at construction time. Recipes and ingredients arrive only through user-triggered searches — the graph reflects what you've looked at.
2. **`FREE_FROM` is inferred, not declared.** Spoonacular filters intolerances out at query time. When you search with `intolerances=dairy,gluten`, the sample tags every returned recipe with `FREE_FROM → Dairy` and `FREE_FROM → Gluten` because those allergens are guaranteed absent. Additional `FREE_FROM` edges are added by reading the recipe's `diets[]` (e.g. "dairy free" implies `FREE_FROM → Dairy`).
3. **Edge properties on `CONTAINS`.** Each `(Recipe)-[:CONTAINS]->(Ingredient)` carries an `amount` string (Spoonacular's `original` field, e.g. *"2 cloves garlic, minced"*). Most edges in the graph are pure structural — this one is the exception.
4. **Live-generated Cypher in Dietary Lab.** The `GeneratedCypher` property in `DietLab.razor` builds the query from current slider/chip state every time the UI re-renders, then shows it to the user *before* execution. The same string is fed into `Svc.Execute(...)` when **Filter Graph** is pressed.
5. **API key is opt-in.** The app boots fine without `Spoonacular:ApiKey` — pages render warnings instead of crashing. `RecipeApiService.HasApiKey` gates the actual outbound HTTP call. Free tier from Spoonacular is 150 requests/day.
6. **`r-{id}` / `ing-{id}` / `alg-{slug}` / `diet-{slug}` id prefixes.** Keeps the namespaces tidy and prevents collisions when the same numeric id appears across node types.

---

## Running the sample

```bash
cd BogDb.Samples.NutriGraph.Blazor
dotnet run
# → http://localhost:5219
```

Configure your API key in `appsettings.json` (free key from <https://spoonacular.com/food-api/console>):

```json
{
  "Spoonacular": {
    "ApiKey": "YOUR_KEY_HERE",
    "BaseUrl": "https://api.spoonacular.com"
  }
}
```

Then visit **Recipe Search**, run a query or two to populate the graph, and open **Dietary Lab** to experiment with the filter combinations.

---

## Graph schema

```
(Recipe {id, title, image, ready_minutes, servings,
         calories, protein_g, sodium_mg, magnesium_mg,
         sugar_g, vitamin_d_mcg, vitamin_c_mg})
   ─[:CONTAINS {amount}]──►  (Ingredient {id, name, aisle})
   ─[:TAGGED]────────────►  (DietLabel {id, name})
   ─[:FREE_FROM]─────────►  (Allergen {id, name})
```

The nutrient block lives entirely on the `Recipe` node so that threshold filtering is a `WHERE` predicate, not a multi-hop traversal. Allergen and diet semantics live in the *graph* because users want to compose them (e.g. *vegan AND dairy-free AND high-protein*) — that composition is where the graph pays off over a flat table.
