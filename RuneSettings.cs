using System.Collections.Generic;
using System.Drawing;
using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Nodes;

namespace ExpeditionIcons;

/// <summary>
/// Pricing and display settings for Expedition2 rune encounters.
/// Ported from Expedition2GoodSettings; every default here matches Expedition2Good.
/// </summary>
[Submenu(CollapsedByDefault = true)]
public class RuneDisplaySettings
{
    [Menu("Enable rune values", "Prices Expedition2 rune encounters via the NinjaPrice plugin bridge and draws the results.")]
    public ToggleNode EnableRuneDisplay { get; set; } = new ToggleNode(true);

    [Menu("Show value on map", "Draws 'Rune <value> (<n> sockets)' at the encounter position on the minimap and the large map.")]
    public ToggleNode ShowOnMinimap { get; set; } = new ToggleNode(true);

    public ColorNode TextColor { get; set; } = new ColorNode(Color.LightBlue);
    public ColorNode TopPickColor { get; set; } = new ColorNode(Color.LightGreen);

    [Menu("Valuable color threshold", "Recipes worth at least this much use the valuable text color. Purely cosmetic - the planner uses its own threshold under Planner settings.")]
    public RangeNode<float> ValuableColorThreshold { get; set; } = new RangeNode<float>(50, 0, 10000);

    public ColorNode ValuableTextColor { get; set; } = new ColorNode(Color.Pink);

    [Menu("Minimum value to show", "When greater than 0, recipes with a value below this are hidden. Set to 0 to list every item and value.")]
    public RangeNode<float> MinimumValueToShow { get; set; } = new RangeNode<float>(0, 0, 500);

    [Menu("Max items to show", "Maximum number of recipes listed per label (highest value first). Set to 0 to show all.")]
    public RangeNode<int> MaxItemsToShow { get; set; } = new RangeNode<int>(0, 0, 20);

    [Menu("Display only non activated", "Hides expedition encounters whose StateMachine 'activated' state equals 6.")]
    public ToggleNode DisplayOnlyNonActivated { get; set; } = new ToggleNode(false);

    [Menu("Render offset X", "Horizontal offset (pixels) of the text relative to the on-ground object label. 0 keeps the original position.")]
    public RangeNode<int> RenderOffsetX { get; set; } = new RangeNode<int>(0, -2000, 2000);

    [Menu("Render offset Y", "Vertical offset (pixels) of the text relative to the on-ground object label. 0 keeps the original position.")]
    public RangeNode<int> RenderOffsetY { get; set; } = new RangeNode<int>(0, -2000, 2000);

    public ContentNode<PriceOverride> PriceOverrides { get; set; } = new ContentNode<PriceOverride>
        { Content = [], EnableControls = true, EnableItemCollapsing = true, ItemFactory = () => new PriceOverride(), };

    public HashSet<string> KnownRecipes = [];
}

/// <summary>
/// How the path planner weighs rune encounters.
/// Lives inside PlannerSettings because that is the only object handed to
/// PathPlanner and its worker threads.
/// </summary>
[Submenu(CollapsedByDefault = true)]
public class RuneScoringSettings
{
    [Menu("Consider rune values when planning", "When off, rune encounters are invisible to the path planner.")]
    public ToggleNode EnableRuneScoring { get; set; } = new ToggleNode(true);

    [Menu("Value threshold", "Encounters worth at least this much are chased, everything else is avoided.")]
    public RangeNode<float> ValueThreshold { get; set; } = new RangeNode<float>(50, 0, 1000);

    [Menu("Weight above threshold",
        "Score for an encounter that clears the threshold. For scale, a runic monster is 3 and a normal monster 0.2, so 25 means one good rune outweighs eight runic monsters.")]
    public RangeNode<float> AboveThresholdWeight { get; set; } = new RangeNode<float>(25, 0, 100);

    [Menu("Extra weight per point of value above threshold",
        "Breaks ties between qualifying encounters so a more expensive rune wins when only one is reachable.")]
    public RangeNode<float> AboveThresholdScale { get; set; } = new RangeNode<float>(0.05f, 0, 1);

    [Menu("Weight below threshold", "Score for an encounter below the threshold. Negative values make the planner route around it.")]
    public RangeNode<float> BelowThresholdWeight { get; set; } = new RangeNode<float>(-25, -100, 0);

    //The effect a covered rune has on runic monsters caught later along the path is configured
    //as a row in the relic weight modifier table, alongside every other relic modifier.
    //See PlannerSettings.RuneEncounterRelicSettings.
}

[Submenu]
public class PriceOverride
{
    public ListNode Type { get; set; } = new ListNode();
    public RangeNode<float> Value { get; set; } = new RangeNode<float>(0, 0, 10000);

    public override string ToString()
    {
        return string.IsNullOrWhiteSpace(Type.Value) ? base.ToString() : $"{Type.Value} -> {Value}";
    }
}
