using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Util;

namespace RiftWeapons
{
    /// <summary>
    /// Any weapon assembled from a head and a handle. Extends ARL's item class, so shapes,
    /// textures, durability, attack power and tool tier all still come from the JSON
    /// attribute maps; this adds how a weapon's attributes are decided when it is crafted.
    ///
    /// NOTHING HERE IS SWORD-SPECIFIC. Which item is the head, which is the handle, and
    /// which attribute keys come from each are read from the weapon's definition in
    /// `riftweapons/weapons/*.json`. A new weapon is that file plus its shapes and
    /// itemtypes - no code.
    ///
    /// WHY IT EXISTS AT ALL. A grid recipe merges WHOLE ingredients and the merge order
    /// decides every shared key at once. A weapon needs the opposite: `tier`/`metal` from
    /// the head, the grip keys from the handle. No ordering expresses that, which is why
    /// part-swapping is impossible in JSON. Composing explicitly also makes assembly and
    /// re-gripping the same operation.
    /// </summary>
    public class ItemRiftWeapon : AttributeRenderingLibrary.ItemShapeTexturesFromAttributes
    {
        WeaponDef cached;
        bool warned;

        /// <summary>
        /// Resolved on first use rather than in OnLoaded. Item classes and asset loading
        /// have bitten this once already: looking the definition up too early produced a
        /// null that never recovered, and every weapon silently stopped composing.
        /// </summary>
        WeaponDef Def
        {
            get
            {
                cached ??= RiftConfig.For(Code);
                if (cached == null && !warned)
                {
                    warned = true;
                    api?.Logger.Warning("[Rift Weapons] {0} uses the RiftWeapon class but no "
                        + "definition names it - add one in riftweapons/weapons/", Code);
                }
                return cached;
            }
        }

        public override void OnCreatedByCrafting(ItemSlot[] allInputslots, ItemSlot outputSlot,
                                                 IRecipeBase byRecipe)
        {
            base.OnCreatedByCrafting(allInputslots, outputSlot, byRecipe);
            WeaponDef def = Def;
            if (def == null) return;

            // Prefer a dedicated part over an assembled weapon for each half, so swapping a
            // handle into an existing weapon takes the head from the weapon and the grip
            // from the NEW handle rather than from itself.
            ItemStack headPart = Find(allInputslots, def.Head);
            ItemStack head = headPart ?? Find(allInputslots, def.Code);
            ItemStack handle = Find(allInputslots, def.Handle) ?? Find(allInputslots, def.Code);
            if (head == null && handle == null) return;

            ITreeAttribute types = outputSlot.Itemstack.Attributes.GetOrAddTreeAttribute("types");
            Copy(head, types, def.HeadKeys);

            // WHERE tier/metal COME FROM DEPENDS ON WHAT THE HEAD HALF IS. Assembling from a
            // blade head, they are segments of its CODE. Re-gripping, there is no head in
            // the grid at all - the head half IS the finished weapon, whose code carries no
            // variants and whose tier and metal live in its attributes.
            //
            // Reading the code regardless is what broke re-gripping: `rift-sword` splits
            // into two segments, `FromCode` wants two keys, its guard tripped and it set
            // NOTHING. `headKeys` does not list tier/metal either, so the blade's identity
            // was silently dropped - the sword came out named "material-{metal} Sword" with
            // base durability. Seen in game 2026-08-17.
            if (headPart != null) FromCode(headPart, types, def.HeadCodeKeys);
            else Copy(head, types, def.HeadCodeKeys);
            int carried = CarryVanillaAttributes(head, outputSlot.Itemstack, def.CarryFromHead);
            Copy(handle, types, def.HandleKeys);

            // A handle from before `handletier` existed, or any plain blank, still has to
            // produce a weapon with a visible grip rather than a bare blade.
            if (handle != null && !types.HasAttribute("handletier"))
            {
                string tier = handle.Attributes.GetTreeAttribute("types")?.GetString("tier");
                types.SetString("handletier",
                    tier == "basic" || tier == "advanced" ? tier : "crude");
            }

            // EVERYTHING ABOVE HAS TO HAVE RUN FIRST. Durability is derived from the
            // attributes just composed, so both of these are only correct at the very end.
            RescaleDurability(head, outputSlot.Itemstack);

            // One line per assembly, naming what the head HAD against what was carried, and
            // the durability either side. Quench and sharpen bonuses are invisible until the
            // tooltip is read, and "not carried" vs "not displayed" are different bugs that
            // look identical in game. This answers it from the log, not from a screenshot.
            if (api?.Side == EnumAppSide.Server && def.CarryFromHead?.Length > 0)
            {
                ItemStack outStack = outputSlot.Itemstack;
                api.Logger.VerboseDebug(
                    "[Rift Weapons] {0}: head had [{1}], carried {2}/{3}; "
                    + "durability in {4}/{5} -> out {6}/{7}",
                    Code,
                    head?.Attributes == null
                        ? "" : string.Join(" ", head.Attributes.Select(kv => kv.Key)),
                    carried, def.CarryFromHead.Length,
                    head == null ? -1 : head.Collectible.GetRemainingDurability(head),
                    head == null ? -1 : head.Collectible.GetMaxDurability(head),
                    outStack.Collectible.GetRemainingDurability(outStack),
                    outStack.Collectible.GetMaxDurability(outStack));
            }
        }

        /// <summary>
        /// Let the BEHAVIOURS have their say on attack power, which ARL does not.
        ///
        /// `CollectibleObject.GetAttackPower` seeds a value and then WALKS THE BEHAVIOURS,
        /// and that walk is the only place a `Buffable` buff with statcode `attackpower`
        /// is ever applied. ARL overrides the method to return its own attribute-derived
        /// number and returns straight out, so the walk never happens: a quenched sword
        /// showed `Hardened. +10% damage` in its tooltip and hit for exactly the table
        /// value - 6.0 for iron with a wood grip, 6.5 with leather, both unbuffed to the
        /// decimal. Measured in game 2026-08-18.
        ///
        /// So take ARL's number as the seed and run the walk ourselves. Reading the buff
        /// tree directly would work too, but this way anything else that hooks the stat
        /// keeps working and no buff key names are hard-coded - the names were guessed
        /// wrong once already tonight.
        /// </summary>
        public override float GetAttackPower(ItemStack withItemStack)
        {
            return WalkStat(withItemStack, base.GetAttackPower(withItemStack),
                (CollectibleBehavior bh, float v, ref EnumHandling h)
                    => bh.GetAttackPower(withItemStack, v, ref h));
        }

        /// <summary>
        /// The same hole on the durability side. A CLAY-COVERED head quenches into
        /// `durationbonus`, which becomes a buff with statcode `maxdurability` - useless if
        /// the behaviour walk never runs. Fixed here rather than waited for.
        /// </summary>
        public override int GetMaxDurability(ItemStack itemstack)
        {
            return (int)WalkStat(itemstack, base.GetMaxDurability(itemstack),
                (CollectibleBehavior bh, float v, ref EnumHandling h)
                    => bh.GetMaxDurability(itemstack, (int)v, ref h));
        }

        delegate float StatStep(CollectibleBehavior bh, float value, ref EnumHandling handling);

        /// <summary>
        /// Run one stat through every behaviour the way CollectibleObject would, starting
        /// from a value ARL has already computed. A behaviour that passes through leaves the
        /// value alone; one that handles it replaces it.
        /// </summary>
        float WalkStat(ItemStack stack, float seed, StatStep step)
        {
            if (stack == null || CollectibleBehaviors == null) return seed;
            float value = seed;
            foreach (CollectibleBehavior bh in CollectibleBehaviors)
            {
                EnumHandling handling = EnumHandling.PassThrough;
                float next = step(bh, value, ref handling);
                if (handling != EnumHandling.PassThrough) value = next;
                if (handling == EnumHandling.PreventSubsequent) break;
            }
            return value;
        }

        /// <summary>
        /// Re-scale carried-over durability against the weapon's COMPOSED maximum.
        ///
        /// Vanilla transfers durability as a FRACTION of the output's maximum, and it does
        /// that inside `base.OnCreatedByCrafting` - which runs BEFORE this class composes
        /// tier, metal and the handle keys. At that moment the weapon is still a bare
        /// itemtype, so the fraction is scaled against its base durability rather than the
        /// real one. Re-gripping a sword at 1745/1770 stored 98.59% of 400 = 394, and the
        /// maximum then resolved to 1370 - the sword lost two thirds of its life to a
        /// handle swap. Measured in game 2026-08-18; the arithmetic matched to the unit on
        /// two separate crafts.
        ///
        /// Fixing the fraction rather than the absolute number is also right on merit: a
        /// worse handle genuinely lowers the maximum, so carrying the old number across
        /// unchanged would be a free repair.
        ///
        /// Only the re-grip path is affected. A head part has no `durability` attribute at
        /// all, so a freshly assembled weapon transfers nothing and reads full.
        /// </summary>
        static void RescaleDurability(ItemStack from, ItemStack to)
        {
            if (from == null || from.Attributes?.HasAttribute("durability") != true) return;

            int inMax = from.Collectible.GetMaxDurability(from);
            int outMax = to.Collectible.GetMaxDurability(to);
            if (inMax <= 0 || outMax <= 0) return;

            int rem = (int)System.Math.Round(
                from.Collectible.GetRemainingDurability(from) * (double)outMax / inMax);
            // never hand back a weapon that breaks on the next swing, and never more than full
            to.Attributes.SetInt("durability", System.Math.Max(1, System.Math.Min(outMax, rem)));
        }

        static ItemStack Find(ItemSlot[] slots, string code)
        {
            if (code == null) return null;
            var loc = new AssetLocation(code);
            return slots.FirstOrDefault(s => s?.Itemstack?.Collectible?.Code != null
                                             && WildcardUtil.Match(loc, s.Itemstack.Collectible.Code))
                        ?.Itemstack;
        }

        /// <summary>
        /// Read attributes out of an item CODE rather than its stack attributes.
        ///
        /// The blade head is a normal variant item - `blade-head-basic-iron` - because
        /// vanilla's Quenchable is gated by code and CRASHES on an item whose metal is not
        /// in one. It has only 9 combinations, so codes cost nothing there. The finished
        /// weapon still stores tier and metal as ARL attributes, so this is where the two
        /// worlds meet: `headCodeKeys` names the code segments to lift, in order after the
        /// item's base name.
        ///
        /// **ONLY EVER PASS THE HEAD PART.** Handing this an assembled weapon reads segments
        /// of the WEAPON's code, which is meaningless - and actively wrong for a code with
        /// enough dashes: `rift-battle-axe` would yield tier=battle, metal=axe. The caller
        /// decides, and falls back to copying the attributes when the head half is a weapon.
        /// </summary>
        static void FromCode(ItemStack from, ITreeAttribute to, string[] keys)
        {
            if (from == null || keys == null || keys.Length == 0) return;
            string[] parts = from.Collectible.Code.Path.Split('-');
            // the base name may itself contain dashes, so count back from the END
            int first = parts.Length - keys.Length;
            if (first < 1) return;
            for (int i = 0; i < keys.Length; i++) to.SetString(keys[i], parts[first + i]);
        }

        /// <summary>
        /// Carry vanilla's own stack attributes across assembly - quench bonuses live
        /// OUTSIDE ARL's `types` subtree, so a quenched head would otherwise lose
        /// everything it gained the moment it became a weapon.
        /// </summary>
        static int CarryVanillaAttributes(ItemStack from, ItemStack to, string[] keys)
        {
            if (from == null || keys == null) return 0;
            int n = 0;
            foreach (string key in keys)
            {
                IAttribute a = from.Attributes?[key];
                if (a != null) { to.Attributes[key] = a.Clone(); n++; }
            }
            return n;
        }

        /// <summary>
        /// Copy only the named keys. A key the source does NOT carry is REMOVED from the
        /// output, so re-gripping a wooden weapon with a leather handle leaves no stale
        /// `wood` behind for the texture rules to find.
        /// </summary>
        static void Copy(ItemStack from, ITreeAttribute to, string[] keys)
        {
            if (from == null || keys == null) return;
            ITreeAttribute src = from.Attributes.GetTreeAttribute("types");
            foreach (string key in keys)
            {
                string val = src?.GetString(key);
                if (val != null) to.SetString(key, val);
                else if (to.HasAttribute(key)) to.RemoveAttribute(key);
            }
        }
    }
}
