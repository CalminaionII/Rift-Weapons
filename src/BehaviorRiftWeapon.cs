using System.Linq;
using Vintagestory.API.Config;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Util;

namespace RiftWeapons
{
    /// <summary>
    /// Everything <see cref="ItemRiftWeapon"/> does, as a BEHAVIOUR rather than an item class.
    ///
    /// WHY THIS EXISTS. An item gets exactly one class, and other mods want it. Combat
    /// Overhaul's melee system needs `CombatOverhaul:MeleeWeapon` in that slot; so, most
    /// likely, will the next combat mod. While the composing lived in an item class, taking
    /// Combat Overhaul meant giving up assembly, part swapping and the quench carry - the
    /// sword came out of the grid as an empty item. As a behaviour it sits BESIDE whatever
    /// class the item ends up with, so the class is free for someone else to claim.
    ///
    /// THE STAT OVERRIDES ARE GONE, AND THAT IS THE POINT. `ItemRiftWeapon` had to override
    /// `GetAttackPower` and `GetMaxDurability` and re-run the behaviour walk by hand, because
    /// ARL's item CLASS returns its attribute-derived number without walking - which silently
    /// swallowed every quench buff. With no ARL class in play, `CollectibleObject` runs its own
    /// walk: ARL's BEHAVIOUR supplies the number from the maps, then `Buffable` applies the
    /// quench on top of it. Vanilla does for free what `WalkStat` was written to do.
    ///
    /// **ORDER MATTERS.** ARL's behaviour must be declared BEFORE `Buffable` in the itemtype,
    /// or the buff is applied to a value ARL then overwrites.
    /// </summary>
    public class BehaviorRiftWeapon : CollectibleBehavior
    {
        ICoreAPI api;
        WeaponDef cached;
        bool warned;

        public BehaviorRiftWeapon(CollectibleObject collObj) : base(collObj) { }

        public override void OnLoaded(ICoreAPI api)
        {
            base.OnLoaded(api);
            this.api = api;
        }

        /// <summary>
        /// Resolved on first use rather than in OnLoaded. Asset loading has bitten this once
        /// already: looking the definition up too early produced a null that never recovered,
        /// and every weapon silently stopped composing.
        /// </summary>
        WeaponDef Def
        {
            get
            {
                cached ??= RiftConfig.For(collObj.Code);
                if (cached == null && !warned)
                {
                    warned = true;
                    api?.Logger.Warning("[Rift Weapons] {0} has the RiftWeapon behaviour but no "
                        + "definition names it - add one in riftweapons/weapons/", collObj.Code);
                }
                return cached;
            }
        }

        public override void OnCreatedByCrafting(ItemSlot[] allInputslots, ItemSlot outputSlot,
                                                 IRecipeBase byRecipe,
                                                 ref EnumHandling handling)
        {
            base.OnCreatedByCrafting(allInputslots, outputSlot, byRecipe, ref handling);
            WeaponDef def = Def;
            if (def == null || outputSlot?.Itemstack == null) return;

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
            // blade head, they are segments of its CODE. Re-gripping, there is no head in the
            // grid at all - the head half IS the finished weapon, whose code carries no
            // variants and whose tier and metal live in its attributes.
            if (headPart != null) FromCode(headPart, types, def.HeadCodeKeys);
            else Copy(head, types, def.HeadCodeKeys);
            // ONLY WHEN RE-GRIPPING. `carryFromHead` exists so a finished sword keeps its
            // quench and sharpening when its handle changes. Assembling from a raw head part
            // is different: the recipe already carries `applyquenchablebuffs`, so `Buffable`
            // adds the buff itself - and buffs are added with AddOnDuplicate, so carrying the
            // head`s tree as well STACKS them. A quenched head reading +10% produced a sword
            // reading +20%, seen in game 2026-08-21.
            // BUFFS are the exception, not the rule. They are added with AddOnDuplicate, and the
            // assemble recipe already applies the quench through `applyquenchablebuffs` - so
            // carrying the head`s tree as well STACKS them, and a +10% head made a +20% sword
            // (seen in game 2026-08-21). Everything else must survive assembly: XSkills stamps
            // `quality` on anything smithed at an anvil, and a sword that dropped it would
            // silently waste the smith`s skill.
            int carried = CarryVanillaAttributes(head, outputSlot.Itemstack, def.CarryFromHead,
                                                 skipBuffs: headPart != null);
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
            // attributes just composed, so this is only correct at the very end.
            RescaleDurability(head, outputSlot.Itemstack);

            if (api?.Side == EnumAppSide.Server && def.CarryFromHead?.Length > 0)
            {
                ItemStack outStack = outputSlot.Itemstack;
                api.Logger.VerboseDebug(
                    "[Rift Weapons] {0}: head had [{1}], carried {2}/{3}; "
                    + "durability in {4}/{5} -> out {6}/{7}",
                    collObj.Code,
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
        /// Run the stat through EVERY behaviour, and do not let one of them stop the others.
        ///
        /// ARL supplies its attribute-derived number and then sets `PreventSubsequent`, which
        /// halts the walk - so `Buffable` never applies the quench. A head quenched to +10%
        /// showed the buff in the tooltip and hit for the unbuffed number, which is the same
        /// hole the old `ItemRiftWeapon.WalkStat` existed to close.
        ///
        /// This behaviour is declared BEFORE ARL so it is reached first, computes the whole
        /// chain itself - ARL for the base, then Buffable and Sharpenable on top - and sets
        /// `PreventSubsequent` at the end so nothing is applied twice.
        /// </summary>
        delegate float StatStep(CollectibleBehavior bh, float value, ref EnumHandling h);

        float WalkStat(ItemStack stack, float seed, StatStep step)
        {
            if (stack == null || collObj?.CollectibleBehaviors == null) return seed;
            float value = seed;
            foreach (CollectibleBehavior bh in collObj.CollectibleBehaviors)
            {
                if (bh == this) continue;
                EnumHandling h = EnumHandling.PassThrough;
                float next = step(bh, value, ref h);
                // deliberately NOT honouring PreventSubsequent - see above
                if (h != EnumHandling.PassThrough) value = next;
            }
            return value;
        }

        public override float GetAttackPower(ItemStack withItemStack, float attackPower,
                                             ref EnumHandling handling)
        {
            float v = WalkStat(withItemStack, attackPower,
                (CollectibleBehavior bh, float value, ref EnumHandling h)
                    => bh.GetAttackPower(withItemStack, value, ref h));
            handling = EnumHandling.PreventSubsequent;
            return v;
        }

        public override int GetMaxDurability(ItemStack itemstack, int durability,
                                             ref EnumHandling handling)
        {
            float v = WalkStat(itemstack, durability,
                (CollectibleBehavior bh, float value, ref EnumHandling h)
                    => bh.GetMaxDurability(itemstack, (int)value, ref h));
            handling = EnumHandling.PreventSubsequent;
            return (int)v;
        }

        /// <summary>
        /// Re-scale carried-over durability against the weapon's COMPOSED maximum.
        ///
        /// Vanilla transfers durability as a FRACTION of the output's maximum, before this
        /// composes tier, metal and the handle keys - so the fraction is scaled against the
        /// bare itemtype's number rather than the real one. A sword at 1745/1770 stored
        /// 98.59% of 400 = 394 and resolved to 1370, losing two thirds of its life to a
        /// handle swap. Only the re-grip path is affected; a head part carries no
        /// `durability` attribute, so a fresh assembly transfers nothing and reads full.
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
        /// **ONLY EVER PASS THE HEAD PART.** Handing this an assembled weapon reads segments of
        /// the WEAPON's code, which is meaningless - `rift-battle-axe` would yield tier=battle,
        /// metal=axe. The caller decides, and falls back to copying attributes when the head
        /// half is a weapon.
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
        /// Carry vanilla's own stack attributes across assembly - quench bonuses live OUTSIDE
        /// ARL's `types` subtree, so a quenched head would otherwise lose everything it gained
        /// the moment it became a weapon.
        /// </summary>
        static int CarryVanillaAttributes(ItemStack from, ItemStack to, string[] keys,
                                          bool skipBuffs = false)
        {
            if (from == null || keys == null) return 0;
            int n = 0;
            foreach (string key in keys)
            {
                if (skipBuffs && key == "buffs") continue;
                IAttribute a = from.Attributes?[key];
                if (a != null) { to.Attributes[key] = a.Clone(); n++; }
            }
            return n;
        }

        /// <summary>
        /// The handle, as its own tooltip line instead of a bracket on the name.
        ///
        /// WHY IT MOVED, 2026-08-29. The name used to carry the whole build -
        /// "Bismuth bronze Sword (Bismuth bronze &amp; Plain Leather Handle)" - and the item
        /// info box draws the title across its FULL WIDTH, wrapping it straight over the
        /// icon. That layout is the engine's; no itemtype, lang file or behaviour can change
        /// it, and this mod carries no Harmony to patch it. **The only lever is the length of
        /// the name.** So the ten ARL name rules collapsed to one - `material-{metal} Sword` -
        /// and everything they used to say is printed here, where the box wraps properly.
        ///
        /// The wording is deliberately the same as the old brackets so a sword reads the way
        /// it always did, one line lower. Metal and colour reuse VANILLA's `material-*` and
        /// `color-*` lang keys, exactly as the ARL name rules did, so this stays translated
        /// in every language the game ships.
        ///
        /// **THE MOD KEYS MUST CARRY THE `rift-weapons:` PREFIX AND THE MODID IS NOT IT.**
        /// The modid is `riftweapons`; the ASSET DOMAIN, and so the lang domain, is
        /// `rift-weapons` with the hyphen - that is the folder under assets/. An unprefixed
        /// `Lang.Get` resolves against `game`, finds nothing, and returns THE KEY ITSELF, so
        /// the tooltip printed the literal text "riftweapons-handle" with the arguments
        /// silently dropped. It fails as a wrong string, never as an exception. The vanilla
        /// `material-*` / `color-*` lookups above are correct BARE precisely because they do
        /// live in `game`.
        /// </summary>
        public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc,
            IWorldAccessor world, bool withDebugInfo)
        {
            base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);

            ITreeAttribute types = inSlot?.Itemstack?.Attributes?.GetTreeAttribute("types");
            if (types == null) return;

            string grip = GripName(types);
            if (grip == null) return;

            // a crude handle has no metal at all, which is why this is not unconditional
            string metal = types.GetString("handlemetal");
            string text = string.IsNullOrEmpty(metal)
                ? grip
                : Lang.Get("rift-weapons:riftweapons-handle-withmetal", Lang.Get("material-" + metal), grip);

            // The tier picks the whole LABEL rather than wrapping the text - "Fine Handle:"
            // reads better than "Handle: ... (Fine)", and it keeps the qualifier next to the
            // word it qualifies instead of trailing the whole line.
            dsc.AppendLine(Lang.Get(
                types.GetString("handletier") == "advanced"
                    ? "rift-weapons:riftweapons-handle-fine"
                    : "rift-weapons:riftweapons-handle", text));
        }

        /// <summary>
        /// The grip half of the line - wood names its timber, cloth and leather their colour.
        /// Returns null when the stack carries no grip, which is every unassembled sword and
        /// any creative stack made before the attributes existed; the caller then prints
        /// nothing rather than a half-line.
        /// </summary>
        static string GripName(ITreeAttribute types)
        {
            string grip = types.GetString("grip");
            string value = grip switch
            {
                "wood" => types.GetString("wood"),
                "cloth" => types.GetString("cloth"),
                "leather" => types.GetString("leather"),
                _ => null,
            };
            if (string.IsNullOrEmpty(value)) return null;

            return grip switch
            {
                "wood" => Lang.Get("material-" + value),
                "cloth" => Lang.Get("rift-weapons:riftweapons-grip-cloth", Lang.Get("color-" + value)),
                _ => Lang.Get("rift-weapons:riftweapons-grip-leather", Lang.Get("color-" + value)),
            };
        }

        /// <summary>
        /// Copy only the named keys. A key the source does NOT carry is REMOVED from the
        /// output, so re-gripping a wooden weapon with a leather handle leaves no stale `wood`
        /// behind for the texture rules to find.
        /// </summary>
        static void Copy(ItemStack from, ITreeAttribute to, string[] keys)
        {
            if (keys == null) return;
            ITreeAttribute src = from?.Attributes?.GetTreeAttribute("types");
            foreach (string key in keys)
            {
                string v = src?.GetString(key);
                if (v == null) to.RemoveAttribute(key);
                else to.SetString(key, v);
            }
        }
    }
}
