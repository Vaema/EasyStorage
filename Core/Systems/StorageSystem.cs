using System;
using System.Collections.Generic;
using System.IO;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;

namespace EasyStorage.Core.Systems;

public sealed class StorageSystem : ModSystem
{
    private const int InitialCapacity = 30;
    private const int CapacityIncrement = 30;
    private const int MaximumSnapshotSize = 16 * 1024 * 1024;

    private static readonly List<StorageEntry> entries = [];
    private static bool cursorDepositPending;
    private static int pendingCursorType;
    private static int pendingCursorPrefix;
    private static int pendingCursorCount;

    public static IReadOnlyList<StorageEntry> Entries => entries;

    public static int Capacity => Math.Max(InitialCapacity, ((entries.Count + CapacityIncrement - 1) / CapacityIncrement) * CapacityIncrement);

    public static bool CursorDepositPending => cursorDepositPending;

    public override void ClearWorld() => entries.Clear();

    public override void SaveWorldData(TagCompound tag)
    {
        List<TagCompound> savedEntries = new(entries.Count);
        foreach (StorageEntry entry in entries)
        {
            List<TagCompound> savedVariants = new(entry.MutableVariants.Count);
            foreach (StoredVariant variant in entry.MutableVariants)
            {
                savedVariants.Add(new TagCompound
                {
                    ["item"] = ItemIO.Save(variant.Item),
                    ["count"] = variant.Count
                });
            }

            savedEntries.Add(new TagCompound
            {
                ["type"] = entry.Type,
                ["prefix"] = entry.Prefix,
                ["variants"] = savedVariants
            });
        }

        tag["storageEntries"] = savedEntries;
    }

    public override void LoadWorldData(TagCompound tag)
    {
        entries.Clear();
        if (!tag.ContainsKey("storageEntries"))
            return;

        foreach (TagCompound savedEntry in tag.GetList<TagCompound>("storageEntries"))
        {
            int type = savedEntry.GetInt("type");
            int prefix = savedEntry.GetInt("prefix");
            if (type <= ItemID.None || type >= ItemLoader.ItemCount)
                continue;

            StorageEntry entry = new(type, prefix);
            foreach (TagCompound savedVariant in savedEntry.GetList<TagCompound>("variants"))
            {
                long count = savedVariant.GetLong("count");
                if (count <= 0)
                    continue;

                Item item = ItemIO.Load(savedVariant.GetCompound("item"));
                if (item.IsAir || item.type != type || item.prefix != prefix)
                    continue;

                item.stack = 1;
                entry.MutableVariants.Add(new StoredVariant(item, count));
            }

            if (entry.MutableVariants.Count > 0)
                entries.Add(entry);
        }
    }

    public override void NetSend(BinaryWriter writer) => WriteSnapshot(writer);

    public override void NetReceive(BinaryReader reader) => ReadSnapshot(reader);

    public static bool TryStore(Item item)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient || item is null || item.IsAir || item.stack <= 0)
            return false;

        StorageEntry entry = FindEntry(item.type, item.prefix);
        if (entry is null)
        {
            entry = new StorageEntry(item.type, item.prefix);
            entries.Add(entry);
        }

        Item storedItem = item.Clone();
        int count = storedItem.stack;
        storedItem.stack = 1;
        entry.MutableVariants.Add(new StoredVariant(storedItem, count));
        item.TurnToAir();
        BroadcastSnapshot();
        return true;
    }

    public static bool TryCraftRecipe(int recipeIndex, out Item craftedItem)
    {
        craftedItem = new Item();
        if (Main.netMode == NetmodeID.MultiplayerClient || recipeIndex < 0 || recipeIndex >= Main.recipe.Length)
            return false;

        Recipe recipe = Main.recipe[recipeIndex];
        if (recipe is null || recipe.Disabled || recipe.createItem.IsAir || !CanCraftRecipe(recipeIndex))
            return false;

        foreach (int requiredTile in recipe.requiredTile)
        {
            bool stationAvailable = false;
            foreach (StorageEntry entry in entries)
            {
                foreach (StoredVariant variant in entry.Variants)
                {
                    if (variant.Item.createTile == requiredTile)
                    {
                        stationAvailable = true;
                        break;
                    }
                }

                if (stationAvailable)
                    break;
            }

            if (!stationAvailable)
                return false;
        }

        Dictionary<StoredVariant, long> remaining = [];
        List<(StoredVariant Variant, long Amount)> consumption = [];
        foreach (Item ingredient in recipe.requiredItem)
        {
            if (ingredient.IsAir || ingredient.stack <= 0)
                continue;

            long needed = ingredient.stack;
            foreach (StorageEntry entry in entries)
            {
                if (!MatchesIngredient(ingredient.type, entry.Type))
                    continue;

                foreach (StoredVariant variant in entry.Variants)
                {
                    long available = remaining.TryGetValue(variant, out long left) ? left : variant.Count;
                    long used = Math.Min(needed, available);
                    if (used <= 0)
                        continue;

                    remaining[variant] = available - used;
                    consumption.Add((variant, used));
                    needed -= used;
                    if (needed == 0)
                        break;
                }

                if (needed == 0)
                    break;
            }

            if (needed > 0)
                return false;
        }

        foreach ((StoredVariant variant, long amount) in consumption)
            variant.Count -= amount;

        for (int entryIndex = entries.Count - 1; entryIndex >= 0; entryIndex--)
        {
            List<StoredVariant> variants = entries[entryIndex].MutableVariants;
            variants.RemoveAll(variant => variant.Count <= 0);
            if (variants.Count == 0)
                entries.RemoveAt(entryIndex);
        }

        craftedItem = recipe.createItem.Clone();
        BroadcastSnapshot();
        return true;
    }

    public static void RequestDeposit(int inventorySlot)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient)
        {
            ModPacket packet = ModContent.GetInstance<EasyStorage>().GetPacket();
            packet.Write(EasyStorage.DepositRequestPacket);
            packet.Write(inventorySlot);
            packet.Send();
            return;
        }

        Player player = Main.LocalPlayer;
        if (inventorySlot >= 0 && inventorySlot < player.inventory.Length)
            TryStore(player.inventory[inventorySlot]);
    }

    public static void RequestDepositMouseItem(Item cursorItem)
    {
        if (cursorItem is null || cursorItem.IsAir || cursorDepositPending)
            return;

        if (Main.netMode == NetmodeID.MultiplayerClient)
        {
            pendingCursorType = cursorItem.type;
            pendingCursorPrefix = cursorItem.prefix;
            pendingCursorCount = cursorItem.stack;
            cursorDepositPending = true;

            ModPacket packet = ModContent.GetInstance<EasyStorage>().GetPacket();
            packet.Write(EasyStorage.CursorDepositRequestPacket);
            packet.Write(pendingCursorType);
            packet.Write(pendingCursorPrefix);
            packet.Write(pendingCursorCount);
            packet.Send();
            return;
        }

        Item depositedItem = cursorItem.Clone();
        if (TryStore(depositedItem))
            cursorItem.TurnToAir();
    }

    public static bool TryStoreFromPlayerInventory(Player player, int type, int prefix, int count, out int inventorySlot)
    {
        inventorySlot = -1;
        if (player is null || type <= ItemID.None || count <= 0)
            return false;

        for (int slot = 0; slot < player.inventory.Length; slot++)
        {
            Item source = player.inventory[slot];
            if (source.IsAir || source.type != type || source.prefix != prefix || source.stack < count)
                continue;

            Item depositedItem = source.Clone();
            depositedItem.stack = count;
            if (!TryStore(depositedItem))
                return false;

            source.stack -= count;
            if (source.stack <= 0)
                source.TurnToAir();
            inventorySlot = slot;
            return true;
        }

        return false;
    }

    public static void ReceiveCursorDepositResult(BinaryReader reader)
    {
        int type = reader.ReadInt32();
        int prefix = reader.ReadInt32();
        int count = reader.ReadInt32();
        bool succeeded = reader.ReadBoolean();
        if (!cursorDepositPending || type != pendingCursorType || prefix != pendingCursorPrefix || count != pendingCursorCount)
            return;

        if (succeeded)
        {
            Item cursorItem = Main.mouseItem;
            if (!cursorItem.IsAir && cursorItem.type == type && cursorItem.prefix == prefix && cursorItem.stack >= count)
            {
                cursorItem.stack -= count;
                if (cursorItem.stack <= 0)
                    cursorItem.TurnToAir();
            }
        }

        cursorDepositPending = false;
    }

    public static void RequestWithdraw(int entryIndex, int amount)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient)
        {
            ModPacket packet = ModContent.GetInstance<EasyStorage>().GetPacket();
            packet.Write(EasyStorage.WithdrawRequestPacket);

            if (entryIndex < 0 || entryIndex >= entries.Count)
                return;

            packet.Write(entries[entryIndex].Type);
            packet.Write(entries[entryIndex].Prefix);
            packet.Write(amount);
            packet.Send();
            return;
        }

        if (TryWithdraw(entryIndex, amount, out Item item))
            Main.LocalPlayer.QuickSpawnItem(Main.LocalPlayer.GetSource_Misc("Easy Storage"), item);
    }

    public static void RequestCraft(int recipeIndex)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient)
        {
            ModPacket packet = ModContent.GetInstance<EasyStorage>().GetPacket();
            packet.Write(EasyStorage.CraftRequestPacket);
            packet.Write(recipeIndex);
            packet.Send();
            return;
        }

        if (TryCraftRecipe(recipeIndex, out Item item))
            Main.LocalPlayer.QuickSpawnItem(Main.LocalPlayer.GetSource_Misc("Easy Storage"), item);
    }

    public static bool TryWithdraw(int entryIndex, long requestedCount, out Item withdrawnItem)
    {
        if (entryIndex < 0 || entryIndex >= entries.Count)
        {
            withdrawnItem = new Item();
            return false;
        }

        return TryWithdraw(entries[entryIndex].Type, entries[entryIndex].Prefix, requestedCount, out withdrawnItem);
    }

    public static bool TryWithdraw(int type, int prefix, long requestedCount, out Item withdrawnItem)
    {
        withdrawnItem = new Item();
        if (Main.netMode == NetmodeID.MultiplayerClient || requestedCount <= 0)
            return false;

        int entryIndex = -1;
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Type == type && entries[i].Prefix == prefix)
            {
                entryIndex = i;
                break;
            }
        }
        if (entryIndex < 0)
            return false;

        StorageEntry entry = entries[entryIndex];
        StoredVariant variant = entry.Variants[0];
        Item item = variant.Item.Clone();
        int maxStack = Math.Max(1, item.maxStack);
        int amount = (int)Math.Min(requestedCount, Math.Min(variant.Count, maxStack));
        item.stack = amount;
        variant.Count -= amount;

        if (variant.Count == 0)
            entry.MutableVariants.RemoveAt(0);
        if (entry.Variants.Count == 0)
            entries.RemoveAt(entryIndex);

        withdrawnItem = item;
        BroadcastSnapshot();
        return true;
    }

    public static bool CanCraftRecipe(int recipeIndex)
    {
        if (recipeIndex < 0 || recipeIndex >= Main.recipe.Length)
            return false;

        Recipe recipe = Main.recipe[recipeIndex];
        if (recipe is null || recipe.Disabled || recipe.createItem.IsAir)
            return false;

        foreach (Condition condition in recipe.Conditions)
        {
            if (!condition.IsMet())
                return false;
        }

        foreach (int requiredTile in recipe.requiredTile)
        {
            bool foundStation = false;
            foreach (StorageEntry entry in entries)
            {
                foreach (StoredVariant variant in entry.Variants)
                {
                    if (variant.Item.createTile == requiredTile)
                    {
                        foundStation = true;
                        break;
                    }
                }
                if (foundStation)
                    break;
            }
            if (!foundStation)
                return false;
        }

        Dictionary<StoredVariant, long> remaining = [];
        foreach (Item ingredient in recipe.requiredItem)
        {
            if (ingredient.IsAir || ingredient.stack <= 0)
                continue;

            long needed = ingredient.stack;
            foreach (StorageEntry entry in entries)
            {
                if (!MatchesIngredient(ingredient.type, entry.Type))
                    continue;

                foreach (StoredVariant variant in entry.Variants)
                {
                    long available = remaining.TryGetValue(variant, out long left) ? left : variant.Count;
                    long used = Math.Min(needed, available);
                    remaining[variant] = available - used;
                    needed -= used;
                    if (needed == 0)
                        break;
                }

                if (needed == 0)
                    break;
            }
            if (needed > 0)
                return false;
        }
        return true;
    }

    public static Item CreateDisplayItem(int entryIndex)
    {
        if (entryIndex < 0 || entryIndex >= entries.Count || entries[entryIndex].MutableVariants.Count == 0)
            return new Item();

        StorageEntry entry = entries[entryIndex];
        Item item = entry.MutableVariants[0].Item.Clone();
        item.stack = (int)Math.Min(entry.Count, Math.Max(1, item.maxStack));
        return item;
    }

    public static void ReceiveSnapshot(BinaryReader reader) => ReadSnapshot(reader);

    private static StorageEntry FindEntry(int type, int prefix)
    {
        foreach (StorageEntry entry in entries)
        {
            if (entry.Type == type && entry.Prefix == prefix)
                return entry;
        }

        return null;
    }

    private static bool MatchesIngredient(int requiredType, int storedType)
    {
        if (requiredType == storedType)
            return true;

        int groupId = requiredType - 1_000_000;
        return groupId >= 0 && RecipeGroup.recipeGroups.TryGetValue(groupId, out RecipeGroup group) && group.ContainsItem(storedType);
    }

    private static void BroadcastSnapshot()
    {
        if (Main.netMode != NetmodeID.Server)
            return;

        ModPacket packet = ModContent.GetInstance<EasyStorage>().GetPacket();
        packet.Write(EasyStorage.StorageSnapshotPacket);
        WriteSnapshot(packet);
        packet.Send();
    }

    private static void WriteSnapshot(BinaryWriter writer)
    {
        TagCompound snapshot = [];
        new StorageSystem().SaveWorldData(snapshot);
        using MemoryStream stream = new();
        TagIO.ToStream(snapshot, stream);
        writer.Write((int)stream.Length);
        writer.Write(stream.GetBuffer(), 0, (int)stream.Length);
    }

    private static void ReadSnapshot(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > MaximumSnapshotSize)
            throw new IOException($"Invalid Easy Storage snapshot size: {length}.");

        byte[] data = reader.ReadBytes(length);
        if (data.Length != length)
            throw new EndOfStreamException("Incomplete Easy Storage snapshot.");

        using MemoryStream stream = new(data, writable: false);
        TagCompound snapshot = TagIO.FromStream(stream);
        new StorageSystem().LoadWorldData(snapshot);
    }
}

public sealed class StorageEntry
{
    internal StorageEntry(int type, int prefix)
    {
        Type = type;
        Prefix = prefix;
    }

    public int Type { get; }

    public int Prefix { get; }

    public IReadOnlyList<StoredVariant> Variants => variants;

    internal List<StoredVariant> MutableVariants => variants;

    public long Count
    {
        get
        {
            long count = 0;
            foreach (StoredVariant variant in variants)
                count = long.MaxValue - count < variant.Count ? long.MaxValue : count + variant.Count;
            return count;
        }
    }

    internal List<StoredVariant> variants { get; } = [];
}

public sealed class StoredVariant
{
    internal StoredVariant(Item item, long count)
    {
        Item = item;
        Count = count;
    }

    public Item Item { get; }
    public long Count { get; internal set; }
}