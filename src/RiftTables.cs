using System.Collections.Generic;
using System.Linq;

namespace RiftWeapons
{
    /// <summary>
    /// The single source of truth for what this mod can be made of.
    ///
    /// These lists used to live smeared across ~650 item codes, then across generated JSON
    /// recipe files. Having them in one place is the whole point of the code conversion:
    /// adding a metal is one entry here, and the recipes, the creative stacks and the
    /// handbook all follow. Nothing below should be duplicated anywhere else.
    /// </summary>
    public static class RiftTables
    {
        public const string Domain = "rift-weapons";

        // ---------------------------------------------------------------- metals
        /// <summary>Blade metals, per tier. The tier decides the blade's shape.</summary>
        public static readonly Dictionary<string, string[]> BladeMetals = new()
        {
            ["crude"]    = new[] { "copper" },
            ["basic"]    = new[] { "blackbronze", "bismuthbronze", "tinbronze", "iron" },
            ["advanced"] = new[] { "steel", "meteoriciron", "silver", "gold" },
        };

        /// <summary>
        /// Metals the metal PARTS can be made from - guards, bands, and therefore handles.
        /// Copper is deliberately absent: it is a blade-only metal.
        /// </summary>
        public static readonly string[] PartMetals =
        {
            "blackbronze", "bismuthbronze", "tinbronze", "iron",
            "steel", "meteoriciron", "silver", "gold",
        };

        // ---------------------------------------------------------------- grips
        /// <summary>Grip material -> the attribute key that carries it, and its values.</summary>
        public static readonly Dictionary<string, string[]> GripMaterials = new()
        {
            ["wood"]    = new[] { "acacia", "baldcypress", "birch", "ebony", "kapok", "larch",
                                  "maple", "oak", "pine", "purpleheart", "redwood", "walnut" },
            ["cloth"]   = new[] { "black", "blue", "brown", "gray", "green", "orange",
                                  "pink", "plain", "purple", "red", "white", "yellow" },
            ["leather"] = new[] { "black", "blue", "gray", "green", "orange", "pink",
                                  "plain", "purple", "red", "white", "yellow" },
        };

        public static IEnumerable<string> Grips => GripMaterials.Keys;

        /// <summary>Handle tiers, weakest first. "crude" is a bare blank with no metal.</summary>
        public static readonly string[] HandleTiers = { "crude", "basic", "advanced" };

        // ---------------------------------------------------------------- durability
        /// <summary>Blade contribution, by tier and metal, for a WOOD grip.</summary>
        public static readonly Dictionary<string, int> BladeDurability = new()
        {
            ["copper"] = 400,
            ["blackbronze"] = 850, ["bismuthbronze"] = 800, ["tinbronze"] = 700, ["iron"] = 1110,
            ["steel"] = 2620, ["meteoriciron"] = 1620, ["silver"] = 2620, ["gold"] = 2620,
        };

        /// <summary>Flat bonus for the grip material, over wood. Same for every metal.</summary>
        public static readonly Dictionary<string, int> GripDurabilityBonus = new()
        {
            ["wood"] = 0, ["cloth"] = 200, ["leather"] = 400,
        };

        /// <summary>
        /// A fitted handle adds 10% of its own metal's blade durability, to the nearest 5.
        /// Derived rather than invented, so retuning a metal keeps the handle bonus in step.
        /// A crude handle has no metal and therefore adds nothing - Calm's call.
        /// </summary>
        public static int HandleBonus(string handleMetal)
        {
            if (handleMetal == null || !BladeDurability.TryGetValue(handleMetal, out int b)) return 0;
            return (int)((b * 0.10 + 2.5) / 5) * 5;
        }

        public static int Durability(string bladeMetal, string grip, string handleMetal)
        {
            int b = BladeDurability.TryGetValue(bladeMetal ?? "", out int v) ? v : 400;
            int g = GripDurabilityBonus.TryGetValue(grip ?? "wood", out int gv) ? gv : 0;
            return b + g + HandleBonus(handleMetal);
        }

        // ---------------------------------------------------------------- misc lookups
        public static string TierOf(string bladeMetal) =>
            BladeMetals.FirstOrDefault(kv => kv.Value.Contains(bladeMetal)).Key;

        /// <summary>The attribute key a grip's material is stored under - same as the grip.</summary>
        public static string MaterialKey(string grip) => grip;
    }
}
