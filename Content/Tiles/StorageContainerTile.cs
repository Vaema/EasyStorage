using EasyStorage.Content.Items;
using EasyStorage.Content.UI;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ObjectData;

namespace EasyStorage.Content.Tiles;

public sealed class StorageContainerTile : ModTile
{
    public override void SetStaticDefaults()
    {
        Main.tileFrameImportant[Type] = true;
        Main.tileNoAttach[Type] = true;
        TileObjectData.newTile.CopyFrom(TileObjectData.Style3x3);
        TileObjectData.newTile.Origin = new Point16(1, 2);
        TileObjectData.newTile.CoordinateHeights = [16, 16, 16];
        TileObjectData.addTile(Type);
        AddMapEntry(new Color(95, 135, 125), CreateMapEntryName());
        RegisterItemDrop(ModContent.ItemType<StorageContainerItem>());
        DustType = DustID.WoodFurniture;
    }

    public override bool RightClick(int i, int j)
    {
        StorageUI.Show();
        return true;
    }
}