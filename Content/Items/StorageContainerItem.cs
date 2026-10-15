using EasyStorage.Content.Tiles;
using Terraria;
using Terraria.ModLoader;

namespace EasyStorage.Content.Items;

public sealed class StorageContainerItem : ModItem
{
    public override void SetDefaults()
    {
        Item.DefaultToPlaceableTile(ModContent.TileType<StorageContainerTile>());
        Item.width = 26;
        Item.height = 26;
        Item.maxStack = 99;
        Item.value = Item.buyPrice(silver: 50);
    }
}