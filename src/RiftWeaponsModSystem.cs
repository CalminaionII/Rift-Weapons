using System.Linq;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace RiftWeapons
{
    /// <summary>
    /// Registers the item classes and loads the weapon definitions. It no longer builds any
    /// recipes.
    ///
    /// WHY THE GENERATOR WENT AWAY (2026-08-18). Every grid recipe now ships as ordinary JSON
    /// in `recipes/grid/`. The generator existed because code could do three things JSON
    /// could not, and none of those hold any more:
    ///
    /// * "a recipe cannot require two ingredients to share a metal" - it can now. The parts
    ///   carry their metal in the CODE, so `flatguard-iron` + `metalbands-iron` is an
    ///   ordinary pair of concrete ingredients. What could not be done with attribute pins
    ///   is trivial with codes.
    /// * "enumerating variants needs pins, so the copies collapse to one signature" - with
    ///   real codes each enumerated recipe has its own signature.
    /// * "returning the old handle needs a GridRecipe subclass" - `OnConsumedByCrafting`
    ///   exists on CollectibleObject and hands us the player. See ItemRiftWeapon.
    ///
    /// WHAT THIS BUYS. Another mod adds a handle style, a part or a whole weapon with JSON
    /// alone: itemtypes, shapes, a weapon definition, and its own recipes. Nothing here needs
    /// editing, and nothing needs this assembly referenced.
    ///
    /// The weapon DEFINITION stays, because it is not recipe data - it says which half is the
    /// head, which keys come from where, and what to carry across a re-grip. A recipe cannot
    /// express that: the re-grip has no head ingredient to read it from.
    /// </summary>
    public class RiftWeaponsModSystem : ModSystem
    {
        public override void Start(ICoreAPI api)
        {
            base.Start(api);
            // RiftSword is kept as an alias so an existing itemtype naming it still loads;
            // RiftWeapon is the name to use for anything new.
            api.RegisterItemClass("riftweapons.RiftWeapon", typeof(ItemRiftWeapon));
            api.RegisterItemClass("riftweapons.RiftSword", typeof(ItemRiftWeapon));
            api.RegisterItemClass("riftweapons.RiftHandle", typeof(ItemRiftHandle));

            // The BEHAVIOUR route. An item gets one class and other mods want it - Combat
            // Overhaul needs `CombatOverhaul:MeleeWeapon` in that slot - so the composing is
            // available as a behaviour that sits beside whatever class the item ends up with.
            // The item classes above are kept so existing itemtypes keep working.
            api.RegisterCollectibleBehaviorClass("riftweapons.RiftWeapon", typeof(BehaviorRiftWeapon));
            api.RegisterCollectibleBehaviorClass("riftweapons.RiftHandle", typeof(BehaviorRiftHandle));
        }

        /// <summary>
        /// Assets do not exist yet in Start() - loading there silently produced zero weapons,
        /// and every weapon then failed to compose with no error anywhere. AssetsLoaded is
        /// the earliest point the files can actually be read.
        /// </summary>
        public override void AssetsLoaded(ICoreAPI api)
        {
            base.AssetsLoaded(api);
            RiftConfig.Load(api);
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            // Recipes are JSON now, so the only thing worth asserting at startup is that the
            // definitions loaded. Without them a weapon still CRAFTS - its recipe is JSON -
            // but it composes nothing and comes out blank, which is far more confusing than
            // a weapon that cannot be made at all. Hence an error rather than a warning.
            if (RiftConfig.Weapons.Count == 0)
            {
                api.Logger.Error("[Rift Weapons] NO WEAPON DEFINITIONS LOADED - weapons will "
                    + "craft but compose nothing. Expected config/riftweapons/weapons/*.json "
                    + "in some domain.");
            }
            AssertEveryWeaponHasADefinition(api);
            RegisterRegrips(api);
        }

        /// <summary>
        /// Every item that composes must be able to FIND its definition, at load rather than
        /// at the first craft.
        ///
        /// WHY THIS EXISTS. `RiftConfig.For` compared codes as strings until 2026-08-21, so
        /// when the blade metal moved into the code a definition reading `rift-sword-*` never
        /// matched `rift-sword-iron` and every sword resolved null. The item still crafted -
        /// the recipe is JSON - and the recipe's own `types` still applied, so it came out
        /// correctly named "Iron Sword" and correctly coloured with NO HANDLE ON IT. Only the
        /// half that comes from C# was missing, which reads as a modelling mistake rather
        /// than a lookup failure.
        ///
        /// The single warning that would have explained it fired at craft time, buried in a
        /// running server's log. At startup it is unmissable, and it names the fix.
        /// </summary>
        static void AssertEveryWeaponHasADefinition(ICoreServerAPI api)
        {
            int orphans = 0;
            foreach (Item item in api.World.Items)
            {
                if (item?.Code == null) continue;
                bool composes = item is ItemRiftWeapon
                    || item.CollectibleBehaviors?.Any(b => b is BehaviorRiftWeapon) == true;
                if (!composes || RiftConfig.For(item.Code) != null) continue;

                orphans++;
                api.Logger.Error("[Rift Weapons] {0} composes but NO definition matches it - "
                    + "it will craft and come out with no handle. Check that a weapon "
                    + "definition's `code` covers it; a wildcard like `{1}-*` is allowed.",
                    item.Code, item.Code.Path);
            }
            if (orphans == 0)
            {
                api.Logger.Event("[Rift Weapons] every composing item resolves a definition");
            }
        }

        /// <summary>
        /// The ONE recipe still built in code, generated from each weapon definition.
        ///
        /// It cannot be JSON because handing the old handle back needs a `GridRecipe`
        /// subclass - see RiftSwapRecipe for why the `OnConsumedByCrafting` hook fires too
        /// late. Generating it from the definition means a third party still writes no code:
        /// they ship a definition and get the re-grip for free.
        /// </summary>
        void RegisterRegrips(ICoreServerAPI api)
        {
            int n = 0;
            foreach (WeaponDef w in RiftConfig.Weapons)
            {
                if (!w.Regrip) continue;

                // ONE RECIPE PER CONCRETE WEAPON CODE, not one wildcard recipe. Since the
                // blade metal moved into the code (2026-08-21) `w.Code` is a pattern like
                // `rift-sword-*`, and a re-grip has to give back the SAME metal it was
                // handed - so input and output must name one variant each.
                //
                // JSON recipes get this free: the loader expands a wildcard ingredient and
                // substitutes the captured variant into the output code. A recipe built in
                // code and handed to `RegisterCraftingRecipe` never goes through that
                // expansion, so the wildcard would reach the registry unresolved and the
                // output would be a code no item has. Enumerating the registry is the
                // honest version of what the loader would have done.
                foreach (AssetLocation code in Matching(api, w.Code))
                {
                    var r = new RiftSwapRecipe
                    {
                        Def = w,
                        IngredientPattern = w.Pattern,
                        Width = w.Width,
                        Height = w.Height,
                        Ingredients = new()
                        {
                            ["A"] = Ingredient(code.ToString()),
                            ["B"] = Ingredient(w.Handle),
                        },
                        Output = OutputFor(w, code),
                        RecipeGroup = 0,
                        Name = new AssetLocation("riftweapons", "regrip"),
                    };

                    if (!r.Resolve(api.World, "Rift Weapons re-grip"))
                    {
                        api.Logger.Warning("[Rift Weapons] re-grip recipe for {0} did not "
                            + "resolve (handle {1})", code, w.Handle);
                        continue;
                    }
                    api.RegisterCraftingRecipe(r);
                    n++;
                }
            }
            api.Logger.Event("[Rift Weapons] registered {0} re-grip recipe(s); every other "
                + "recipe is JSON", n);
        }

        /// <summary>
        /// Every registered item whose code matches the pattern, in registry order.
        ///
        /// A plain code with no wildcard in it matches exactly itself, so a weapon
        /// definition written before the metal moved into the code still works.
        /// </summary>
        static System.Collections.Generic.List<AssetLocation> Matching(ICoreServerAPI api,
                                                                      string pattern)
        {
            var loc = new AssetLocation(pattern);
            var found = new System.Collections.Generic.List<AssetLocation>();
            foreach (Item item in api.World.Items)
            {
                if (item?.Code != null && WildcardUtil.Match(loc, item.Code)) found.Add(item.Code);
            }
            if (found.Count == 0)
            {
                api.Logger.Warning("[Rift Weapons] no item matches {0}; no re-grip registered",
                                   pattern);
            }
            return found;
        }

        /// <summary>
        /// The re-grip's output, declaring the variants its CODE already fixes.
        ///
        /// WHY IT DECLARES ANYTHING AT ALL. It used to declare nothing, on the reasoning that
        /// a re-grip decides neither the blade it keeps nor the handle it is given. That was
        /// true when the weapon was ONE code. It is not any more: `rift-sword-steel` can only
        /// ever produce a steel sword, so saying nothing is not honest, it is silent.
        ///
        /// AND SAYING NOTHING RENDERED WRONG. By the handbook's rules a recipe with NO
        /// declared output attributes appears on EVERY page of that item, drawn from a stack
        /// with no attributes - which ARL renders as its default, a crude copper blade. With
        /// one sword code and copper creative stacks that happened to look right. With seven
        /// it meant every non-copper page showed a re-grip producing a copper sword
        /// (reported in game 2026-08-21).
        ///
        /// Declaring the metal makes it a SUBSET of any real page, which by the same rules
        /// matches nothing - so the re-grip drops out of the handbook rather than lying on
        /// it. That is exactly what every other generic crafting recipe here does. If it
        /// should be VISIBLE instead, it needs pinned documentation entries matching the
        /// creative stacks exactly, the way assembly has 63.
        ///
        /// GENERIC, not sword-specific: whatever the wildcard in the definition's `code`
        /// captured is assigned to the TRAILING `headCodeKeys`, because those are the ones a
        /// code segment can supply. A definition with a plain code captures nothing and
        /// declares nothing, exactly as before.
        /// </summary>
        static CraftingRecipeIngredient OutputFor(WeaponDef w, AssetLocation code)
        {
            var output = Ingredient(code.ToString());
            string captured = WildcardUtil.GetWildcardValue(new AssetLocation(w.Code), code);
            if (string.IsNullOrEmpty(captured) || w.HeadCodeKeys == null) return output;

            string[] parts = captured.Split('-');
            string[] keys = w.HeadCodeKeys;
            if (parts.Length == 0 || parts.Length > keys.Length) return output;

            var types = new JObject();
            for (int i = 0; i < parts.Length; i++)
            {
                types[keys[keys.Length - parts.Length + i]] = parts[i];
            }
            output.Attributes = new JsonObject(new JObject { ["types"] = types });
            return output;
        }

        static CraftingRecipeIngredient Ingredient(string code) =>
            new() { Type = EnumItemClass.Item, Code = new AssetLocation(code), Quantity = 1 };
    }
}
