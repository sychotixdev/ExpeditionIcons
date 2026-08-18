using System.Collections.Generic;
using ExileCore2.Shared.Enums;

namespace ExpeditionIcons;

public class ExpeditionMarkerIconDescription
{
    public IconPickerIndex IconPickerIndex { get; init; }
    public MapIconsIndex DefaultIcon { get; init; }
    public List<string> BaseEntityMetadataSubstrings { get; set; } = new List<string>();

    /// <summary>
    /// MinimapIcon component names for this marker, matched by EXACT equality.
    /// Substring matching would be wrong here: "RewardChestCurrency" is a prefix of
    /// "RewardChestCurrencyRare", and those two share an identical .ao file, so a prefix
    /// match would silently classify every rare currency chest as a normal one.
    /// Takes priority over <see cref="BaseEntityMetadataSubstrings"/> when populated.
    /// </summary>
    public List<string> MinimapIconNames { get; set; } = new List<string>();

    public bool IsWeightCustomizable { get; init; } = true;
}