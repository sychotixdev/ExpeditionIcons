using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.Elements;
using ExileCore2.PoEMemory.FilesInMemory;
using ExileCore2.PoEMemory.Models;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Cache;

namespace ExpeditionIcons;

/// <summary>A single recipe an encounter could still produce, with its resolved price.</summary>
public record RuneRecipeEntry(Expedition2Recipe Recipe, double Value, bool IsOverridden);

/// <summary>
/// The resolved state of one rune encounter. <see cref="Value"/> is the top pick,
/// which is both what the map text shows and what the path planner scores.
/// </summary>
/// <param name="IsRerolled">
/// A rerolled encounter has its recipe locked and cannot be changed, so <see cref="Recipes"/> holds
/// exactly the one it will produce and the planner has no choice left to make for it.
/// </param>
public record RuneValueInfo(double Value, bool IsOverridden, List<RuneRecipeEntry> Recipes, int RuneCount,
    List<int> PassedOnPositions, bool IsRerolled);

/// <summary>
/// Prices Expedition2 rune encounters. The resolution chain is a literal transcription of
/// Expedition2Good's Render method - keep it diffable against that plugin.
/// </summary>
public class RunePricer
{
    public const string EncounterMetadataPrefix = "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter";

    private static readonly (double Value, bool Overridden) NoPrice = (0, false);

    private readonly GameController _gameController;
    private readonly RuneDisplaySettings _settings;
    private readonly TimeCache<List<(LabelOnGround Log, Expedition2EncounterLabel Label)>> _labels;
    private readonly TimeCache<Dictionary<Expedition2Recipe, (double Value, bool Overridden)>> _price;
    private readonly TimeCache<ILookup<int, Expedition2Recipe>> _recipesByRuneCount;

    // Sticky: only ever overwritten when a real value resolves, never downgraded back to
    // unknown. A search runs for seconds across worker threads, so a value flickering
    // mid-run would change the score function underneath the genetic algorithm.
    private readonly Dictionary<uint, RuneValueInfo> _valuesByEntityId = new();

    // Also sticky. An encounter never becomes un-activated within a zone, and its label can
    // disappear once it is spent - rebuilding this set each pass would let a consumed
    // encounter silently re-enter the loot list and attract explosives again.
    private readonly HashSet<uint> _activated = new();

    private DateTime _nextUpdate = DateTime.MinValue;

    public RunePricer(GameController gameController, RuneDisplaySettings settings)
    {
        _gameController = gameController;
        _settings = settings;

        _labels = new TimeCache<List<(LabelOnGround, Expedition2EncounterLabel)>>(() =>
                _gameController.EntityListWrapper.Entities
                    .Any(x => x.Metadata?.StartsWith(EncounterMetadataPrefix, StringComparison.Ordinal) == true)
                    ? _gameController.IngameState.IngameUi.ItemsOnGroundLabelsVisible.Where(x =>
                            x?.ItemOnGround?.Metadata?.StartsWith(EncounterMetadataPrefix, StringComparison.Ordinal) == true)
                        .Select(x => (x, x.Label.AsObject<Expedition2EncounterLabel>())).ToList()
                    : []
            , 1000);

        _price = new TimeCache<Dictionary<Expedition2Recipe, (double, bool)>>(() =>
        {
            // Resolved inside the factory rather than once at init: NinjaPricer may register
            // its bridge method after we initialise. Absent bridge -> everything is unknown.
            var getCurrencyValue = _gameController.PluginBridge.GetMethod<Func<BaseItemType, double>>("NinjaPrice.GetBaseItemTypeValue") ?? (_ => 0);
            return _gameController.Files.Expedition2Recipes.EntriesList.ToDictionary(x => x, x =>
            {
                if (_settings.PriceOverrides.Content.FirstOrDefault(priceOverride => priceOverride.Type.Value == x.Id) is { } over)
                {
                    return (over.Value, true);
                }

                return ((x.Reward == null ? 0 : getCurrencyValue(x.Reward)) * x.RewardCount, false);
            });
        }, 1000);

        // Expedition2Good rebuilds this lookup on every frame. Pure performance, no behavior change.
        _recipesByRuneCount = new TimeCache<ILookup<int, Expedition2Recipe>>(
            () => _gameController.Files.Expedition2Recipes.EntriesList.ToLookup(x => x.RuneCountRequired), 5000);
    }

    /// <summary>Encounter labels currently known, for positioning the display.</summary>
    public List<(LabelOnGround Log, Expedition2EncounterLabel Label)> Labels => _labels.Value;

    public void Reset()
    {
        _valuesByEntityId.Clear();
        _activated.Clear();
        _nextUpdate = DateTime.MinValue;
    }

    public bool TryGetInfo(uint entityId, out RuneValueInfo info)
    {
        return _valuesByEntityId.TryGetValue(entityId, out info);
    }

    public bool IsActivated(uint entityId)
    {
        return _activated.Contains(entityId);
    }

    public (double Value, bool Overridden) GetPriceOrDefault(Expedition2Recipe recipe)
    {
        return recipe != null && _price.Value.TryGetValue(recipe, out var price) ? price : NoPrice;
    }

    public static bool IsEntityActivated(Entity entity)
    {
        var states = entity?.GetComponent<StateMachine>()?.States;
        return states != null && states.Any(s => s.Name == "activated" && (int)s.Value == 6);
    }

    /// <summary>
    /// A rerolled encounter is locked to whatever recipe it currently shows: the reward, the runes
    /// and the passed-on runes are all fixed and no choice can change them.
    /// </summary>
    public static bool IsEntityRerolled(Entity entity)
    {
        var states = entity?.GetComponent<StateMachine>()?.States;
        return states != null && states.Any(s => s.Name == "is_rerolled" && s.Value == 1);
    }

    public static int? GetSocketCount(Entity entity)
    {
        var value = entity?.GetComponent<StateMachine>()?.States?.FirstOrDefault(x => x.Name == "sockets")?.Value;
        return value == null ? null : (int)value.Value;
    }

    public void Update()
    {
        if (DateTime.UtcNow < _nextUpdate)
        {
            return;
        }

        _nextUpdate = DateTime.UtcNow.AddMilliseconds(250);

        // Unconditional, as in Expedition2Good's Tick: the price override dropdown has to be
        // populated even when you are not standing in an expedition.
        _settings.KnownRecipes.UnionWith(_price.Value.Keys.Select(x => x.Id));

        var labels = _labels.Value;
        if (labels is not { Count: > 0 })
        {
            return;
        }

        var allRecipes = _recipesByRuneCount.Value;
        if (allRecipes.Count == 0)
        {
            return;
        }

        var areaLevel = _gameController.IngameState.Data.CurrentAreaLevel;
        var runesWeights = _gameController.Files.Expedition2RunesWeights.EntriesList;

        foreach (var (log, label) in labels)
        {
            var entity = log.ItemOnGround;
            if (entity == null)
            {
                continue;
            }

            if (IsEntityActivated(entity))
            {
                _activated.Add(entity.Id);
            }

            //A rerolled encounter is locked, so its one recipe replaces the eligible list entirely -
            //built straight from SelectedRecipe rather than filtered out of the list, since a locked
            //recipe need not still satisfy the level and fixed-rune constraints ResolveRecipes applies.
            var rerolled = IsEntityRerolled(entity);
            var lockedRecipe = rerolled ? label.Data?.SelectedRecipe : null;
            List<RuneRecipeEntry> recipes;
            if (lockedRecipe != null)
            {
                var (lockedValue, lockedOverridden) = GetPriceOrDefault(lockedRecipe);
                recipes = [new RuneRecipeEntry(lockedRecipe, lockedValue, lockedOverridden)];
            }
            else
            {
                //Rerolled with no readable selection falls back to the normal list, which will read
                //as the best case rather than the true one.
                recipes = ResolveRecipes(label, areaLevel, allRecipes, runesWeights);
            }

            if (recipes.Count == 0)
            {
                // Sticky: leave any previously resolved value in place.
                continue;
            }

            var top = recipes[0];
            //PassedOnRunePositions are 0-based slot indices, matching FixedRunePosition.
            _valuesByEntityId[entity.Id] = new RuneValueInfo(top.Value, top.IsOverridden, recipes, label.RuneCount,
                label.Data.PassedOnRunePositions ?? [], rerolled);
        }
    }

    /// <summary>
    /// Every recipe the encounter could still produce, ranked by value descending.
    /// Transcribed from Expedition2Good.Render - each clause is load-bearing.
    /// </summary>
    private List<RuneRecipeEntry> ResolveRecipes(
        Expedition2EncounterLabel label,
        int areaLevel,
        ILookup<int, Expedition2Recipe> allRecipes,
        List<Expedition2RunesWeight> runesWeights)
    {
        var allowedRuneCounts = runesWeights
            .Where(x => x.RuneSlot - 1 == label.FixedRunePosition)
            .Where(x => x.Rune?.Equals(label?.FixedRune) == true)
            .Where(x => x.Level <= areaLevel)
            .Select(x => x.SlotCount)
            .ToHashSet();

        return allRecipes.Where(x => x.Key <= label.RuneCount)
            .SelectMany(x => x)
            .Where(x => allowedRuneCounts.Contains(x.RuneCountRequired))
            .Where(x => x.MinLevelReq <= areaLevel && x.MaxLevelReq >= areaLevel)
            .Where(x => x.Runes.ElementAtOrDefault(label.FixedRunePosition)?.Equals(label.FixedRune) == true)
            .Select(x =>
            {
                var (value, overridden) = GetPriceOrDefault(x);
                return new RuneRecipeEntry(x, value, overridden);
            })
            .OrderByDescending(x => x.Value)
            .ToList();
    }
}
