using ExileCore2.PoEMemory.FilesInMemory;

namespace ExpeditionIcons.PathPlannerData;

/// <summary>
/// One recipe a runestone could be completed with, reduced to what scoring needs.
/// </summary>
/// <param name="Recipe">Kept for the expected-choice indicator, not used by scoring.</param>
/// <param name="Price">The static loot drop's value.</param>
/// <param name="RecipeRuneMask">Every rune in the recipe - scales this runestone's own monsters.</param>
/// <param name="PassedOnMask">Runes at the passed-on positions - scale later runestones' monsters.</param>
/// <param name="PassedOnProduct">Product of the passed-on runes' multipliers, precomputed for candidate ranking and display.</param>
public record RunestoneCandidate(
    Expedition2Recipe Recipe,
    double Price,
    ulong RecipeRuneMask,
    ulong PassedOnMask,
    double PassedOnProduct);
