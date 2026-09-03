using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Util;

namespace RiftWeapons
{
    /// <summary>
    /// The re-grip recipe: weapon + new handle -> weapon, and the OLD handle comes back.
    ///
    /// WHY THIS IS THE ONE RECIPE STILL BUILT IN CODE. Every other grid recipe is ordinary
    /// JSON. This one cannot be, because the handle we owe the player is not a fixed stack -
    /// it is whatever THAT weapon was wearing, with its tier, metal and grip material - and
    /// `CraftingRecipeIngredient.ReturnedStack` can only be a constant.
    ///
    /// `OnConsumedByCrafting` WAS TRIED AND FIRES TOO LATE (2026-08-18). It exists, it runs
    /// server-side, it hands us the player, and every guard passed - but the weapon is
    /// already gone when it is called. The trace read:
    ///
    ///     consumed: could not rebuild the old handle.
    ///     slotStack= inputs=[- - - rift-weapons:handle - - - - -]
    ///
    /// The consumed slot is empty AND the weapon is absent from every other input slot; only
    /// the not-yet-consumed handle remains. So there is nothing left to read. `ConsumeInput`
    /// is the only place that sees the inputs BEFORE they are taken. Do not "simplify" this
    /// back to the hook.
    ///
    /// A third party does not write this recipe - it is generated from their weapon
    /// definition, so they still ship no code.
    ///
    /// CAVEAT, UNVERIFIED: recipes are serialised to clients, which rebuild them as plain
    /// GridRecipe, so only the SERVER runs this override. Inventory is server-authoritative
    /// so the handle should arrive normally, but this has not been tested in multiplayer.
    /// </summary>
    public class RiftSwapRecipe : GridRecipe
    {
        /// <summary>The weapon this re-grips, so the old handle can be rebuilt from it.</summary>
        public WeaponDef Def;

        public override bool ConsumeInput(IPlayer byPlayer, ItemSlot[] inputSlots, int gridWidth)
        {
            // Read the outgoing handle BEFORE the inputs are consumed. This is the whole
            // reason the class exists.
            ItemStack old = OldHandle(byPlayer?.Entity?.World, inputSlots);

            bool ok = base.ConsumeInput(byPlayer, inputSlots, gridWidth);
            if (!ok || old == null) return ok;

            if (byPlayer.InventoryManager?.TryGiveItemstack(old, true) != true)
            {
                // hands full - put it on the floor rather than quietly deleting it
                byPlayer.Entity?.World?.SpawnItemEntity(old, byPlayer.Entity.Pos.XYZ);
            }
            return ok;
        }

        ItemStack OldHandle(IWorldAccessor world, ItemSlot[] inputSlots)
        {
            if (world == null || Def?.Code == null || Def.Handle == null) return null;

            // MATCHED, NOT COMPARED. `Def.Code` is a pattern - `rift-sword-*` since the
            // blade metal moved into the code - while this recipe was registered against one
            // concrete variant. An equality test found nothing and the old handle silently
            // stopped coming back.
            var weaponCode = new AssetLocation(Def.Code);
            ItemStack weapon = inputSlots
                .FirstOrDefault(s => s?.Itemstack?.Collectible?.Code != null
                                     && WildcardUtil.Match(weaponCode,
                                                           s.Itemstack.Collectible.Code))
                ?.Itemstack;

            ITreeAttribute src = weapon?.Attributes?.GetTreeAttribute("types");
            if (src == null) return null;

            Item handle = world.GetItem(new AssetLocation(Def.Handle));
            if (handle == null) return null;

            var stack = new ItemStack(handle);
            ITreeAttribute types = stack.Attributes.GetOrAddTreeAttribute("types");

            string handletier = src.GetString("handletier");
            types.SetString("tier",
                handletier == "basic" || handletier == "advanced" ? handletier : "base");
            types.SetString("handletier", handletier ?? "crude");

            string grip = src.GetString("grip") ?? "wood";
            types.SetString("grip", grip);

            // the grip material lives under a key named after the grip itself
            string mat = src.GetString(grip);
            if (mat != null) types.SetString(grip, mat);

            // a plain handle has no metal, and must not be given one
            string handlemetal = src.GetString("handlemetal");
            if (handlemetal != null)
            {
                types.SetString("handlemetal", handlemetal);
                types.SetString("metal", handlemetal);
            }

            // EVERYTHING ELSE THE DEFINITION NAMES. The lines above derive the tier, the grip
            // and the metal; anything else in `handleKeys` is copied straight across.
            //
            // THE GEM WAS LOST EXACTLY HERE. This list was written by hand when the only keys
            // were tier, grip and metal, and the gem axis arrived later - so a re-gripped
            // sword handed back a handle with no stone in it, silently. Driving it off the
            // definition means the next key added cannot go missing the same way.
            foreach (string key in Def.HandleKeys ?? new string[0])
            {
                if (types.HasAttribute(key)) continue;
                // A handle carries exactly ONE material key, the one its grip names. Copying
                // the rest would ride a stale `wood` onto a leather handle.
                if (key != grip && RiftTables.Grips.Contains(key)) continue;
                string value = src.GetString(key);
                if (value != null) types.SetString(key, value);
            }
            return stack;
        }

        /// <summary>
        /// The registry clones recipes, and the base Clone returns a plain GridRecipe -
        /// which would silently drop the behaviour above, and the handle would stop coming
        /// back with no error anywhere.
        /// </summary>
        public new GridRecipe Clone()
        {
            GridRecipe baseClone = base.Clone();
            return new RiftSwapRecipe
            {
                Def = Def,
                IngredientPattern = IngredientPattern,
                Ingredients = baseClone.Ingredients,
                Output = baseClone.Output,
                Width = Width,
                Height = Height,
                Name = Name,
                RecipeGroup = RecipeGroup,
                CopyAttributesFrom = CopyAttributesFrom,
                MergeAttributesFrom = MergeAttributesFrom,
            };
        }
    }
}
