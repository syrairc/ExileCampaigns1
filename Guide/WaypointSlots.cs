using System;
using System.Collections.Generic;

namespace ExileCampaigns.Guide;

// verified world-map waypoint slot -> area name, per act (slot index = the map node's order).
// pure + testable; the dev overlay (WaypointGuide) and the destination resolver both read this.
public static class WaypointSlots
{
    public static readonly IReadOnlyDictionary<int, string[]> NamesByAct = new Dictionary<int, string[]>
    {
        [1] = new[]
        {
            "Lioneye's Watch",        // 0
            "The Twilight Strand",    // 1
            "The Coast",              // 2
            "The Tidal Island",       // 3
            "The Mud Flats",          // 4
            "The Fetid Pool",         // 5
            "The Flooded Depths",     // 6
            "The Submerged Passage",  // 7
            "The Ledge",              // 8
            "The Climb",              // 9
            "The Prison",             // 10
            "Prisoner's Gate",        // 11
            "The Ship Graveyard",     // 12
            "The Ship Graveyard Cave",// 13
            "Merveil's Caverns",      // 14
        },
        [2] = new[]
        {
            "The Forest Encampment",  // 0
            "The Southern Forest",    // 1
            "The Old Fields",         // 2
            "The Den",                // 3
            "The Crossroads",         // 4
            "The Crypt",              // 5
            "The Chamber of Sins",    // 6
            "The Broken Bridge",      // 7
            "The Riverways",          // 8
            "The Northern Forest",    // 9
            "The Western Forest",     // 10
            "The Weaver's Chambers",  // 11
            "The Vaal Ruins",         // 12
            "The Wetlands",           // 13
            "The Dread Thicket",      // 14
            "The Caverns",            // 15
            "The Fellshrine Ruins",   // 16
        },
        [3] = new[]
        {
            "The Sarn Encampment",       // 0
            "The City of Sarn",          // 1
            "The Slums",                 // 2
            "The Crematorium",           // 3
            "The Marketplace",           // 4
            "The Catacombs",             // 5
            "The Battlefront",           // 6
            "The Solaris Temple Level 1",// 7
            "The Solaris Temple Level 2",// 8
            "The Docks",                 // 9
            "The Sewers",                // 10
            "The Ebony Barracks",        // 11
            "The Lunaris Temple",        // 12
            "The Imperial Gardens",      // 13
            "The Library",               // 14
            "The Sceptre of God",        // 15
        },
        [4] = new[]
        {
            "Highgate",                     // 0
            "The Aqueduct",                 // 1
            "The Dried Lake",               // 2
            "The Mines Level 1",            // 3
            "The Crystal Veins",            // 4
            "Kaom's Dream",                 // 5
            "Kaom's Stronghold",            // 6
            "Daresso's Dream",              // 7
            "The Grand Arena",              // 8
            "The Belly of the Beast Level 1",// 9
            "The Harvest",                  // 10
            "The Ascent",                   // 11
        },
        [5] = new[]
        {
            "Overseer's Tower",        // 0
            "The Slave Pens",          // 1
            "The Control Blocks",      // 2
            "Oriath Square",           // 3
            "The Ruined Square",       // 4
            "The Templar Courts",      // 5
            "The Torched Courts",      // 6
            "The Chamber of Innocence",// 7
            "The Ossuary",             // 8
            "The Reliquary",           // 9
            "The Cathedral Rooftop",   // 10
        },
        [6] = new[]
        {
            "Lioneye's Watch",        // 0
            "The Twilight Strand",    // 1
            "The Coast",              // 2
            "The Tidal Island",       // 3
            "The Mud Flats",          // 4
            "The Karui Fortress",     // 5
            "The Ridge",              // 6
            "The Prison",             // 7
            "Prisoner's Gate",        // 8
            "The Western Forest",     // 9
            "The Riverways",          // 10
            "The Wetlands",           // 11
            "The Southern Forest",    // 12
            "The Cavern of Anger",    // 13
            "The Beacon",             // 14
            "The Brine King's Reef",  // 15
        },
        [7] = new[]
        {
            "The Bridge Encampment",     // 0
            "The Broken Bridge",         // 1
            "The Crossroads",            // 2
            "The Fellshrine Ruins",      // 3
            "The Crypt",                 // 4
            "The Chamber of Sins Level 1",// 5
            "Maligaro's Sanctum",        // 6
            "The Den",                   // 7
            "The Ashen Fields",          // 8
            "The Northern Forest",       // 9
            "The Dread Thicket",         // 10
            "The Causeway",              // 11
            "The Vaal City",             // 12
        },
        [8] = new[]
        {
            "The Sarn Ramparts",          // 0
            "The Sarn Encampment",        // 1
            "The Toxic Conduits",         // 2
            "The Grand Promenade",        // 3
            "The Bath House",             // 4
            "The High Gardens",           // 5
            "The Lunaris Concourse",      // 6
            "The Lunaris Temple",         // 7
            "The Quay",                   // 8
            "The Grain Gate",             // 9
            "The Imperial Fields",        // 10
            "The Solaris Concourse",      // 11
            "The Solaris Temple",         // 12
            "The Harbour Bridge",         // 13
        },
        [9] = new[]
        {
            "The Blood Aqueduct",     // 0
            "Highgate",               // 1
            "The Descent",            // 2
            "The Vastiri Desert",     // 3
            "The Oasis",              // 4
            "The Foothills",          // 5
            "The Boiling Lake",       // 6
            "The Tunnel",             // 7
            "The Quarry",             // 8
            "The Refinery",           // 9
            "The Belly of the Beast", // 10
            "The Rotting Core",       // 11
        },
        [10] = new[]
        {
            "Oriath Docks",           // 0
            "The Cathedral Rooftop",  // 1
            "The Ravaged Square",     // 2
            "The Torched Courts",     // 3
            "The Desecrated Chambers",// 4
            "The Canals",             // 5
            "The Feeding Trough",     // 6
            "The Control Blocks",     // 7
            "The Reliquary",          // 8
            "The Ossuary",            // 9
        },
    };

    // slot index for a destination area name, or -1. exact (case-insensitive) wins; else a normalized
    // match that ignores a trailing " Level N" so shortened slots (e.g. "The Chamber of Sins") still hit.
    public static int ResolveSlot(int act, string targetName)
    {
        if (string.IsNullOrEmpty(targetName) || !NamesByAct.TryGetValue(act, out var names))
            return -1;
        for (int i = 0; i < names.Length; i++)
            if (string.Equals(names[i], targetName, StringComparison.OrdinalIgnoreCase))
                return i;
        var t = NormalizeArea(targetName);
        for (int i = 0; i < names.Length; i++)
            if (NormalizeArea(names[i]) == t)
                return i;
        return -1;
    }

    // lowercase, trim, drop a trailing " Level <n>" so "The Crypt Level 1" == "The Crypt".
    public static string NormalizeArea(string name)
    {
        var s = (name ?? "").Trim();
        int idx = s.LastIndexOf(" Level ", StringComparison.OrdinalIgnoreCase);
        if (idx > 0 && int.TryParse(s[(idx + 7)..].Trim(), out _))
            s = s[..idx];
        return s.Trim().ToLowerInvariant();
    }
}
