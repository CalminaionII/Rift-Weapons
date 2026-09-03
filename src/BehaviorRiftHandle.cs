using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace RiftWeapons
{
    /// <summary>
    /// Everything <see cref="ItemRiftHandle"/> does, as a BEHAVIOUR rather than an item class -
    /// see <see cref="BehaviorRiftWeapon"/> for why the class slot had to be given up.
    ///
    /// A handle is built up rather than declared: a sawn blank starts from its wood, cloth or
    /// leather re-grips it, a guard sets its tier and lends it a metal, and a gem decorates it.
    /// Nothing here is declared on a recipe output, which is what makes wrapping
    /// non-destructive - re-gripping a fitted handle keeps its guard, because nothing exists
    /// that could overwrite it.
    /// </summary>
    public class BehaviorRiftHandle : CollectibleBehavior
    {
        static readonly string[] MaterialKeys = { "wood", "cloth", "leather" };
        // Only the WRAPS exclude each other - see SetGrip. The wood underneath stays.
        static readonly string[] WrapKeys = { "cloth", "leather" };

        public BehaviorRiftHandle(CollectibleObject collObj) : base(collObj) { }

        public override void OnCreatedByCrafting(ItemSlot[] allInputslots, ItemSlot outputSlot,
                                                 IRecipeBase byRecipe,
                                                 ref EnumHandling handling)
        {
            base.OnCreatedByCrafting(allInputslots, outputSlot, byRecipe, ref handling);
            if (outputSlot?.Itemstack == null) return;

            ITreeAttribute to = outputSlot.Itemstack.Attributes.GetOrAddTreeAttribute("types");

            // 1. carry over whatever handle went in, if any
            ITreeAttribute prev = Find(allInputslots, RiftTables.Domain, "handle")
                                  ?.Attributes.GetTreeAttribute("types");
            if (prev != null)
            {
                foreach (var kv in prev)
                {
                    string val = prev.GetString(kv.Key);
                    if (val != null) to.SetString(kv.Key, val);
                }
            }

            // 2. a sawn blank starts from nothing but its wood
            ItemStack log = FindVanilla(allInputslots, "debarkedlog-");
            if (log != null && prev == null)
            {
                SetGrip(to, "wood", Segment(log, 1));
                to.SetString("tier", "base");
                to.SetString("handletier", "crude");
            }

            // 3. cloth or leather re-grips it, keeping everything else
            ItemStack cloth = FindVanilla(allInputslots, "cloth-");
            if (cloth != null) SetGrip(to, "cloth", Segment(cloth, 1));
            ItemStack leather = FindVanilla(allInputslots, "leather-normal-");
            if (leather != null) SetGrip(to, "leather", Segment(leather, 2));

            // 4. a guard decides the tier, and lends the handle its metal
            Fit(allInputslots, to, "handguard", "basic");
            Fit(allInputslots, to, "handguardcomplete", "advanced");

            // 5. a gem is pure decoration - it changes nothing but the look, and only a
            //    handle with metal has anywhere to seat one
            // NUGGETS, NOT GEMS, since 2026-08-22. Vanilla ships no loose gem this could use,
            // so the decoration is an ore nugget - `game:nugget-nativecopper` and friends,
            // seventeen of them. The ATTRIBUTE is still called `gem`: it names the slot, not
            // the stone, and renaming it would touch handleKeys, headgem, every shape and
            // texture rule on both items, the creative stacks and the shipped example.
            ItemStack gem = FindVanilla(allInputslots, "nugget-");
            if (gem != null && to.HasAttribute("handlemetal"))
            {
                string kind = Segment(gem, 1);          // nugget-nativecopper -> nativecopper
                if (kind != null) to.SetString("gem", kind);
            }
        }

        /// <summary>
        /// Fitting a guard sets the tier and lends the handle its metal.
        ///
        /// THE METAL COMES FROM THE GUARD'S ATTRIBUTES NOW, not its code. Guards and bands
        /// were variant items - `flatguard-iron` - until 2026-08-22, when they collapsed onto
        /// one ARL code each carrying `types.metal`. The old code read the metal off the code
        /// path and matched the guard by the PREFIX `handguard-`; with no dash left in
        /// `handguard` it matched nothing, `Fit` returned early, and every fitted handle came
        /// out CRUDE with the input handle's own metal - seen in game, both tiers.
        ///
        /// The code path is still read as a fallback so a third-party guard that is still a
        /// variant item keeps working.
        ///
        /// Matching is now EXACT, which is what keeps `handguard` from matching
        /// `handguardcomplete` - the job the trailing dash used to do. The FLAT guard is only
        /// ever an ingredient of the complete guard; it never fits a handle directly
        /// (Calm, 2026-08-21).
        /// </summary>
        static void Fit(ItemSlot[] slots, ITreeAttribute to, string guardCode, string tier)
        {
            ItemStack guard = FindExact(slots, RiftTables.Domain, guardCode)
                              ?? FindPrefixed(slots, RiftTables.Domain, guardCode + "-");
            if (guard == null) return;
            to.SetString("tier", tier);
            to.SetString("handletier", tier);

            string metal = guard.Attributes?.GetTreeAttribute("types")?.GetString("metal");
            if (string.IsNullOrEmpty(metal))
            {
                string path = guard.Collectible.Code.Path;
                int dash = path.LastIndexOf('-');
                metal = dash >= 0 ? path.Substring(dash + 1) : null;
            }
            if (!string.IsNullOrEmpty(metal))
            {
                to.SetString("metal", metal);
                to.SetString("handlemetal", metal);
            }
        }

        static ItemStack FindExact(ItemSlot[] slots, string domain, string path) =>
            slots.FirstOrDefault(s => s?.Itemstack?.Collectible?.Code != null
                                      && s.Itemstack.Collectible.Code.Domain == domain
                                      && s.Itemstack.Collectible.Code.Path == path)
                 ?.Itemstack;

        static ItemStack FindPrefixed(ItemSlot[] slots, string domain, string prefix) =>
            slots.FirstOrDefault(s => s?.Itemstack?.Collectible?.Code != null
                                      && s.Itemstack.Collectible.Code.Domain == domain
                                      && s.Itemstack.Collectible.Code.Path.StartsWith(prefix))
                 ?.Itemstack;

        /// <summary>
        /// Set the grip, keeping the timber the handle is made of.
        ///
        /// THE WOOD IS NOT A GRIP MATERIAL - IT IS THE HANDLE ITSELF. Every handle starts as a
        /// sawn blank of one wood, and a wrap goes ON TOP of it: the model proves it, because
        /// a wrapped grip group is `Handle` (the timber) plus `Handlegrip` (the wrap), and the
        /// timber is visible between the turns.
        ///
        /// This used to treat wood, cloth and leather as three mutually exclusive keys and
        /// dropped `wood` the moment a handle was wrapped. Nothing errored - the texture
        /// simply fell back to the itemtype default - so **every wrapped handle in the game
        /// showed OAK underneath, whatever it had been sawn from** (Calm, 2026-08-22).
        ///
        /// Only the two WRAPS exclude each other. Re-wrapping cloth over leather drops the
        /// leather; neither ever drops the wood.
        /// </summary>
        static void SetGrip(ITreeAttribute to, string grip, string material)
        {
            to.SetString("grip", grip);
            foreach (string key in WrapKeys)
            {
                if (key != grip && to.HasAttribute(key)) to.RemoveAttribute(key);
            }
            if (material != null) to.SetString(grip, material);
        }

        static ItemStack Find(ItemSlot[] slots, string domain, string path) =>
            slots.FirstOrDefault(s => s?.Itemstack?.Collectible?.Code != null
                                      && s.Itemstack.Collectible.Code.Domain == domain
                                      && s.Itemstack.Collectible.Code.Path == path)?.Itemstack;

        static ItemStack FindVanilla(ItemSlot[] slots, string prefix) =>
            slots.FirstOrDefault(s => s?.Itemstack?.Collectible?.Code != null
                                      && s.Itemstack.Collectible.Code.Domain == "game"
                                      && s.Itemstack.Collectible.Code.Path.StartsWith(prefix))
                 ?.Itemstack;

        /// <summary>Pull a dash-separated segment out of a code: leather-normal-black -> black.</summary>
        static string Segment(ItemStack stack, int index)
        {
            string[] parts = stack.Collectible.Code.Path.Split('-');
            return parts.Length > index ? parts[index] : null;
        }
    }
}
