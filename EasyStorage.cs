using EasyStorage.Core.Systems;
using System.IO;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace EasyStorage;

public class EasyStorage : Mod
{
    internal const byte StorageSnapshotPacket = 1;
    internal const byte DepositRequestPacket = 2;
    internal const byte WithdrawRequestPacket = 3;
    internal const byte CraftRequestPacket = 4;
    internal const byte CursorDepositRequestPacket = 5;
    internal const byte CursorDepositResultPacket = 6;

    public override void HandlePacket(BinaryReader reader, int whoAmI)
    {
        byte messageType = reader.ReadByte();
        if (Main.netMode == NetmodeID.MultiplayerClient)
        {
            if (messageType == StorageSnapshotPacket)
                StorageSystem.ReceiveSnapshot(reader);
            else if (messageType == CursorDepositResultPacket)
                StorageSystem.ReceiveCursorDepositResult(reader);

            return;
        }

        if (Main.netMode != NetmodeID.Server || whoAmI < 0 || whoAmI >= Main.maxPlayers || !Main.player[whoAmI].active)
            return;

        Player player = Main.player[whoAmI];
        switch (messageType)
        {
            case DepositRequestPacket:
                int slot = reader.ReadInt32();
                if (slot >= 0 && slot < player.inventory.Length && StorageSystem.TryStore(player.inventory[slot]))
                    NetMessage.SendData(MessageID.SyncEquipment, whoAmI, -1, null, whoAmI, slot);
                break;
            case WithdrawRequestPacket:
                int itemType = reader.ReadInt32();
                int itemPrefix = reader.ReadInt32();
                int amount = reader.ReadInt32();
                if (StorageSystem.TryWithdraw(itemType, itemPrefix, amount, out Item withdrawn))
                    player.QuickSpawnItem(player.GetSource_Misc("Easy Storage"), withdrawn);
                break;
            case CraftRequestPacket:
                int recipeIndex = reader.ReadInt32();
                if (StorageSystem.TryCraftRecipe(recipeIndex, out Item result))
                    player.QuickSpawnItem(player.GetSource_Misc("Easy Storage"), result);
                break;
            case CursorDepositRequestPacket:
                int type = reader.ReadInt32();
                int prefix = reader.ReadInt32();
                int count = reader.ReadInt32();
                bool stored = StorageSystem.TryStoreFromPlayerInventory(player, type, prefix, count, out int inventorySlot);
                if (stored)
                    NetMessage.SendData(MessageID.SyncEquipment, whoAmI, -1, null, whoAmI, inventorySlot);

                ModPacket response = GetPacket();
                response.Write(CursorDepositResultPacket);
                response.Write(type);
                response.Write(prefix);
                response.Write(count);
                response.Write(stored);
                response.Send(whoAmI);
                break;
        }
    }
}