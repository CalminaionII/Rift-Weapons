using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Util;

namespace RiftWeapons
{
    /// <summary>
    /// A weapon that can be assembled from two halves.
    ///
    /// Any mod can add one: drop a JSON file in
    /// `assets/&lt;yourdomain&gt;/config/riftweapons/weapons/` and Rift Weapons will generate its
    /// recipes and compose its attributes. You ship the itemtypes and shapes; this file says
    /// how the pieces fit together.
    /// </summary>
    public class WeaponDef
    {
        /// <summary>The finished weapon's item code, e.g. "rift-weapons:rift-sword".</summary>
        public string Code;

        /// <summary>The cutting half - a blade head, an axe head. Sets tier and metal.</summary>
        public string Head;

        /// <summary>The held half. Sets the grip and everything about the handle.</summary>
        public string Handle;

        /// <summary>Attribute keys taken from the head. Defaults suit a bladed weapon.</summary>
        public string[] HeadKeys = { "tier", "metal" };

        /// <summary>
        /// Attribute keys taken from the handle. These must not collide with HeadKeys -
        /// that is why a handle carries `handletier`/`handlemetal` rather than reusing
        /// `tier`/`metal`, which the head has already claimed.
        /// </summary>
        public string[] HandleKeys = { "handletier", "handlemetal", "grip",
                                       "wood", "cloth", "leather" };

        /// <summary>
        /// Attribute keys to read from the head's CODE instead of its attributes, in the
        /// order they appear at the END of the code. `blade-head-basic-iron` with
        /// [ "tier", "metal" ] yields tier=basic, metal=iron.
        ///
        /// Use this when the head is a normal variant item rather than an ARL one - which
        /// is what vanilla behaviours like Quenchable require.
        /// </summary>
        public string[] HeadCodeKeys = { };

        /// <summary>
        /// Vanilla stack attributes to carry from the head onto the finished weapon. These
        /// live outside ARL's `types` subtree, so without this a quenched head loses its
        /// bonus the moment it becomes a weapon.
        ///
        /// **`buffs` IS THE ONE THAT MATTERS, AND IT IS A TREE.** Vanilla's `Quenchable`
        /// writes bookkeeping of its own - `powervalue`, `durationbonus`, `shatterchance`,
        /// `metalworkingstate`, `quenchIteration` - but it does NOT apply the stat change
        /// itself. It hands the change to `CollectibleBehaviorBuffable`, which stores it in
        /// a `buffs` tree attribute and later applies `value *= Multiplier; value +=
        /// FlatChange`. Copy the bookkeeping without `buffs` and the sword gains nothing;
        /// copy `buffs` and it gains the actual bonus. The weapon must have the `Buffable`
        /// behavior for any of it to read - the sword does, which is how sharpening works.
        ///
        /// It also carries SHARPENING across a re-grip, because sharpening uses the same
        /// tree and a re-grip takes the head half from the existing weapon.
        ///
        /// Verified against vsessentialsmod, 2026-08-17. The five keys that were here
        /// before - quenched, tempered, quenchBonus, shatterChance, hardened - were guesses
        /// and NONE of them exist anywhere in the game.
        /// </summary>
        public string[] CarryFromHead = { };

        /// <summary>
        /// Turn a quenched head's stored numbers into real buffs on the finished weapon.
        ///
        /// **QUENCHING DOES NOT PUT A BUFF ON THE HEAD.** `Quenchable` records `powervalue`,
        /// `durationbonus`, `shatterchance` and `quenchIteration` on the head, and stops
        /// there - it tries to apply buffs through `CollectibleBehaviorBuffable`, which tool
        /// heads DO NOT HAVE, so that call returns immediately. Confirmed from a log line on
        /// assembly: the head carried nine vanilla attributes and `buffs` was not among them.
        ///
        /// The conversion happens at CRAFT time instead, and vanilla drives it from the
        /// recipe: `output: { recipeAttributes: { applyquenchablebuffs: true } }`.
        /// `Buffable.OnCreatedByCrafting` reads that off the output, scans the input slots
        /// for `powervalue`/`durationbonus`, and adds `hardened` buffs to the WEAPON, whose
        /// multiplier is `1 + powervalue`. The weapon needs the `Buffable` behavior for any
        /// of it to land - the sword has it, which is how sharpening already worked.
        ///
        /// Not set on the re-grip recipe: the weapon's buffs are already real by then and
        /// ride across in `CarryFromHead`. Applying it there too would risk stacking a
        /// second `hardened` on a weapon that still had raw quench numbers, since the buff
        /// is added with `AddOnDuplicate`.
        /// </summary>
        public bool ApplyQuenchableBuffs = true;

        /// <summary>Grid layout for assembling head + handle. "A" is the head, "B" the handle.</summary>
        public string Pattern = "A,B";
        public int Width = 1;
        public int Height = 2;

        /// <summary>
        /// Generate a "weapon + new handle -> weapon, old handle returned" recipe.
        /// Needs the weapon's own item class to be a RiftWeapon, so composition works.
        /// </summary>
        public bool Regrip = true;

        public override string ToString() => Code ?? "<unnamed weapon>";
    }

    /// <summary>
    /// The materials everything is made of. Shipped by Rift Weapons as
    /// `assets/rift-weapons/config/riftweapons/materials.json`, and PATCHABLE - a mod adding
    /// a metal patches that file rather than touching any code.
    /// </summary>
    public class MaterialDef
    {
        /// <summary>Head metals per tier. The tier picks the head's shape.</summary>
        public Dictionary<string, string[]> HeadMetals = new();

        /// <summary>Metals the guards and bands can be made from. Often excludes copper.</summary>
        public string[] PartMetals = { };

        /// <summary>Grip type -> the material values it accepts.</summary>
        public Dictionary<string, string[]> GripMaterials = new();

        /// <summary>Base durability per metal, for a wood grip.</summary>
        public Dictionary<string, int> Durability = new();

        /// <summary>Flat durability offset per grip type.</summary>
        public Dictionary<string, int> GripDurability = new();

        /// <summary>Base attack power per metal.</summary>
        public Dictionary<string, float> AttackPower = new();

        /// <summary>Flat attack offset per grip type.</summary>
        public Dictionary<string, float> GripAttack = new();

        public IEnumerable<string> Grips => GripMaterials.Keys;

        /// <summary>A fitted handle adds a fraction of its own metal's base durability.</summary>
        public double HandleDurabilityFraction = 0.10;

        public int HandleBonus(string handleMetal)
        {
            if (handleMetal == null || !Durability.TryGetValue(handleMetal, out int b)) return 0;
            return (int)((b * HandleDurabilityFraction + 2.5) / 5) * 5;
        }
    }

    /// <summary>
    /// Loads both from assets, so third-party mods extend Rift Weapons without referencing
    /// its assembly.
    ///
    /// PATH: `assets/&lt;domain&gt;/config/riftweapons/materials.json` and
    /// `assets/&lt;domain&gt;/config/riftweapons/weapons/*.json`. Every domain is scanned, so an
    /// addon simply ships its own files under its own domain.
    ///
    /// **THE `config/` PREFIX IS LOAD-BEARING - DO NOT TIDY IT AWAY.** The game only loads
    /// assets under a FIXED set of categories (blocktypes, itemtypes, config, recipes,
    /// patches, shapes, textures, lang, worldproperties...); `AssetCategory` holds them in a
    /// static table. A top-level `riftweapons/` folder is not one of them, so the loader
    /// skipped it entirely and `GetMany` found nothing - SILENTLY, because as far as the
    /// game is concerned those files do not exist. That produced "0 weapon(s)", which meant
    /// no assembly recipe, which in game looked like "the blade will not attach to the
    /// handle". Diagnosed from the server log 2026-08-17; it had already survived one
    /// wrong diagnosis (load timing) before that.
    /// </summary>
    public static class RiftConfig
    {
        public static MaterialDef Materials { get; private set; } = new();
        public static List<WeaponDef> Weapons { get; private set; } = new();

        public static void Load(ICoreAPI api)
        {
            Materials = new MaterialDef();
            Weapons = new List<WeaponDef>();

            // Materials merge across domains, so an addon can add a metal without
            // replacing the table - last one loaded wins per key, which is the same rule
            // JSON patching already follows.
            foreach (IAsset asset in api.Assets.GetMany("config/riftweapons/materials.json"))
            {
                try
                {
                    var m = JsonConvert.DeserializeObject<MaterialDef>(asset.ToText());
                    Merge(Materials, m);
                }
                catch (System.Exception e)
                {
                    api.Logger.Error("[Rift Weapons] {0} is not valid: {1}", asset.Location, e.Message);
                }
            }

            foreach (IAsset asset in api.Assets.GetMany("config/riftweapons/weapons"))
            {
                try
                {
                    var w = JsonConvert.DeserializeObject<WeaponDef>(asset.ToText());
                    if (w?.Code == null || w.Head == null || w.Handle == null)
                    {
                        api.Logger.Warning("[Rift Weapons] {0} needs code, head and handle",
                                           asset.Location);
                        continue;
                    }
                    Weapons.Add(w);
                }
                catch (System.Exception e)
                {
                    api.Logger.Error("[Rift Weapons] {0} is not valid: {1}", asset.Location, e.Message);
                }
            }

            api.Logger.Event("[Rift Weapons] {0} weapon(s), {1} head metal tier(s), {2} grip(s)",
                Weapons.Count, Materials.HeadMetals.Count, Materials.GripMaterials.Count);
        }

        static void Merge(MaterialDef into, MaterialDef from)
        {
            if (from == null) return;
            foreach (var kv in from.HeadMetals) into.HeadMetals[kv.Key] = kv.Value;
            foreach (var kv in from.GripMaterials) into.GripMaterials[kv.Key] = kv.Value;
            foreach (var kv in from.Durability) into.Durability[kv.Key] = kv.Value;
            foreach (var kv in from.GripDurability) into.GripDurability[kv.Key] = kv.Value;
            foreach (var kv in from.AttackPower) into.AttackPower[kv.Key] = kv.Value;
            foreach (var kv in from.GripAttack) into.GripAttack[kv.Key] = kv.Value;
            if (from.PartMetals.Length > 0)
                into.PartMetals = into.PartMetals.Union(from.PartMetals).ToArray();
            if (from.HandleDurabilityFraction > 0)
                into.HandleDurabilityFraction = from.HandleDurabilityFraction;
        }

        /// <summary>The definition for a given finished-weapon code, or null.</summary>
        ///
        /// MATCHED AS A PATTERN, NOT COMPARED AS A STRING. `w.Code` may be a wildcard -
        /// `rift-sword-*` since the blade metal moved into the code on 2026-08-21 - and
        /// string equality against `rift-sword-iron` finds nothing.
        ///
        /// THE FAILURE THIS CAUSED IS WORTH REMEMBERING, because nothing looked broken.
        /// A null definition makes `OnCreatedByCrafting` return before it copies the HANDLE
        /// keys, while the assemble recipe's own `types` - tier and metal - are applied by
        /// the recipe itself and arrive regardless. So the sword came out correctly named
        /// "Iron Sword" and correctly coloured, with no grip on it at all: the half that
        /// comes from JSON worked and the half that comes from here did not. The only
        /// evidence was one warning line in the server log.
        public static WeaponDef For(AssetLocation code) =>
            code == null ? null
            : Weapons.FirstOrDefault(w => w.Code != null
                                       && WildcardUtil.Match(new AssetLocation(w.Code), code));
    }
}
