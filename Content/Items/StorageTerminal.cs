using EasyStorage.Content.UI;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace EasyStorage.Content.Items;

public sealed class StorageTerminal : ModItem
{
    public override string Texture => "EasyStorage/Content/Items/StorageTerminal";

    public override void SetDefaults()
    {
        Item.width = 26;
        Item.height = 26;
        Item.maxStack = 1;
        Item.useStyle = ItemUseStyleID.HoldUp;
        Item.useTime = 20;
        Item.useAnimation = 20;
        Item.rare = ItemRarityID.Blue;
        Item.value = Item.buyPrice(gold: 1);
        Item.noMelee = true;
        Item.autoReuse = false;
    }

    public override bool? UseItem(Player player)
    {
        if (!Main.dedServ && player.whoAmI == Main.myPlayer)
            StorageUI.Show();

        return true;
    }
}
