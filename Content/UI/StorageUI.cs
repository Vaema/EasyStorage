using EasyStorage.Core.Systems;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System;
using System.Collections.Generic;
using Terraria;
using Terraria.GameContent;
using Terraria.GameInput;
using Terraria.ModLoader;
using Terraria.UI;

namespace EasyStorage.Content.UI;

public sealed class StorageUI : ModSystem
{
    private const int SlotsPerPage = 100;
    private const float InitialUiScale = 1.2f;
    private static bool isVisible;
    private static bool showingCrafting;
    private static bool searchFocused;
    private static bool panelPositionInitialized;
    private static bool draggingPanel;
    private static bool resizingPanel;
    private static bool previousInventoryVisibility;
    private static string searchText = string.Empty;
    private static float uiScale = InitialUiScale;
    private static Vector2 panelPosition;
    private static Vector2 panelDragOffset;
    private static Vector2 resizeStartMouse;
    private static float resizeStartScale;
    private static int firstVisibleIndex;
    private static int selectedIndex;
    private static bool previousUp;
    private static bool previousDown;
    private static bool previousLeft;
    private static bool previousRight;
    private static bool previousUse;

    public static void Show()
    {
        if (Main.dedServ)
            return;

        if (!isVisible)
            previousInventoryVisibility = Main.playerInventory;

        isVisible = true;
        searchFocused = false;
        PlayerInput.WritingText = false;
        Main.playerInventory = true;
    }

    public static void Hide()
    {
        isVisible = false;
        searchFocused = false;
        showingCrafting = false;
        searchText = string.Empty;
        firstVisibleIndex = 0;
        draggingPanel = false;
        resizingPanel = false;
        PlayerInput.WritingText = false;
        if (!Main.dedServ)
        {
            Main.instance.HandleIME();
            Main.playerInventory = previousInventoryVisibility;
        }
        Main.blockInput = false;
    }

    public override void ModifyInterfaceLayers(List<GameInterfaceLayer> layers)
    {
        int mouseTextIndex = layers.FindIndex(layer => layer.Name == "Vanilla: Mouse Text");
        if (mouseTextIndex >= 0)
            layers.Insert(mouseTextIndex, new LegacyGameInterfaceLayer("EasyStorage: Storage UI", DrawInterface, InterfaceScaleType.UI));
    }

    public override void UpdateUI(GameTime gameTime)
    {
        if (!isVisible || Main.gameMenu)
            return;

        Main.blockInput = true;
        Main.playerInventory = true;
        Main.LocalPlayer.mouseInterface = true;
        HandleSearchMouseInput();
        PlayerInput.WritingText = searchFocused;
        Main.instance.HandleIME();
        if (searchFocused)
            searchText = Main.GetInputText(searchText);

        if (Main.keyState.IsKeyDown(Keys.Escape))
        {
            if (searchFocused)
            {
                searchFocused = false;
                PlayerInput.WritingText = false;
            }
            else
                Hide();

            return;
        }

        if (TryDepositShiftClickedItem())
            return;

        NavigateWithController();
    }

    public override void OnWorldUnload() => Hide();

    private static bool DrawInterface()
    {
        if (!isVisible || Main.gameMenu)
            return true;

        DrawPanel(Main.spriteBatch);
        return true;
    }

    private static Rectangle GetPanelBounds()
    {
        uiScale = Math.Min(uiScale, GetMaximumUiScale());
        float slotSize = 52f * Main.inventoryScale * uiScale;
        int padding = (int)(12 * uiScale);
        int panelWidth = (int)(slotSize * 10) + padding * 2;
        int panelHeight = (int)(slotSize * 10) + (int)(112 * uiScale);
        if (!panelPositionInitialized)
        {
            panelPosition = new Vector2((Main.screenWidth - panelWidth) / 2, (Main.screenHeight - panelHeight) / 2);
            panelPositionInitialized = true;
        }

        panelPosition = ClampPanelPosition(panelPosition, panelWidth, panelHeight);
        return new Rectangle((int)panelPosition.X, (int)panelPosition.Y, panelWidth, panelHeight);
    }

    private static Rectangle GetSearchBounds(Rectangle panel)
    {
        int padding = (int)(12 * uiScale);
        int searchWidth = Math.Min((int)(170 * uiScale), Math.Max((int)(80 * uiScale), panel.Width - padding * 2 - (int)(130 * uiScale)));
        return new Rectangle(panel.Right - padding - searchWidth, panel.Y + (int)(8 * uiScale), searchWidth, (int)(28 * uiScale));
    }

    private static void HandleSearchMouseInput()
    {
        if (!Main.mouseLeft || !Main.mouseLeftRelease)
            return;

        Rectangle panel = GetPanelBounds();
        if (GetSearchBounds(panel).Contains(Main.mouseX, Main.mouseY))
        {
            if (!searchFocused)
                Main.clrInput();

            searchFocused = true;
            Main.mouseLeftRelease = false;
        }
        else if (searchFocused && !panel.Contains(Main.mouseX, Main.mouseY))
            searchFocused = false;
    }

    private static void DrawPanel(SpriteBatch spriteBatch)
    {
        Rectangle panel = GetPanelBounds();
        float slotSize = 52f * Main.inventoryScale * uiScale;
        int padding = (int)(12 * uiScale);
        int panelX = panel.X;
        int panelY = panel.Y;
        string heading = showingCrafting ? "Crafting" : "Easy Storage";
        Rectangle search = GetSearchBounds(panel);
        bool panelMouseCaptured = HandlePanelMouse(panel, search);
        DrawRectangle(spriteBatch, panel, new Color(24, 34, 48, 238));
        DrawOutline(spriteBatch, panel, new Color(132, 164, 186));
        DrawResizeHandle(spriteBatch, panel);

        DrawString(spriteBatch, heading, panelX + padding, panelY + (int)(9 * uiScale), Color.White);

        DrawRectangle(spriteBatch, search, new Color(13, 19, 29, 255));
        DrawOutline(spriteBatch, search, searchFocused ? Color.LightBlue : Color.Gray);
        string displayedSearch = searchText.Length == 0 ? "Search items..." : searchText;
        bool hasSearchResults = showingCrafting ? GetFilteredRecipes().Count > 0 : GetFilteredEntries().Count > 0;
        Color searchColor = searchText.Length > 0 && !hasSearchResults ? Color.Red : searchText.Length == 0 ? Color.Gray : Color.White;
        DrawString(spriteBatch, displayedSearch, search.X + (int)(6 * uiScale), search.Y + (int)(5 * uiScale), searchColor);

        List<int> visibleItems = showingCrafting ? GetFilteredRecipes() : GetFilteredEntries();
        int maxFirstIndex = Math.Max(0, visibleItems.Count - SlotsPerPage);
        firstVisibleIndex = Math.Clamp(firstVisibleIndex, 0, maxFirstIndex);
        int gridX = panelX + padding;
        int gridY = panelY + (int)(48 * uiScale);
        Rectangle grid = new(gridX, gridY, (int)(slotSize * 10), (int)(slotSize * 10));
        if (grid.Contains(Main.mouseX, Main.mouseY) && PlayerInput.ScrollWheelDeltaForUI != 0)
        {
            firstVisibleIndex = Math.Clamp(firstVisibleIndex - Math.Sign(PlayerInput.ScrollWheelDeltaForUI) * 10, 0, maxFirstIndex);
            PlayerInput.ScrollWheelDeltaForUI = 0;
        }

        for (int cell = 0; cell < SlotsPerPage; cell++)
        {
            int column = cell % 10;
            int row = cell / 10;
            int x = gridX + (int)(column * slotSize);
            int y = gridY + (int)(row * slotSize);
            Rectangle slotBounds = new(x, y, (int)slotSize, (int)slotSize);
            int filteredIndex = firstVisibleIndex + cell;
            bool focusedSlot = slotBounds.Contains(Main.mouseX, Main.mouseY) || selectedIndex == cell && Main.gamePad;
            bool canCraft = !showingCrafting || filteredIndex >= visibleItems.Count || !focusedSlot || StorageSystem.CanCraftRecipe(visibleItems[filteredIndex]);
            Item item = filteredIndex < visibleItems.Count
                ? showingCrafting ? Main.recipe[visibleItems[filteredIndex]].createItem.Clone() : StorageSystem.CreateDisplayItem(visibleItems[filteredIndex])
                : new Item();
            long displayCount = item.stack;
            if (!showingCrafting && filteredIndex < visibleItems.Count)
            {
                int entryIndex = visibleItems[filteredIndex];
                if (entryIndex >= 0 && entryIndex < StorageSystem.Entries.Count)
                    displayCount = StorageSystem.Entries[entryIndex].Count;
            }

            if (!panelMouseCaptured && slotBounds.Contains(Main.mouseX, Main.mouseY))
            {
                selectedIndex = cell;

                if (!item.IsAir)
                    ItemSlot.MouseHover(ref item, ItemSlot.Context.InventoryItem);

                if (!showingCrafting && !Main.mouseItem.IsAir && Main.mouseLeft && Main.mouseLeftRelease)
                {
                    Main.mouseLeftRelease = false;
                    StorageSystem.RequestDepositMouseItem(Main.mouseItem);
                }
                else if (filteredIndex < visibleItems.Count && canCraft && Main.mouseLeft && Main.mouseLeftRelease)
                {
                    Main.mouseLeftRelease = false;
                    HandleItemClick(visibleItems[filteredIndex], item);
                }
                else if (filteredIndex < visibleItems.Count && Main.mouseRight && Main.mouseRightRelease && !showingCrafting)
                {
                    Main.mouseRightRelease = false;
                    StorageSystem.RequestWithdraw(visibleItems[filteredIndex], 1);
                }
            }

            float inventoryScale = Main.inventoryScale;
            Main.inventoryScale *= uiScale;
            ItemSlot.Draw(spriteBatch, ref item, ItemSlot.Context.InventoryItem, new Vector2(x, y), Color.White);
            Main.inventoryScale = inventoryScale;
            if (filteredIndex < visibleItems.Count && !item.IsAir)
                DrawItemCount(spriteBatch, displayCount, slotBounds);
            if (showingCrafting && focusedSlot && filteredIndex < visibleItems.Count && !canCraft)
                DrawOutline(spriteBatch, slotBounds, Color.Gray);
            if (selectedIndex == cell && Main.gamePad)
                DrawOutline(spriteBatch, slotBounds, Color.Yellow);
        }

        int footerY = grid.Bottom + (int)(10 * uiScale);
        int buttonHeight = (int)(30 * uiScale);
        int closeWidth = (int)(80 * uiScale);
        int modeWidth = (int)(112 * uiScale);
        int buttonGap = (int)(8 * uiScale);
        Rectangle closeButton = new(panel.Right - padding - closeWidth, footerY, closeWidth, buttonHeight);
        Rectangle modeButton = new(closeButton.X - buttonGap - modeWidth, footerY, modeWidth, buttonHeight);
        DrawButton(spriteBatch, modeButton, showingCrafting ? "Storage" : "Crafting");
        DrawButton(spriteBatch, closeButton, "Close");
        string statusText = showingCrafting
            ? $"Recipes: {visibleItems.Count}"
            : $"Items: {visibleItems.Count} Capacity: {StorageSystem.Capacity}";
        DrawString(spriteBatch, statusText, panelX + padding, footerY + (int)(34 * uiScale), Color.LightGray);

        if (!panelMouseCaptured && modeButton.Contains(Main.mouseX, Main.mouseY) && Main.mouseLeft && Main.mouseLeftRelease)
        {
            Main.mouseLeftRelease = false;
            showingCrafting = !showingCrafting;
            firstVisibleIndex = 0;
        }
        if (!panelMouseCaptured && closeButton.Contains(Main.mouseX, Main.mouseY) && Main.mouseLeft && Main.mouseLeftRelease)
        {
            Main.mouseLeftRelease = false;
            Hide();
        }
    }

    private static bool HandlePanelMouse(Rectangle panel, Rectangle search)
    {
        Rectangle header = new(panel.X, panel.Y, Math.Max(0, search.X - panel.X - (int)(8 * uiScale)), (int)(40 * uiScale));
        int handleSize = (int)(24 * uiScale);
        Rectangle resizeHandle = new(panel.Right - handleSize, panel.Bottom - handleSize, handleSize, handleSize);

        if (resizingPanel)
        {
            if (!Main.mouseLeft)
                resizingPanel = false;
            else
                uiScale = Math.Clamp(resizeStartScale + (Main.mouseX - resizeStartMouse.X) / Math.Max(1f, 520f * Main.inventoryScale), GetMinimumUiScale(), GetMaximumUiScale());
        }
        else if (draggingPanel)
        {
            if (!Main.mouseLeft)
                draggingPanel = false;
            else
                panelPosition = new Vector2(Main.mouseX, Main.mouseY) - panelDragOffset;
        }
        else if (Main.mouseLeft && Main.mouseLeftRelease)
        {
            if (resizeHandle.Contains(Main.mouseX, Main.mouseY))
            {
                resizingPanel = true;
                resizeStartMouse = new Vector2(Main.mouseX, Main.mouseY);
                resizeStartScale = uiScale;
                Main.mouseLeftRelease = false;
            }
            else if (header.Contains(Main.mouseX, Main.mouseY))
            {
                draggingPanel = true;
                panelDragOffset = new Vector2(Main.mouseX, Main.mouseY) - panelPosition;
                Main.mouseLeftRelease = false;
            }
        }

        panelPosition = ClampPanelPosition(panelPosition, panel.Width, panel.Height);
        bool isManipulatingPanel = draggingPanel || resizingPanel;
        return isManipulatingPanel || header.Contains(Main.mouseX, Main.mouseY) || resizeHandle.Contains(Main.mouseX, Main.mouseY);
    }

    private static bool TryDepositShiftClickedItem()
    {
        bool shiftHeld = Main.keyState.IsKeyDown(Keys.LeftShift) || Main.keyState.IsKeyDown(Keys.RightShift);
        if (!shiftHeld || !Main.mouseLeft || !Main.mouseLeftRelease || Main.mouseItem is not null && !Main.mouseItem.IsAir || IsMouseOverPanel())
            return false;

        Item hoveredItem = Main.HoverItem;
        if (hoveredItem is null || hoveredItem.IsAir)
            return false;

        Player player = Main.LocalPlayer;
        for (int slot = 0; slot < player.inventory.Length; slot++)
        {
            Item inventoryItem = player.inventory[slot];
            if (inventoryItem.IsAir || inventoryItem.type != hoveredItem.type || inventoryItem.prefix != hoveredItem.prefix || inventoryItem.stack != hoveredItem.stack)
                continue;

            StorageSystem.RequestDeposit(slot);
            Main.mouseLeftRelease = false;
            return true;
        }

        return false;
    }

    private static bool IsMouseOverPanel()
    {
        if (!panelPositionInitialized)
            return false;

        return GetPanelBounds().Contains(Main.mouseX, Main.mouseY);
    }

    private static Vector2 ClampPanelPosition(Vector2 position, int panelWidth, int panelHeight) =>
        new(Math.Clamp(position.X, 0, Math.Max(0, Main.screenWidth - panelWidth)), Math.Clamp(position.Y, 0, Math.Max(0, Main.screenHeight - panelHeight)));

    private static float GetMinimumUiScale() => Math.Min(0.8f, GetMaximumUiScale());

    private static float GetMaximumUiScale()
    {
        float baseWidth = 520f * Main.inventoryScale + 24f;
        float baseHeight = 520f * Main.inventoryScale + 112f;
        float screenScale = Math.Min(Main.screenWidth / baseWidth, Main.screenHeight / baseHeight);
        return Math.Max(0.6f, Math.Min(1.8f, screenScale));
    }

    private static void DrawResizeHandle(SpriteBatch spriteBatch, Rectangle panel)
    {
        int size = (int)(17 * uiScale);
        int x = panel.Right - size;
        int y = panel.Bottom - size;
        Color color = new(150, 178, 196);
        int line = Math.Max(1, (int)(2 * uiScale));
        DrawRectangle(spriteBatch, new Rectangle(x + (int)(9 * uiScale), y + line, line, (int)(10 * uiScale)), color);
        DrawRectangle(spriteBatch, new Rectangle(x + (int)(5 * uiScale), y + (int)(6 * uiScale), line, (int)(6 * uiScale)), color);
        DrawRectangle(spriteBatch, new Rectangle(x + line, y + (int)(10 * uiScale), line, line), color);
    }

    private static void HandleItemClick(int index, Item item)
    {
        if (showingCrafting)
        {
            if (StorageSystem.CanCraftRecipe(index))
                StorageSystem.RequestCraft(index);
        }
        else
            StorageSystem.RequestWithdraw(index, Math.Max(1, item.maxStack));
    }

    private static List<int> GetFilteredEntries()
    {
        List<int> result = [];
        for (int i = 0; i < StorageSystem.Entries.Count; i++)
        {
            Item item = StorageSystem.CreateDisplayItem(i);
            if (MatchesSearch(item.Name))
                result.Add(i);
        }
        return result;
    }

    private static List<int> GetFilteredRecipes()
    {
        List<int> result = [];
        for (int i = 0; i < Main.recipe.Length; i++)
        {
            Recipe recipe = Main.recipe[i];
            if (recipe is not null && !recipe.Disabled && !recipe.createItem.IsAir && MatchesSearch(recipe.createItem.Name))
                result.Add(i);
        }
        return result;
    }

    private static bool MatchesSearch(string value) => searchText.Length == 0 || value.Contains(searchText, StringComparison.CurrentCultureIgnoreCase);

    private static void NavigateWithController()
    {
        Player player = Main.LocalPlayer;
        bool up = player.controlUp;
        bool down = player.controlDown;
        bool left = player.controlLeft;
        bool right = player.controlRight;
        bool use = player.controlUseItem;
        List<int> visible = showingCrafting ? GetFilteredRecipes() : GetFilteredEntries();
        int maxFirstIndex = Math.Max(0, visible.Count - SlotsPerPage);

        if (up && !previousUp)
        {
            if (selectedIndex < 10 && firstVisibleIndex > 0)
                firstVisibleIndex = Math.Max(0, firstVisibleIndex - 10);
            else if (selectedIndex >= 10)
                selectedIndex -= 10;
        }
        if (down && !previousDown)
        {
            if (selectedIndex >= 90 && firstVisibleIndex < maxFirstIndex)
                firstVisibleIndex = Math.Min(maxFirstIndex, firstVisibleIndex + 10);
            else if (selectedIndex < 90)
                selectedIndex += 10;
        }

        if (left && !previousLeft)
            selectedIndex = (selectedIndex + 99) % 100;
        if (right && !previousRight)
            selectedIndex = (selectedIndex + 1) % 100;

        if (use && !previousUse)
        {
            int index = firstVisibleIndex + selectedIndex;
            if (index < visible.Count)
            {
                Item item = showingCrafting ? Main.recipe[visible[index]].createItem : StorageSystem.CreateDisplayItem(visible[index]);
                HandleItemClick(visible[index], item);
            }
        }

        previousUp = up;
        previousDown = down;
        previousLeft = left;
        previousRight = right;
        previousUse = use;
        player.controlUp = false;
        player.controlDown = false;
        player.controlLeft = false;
        player.controlRight = false;
        player.controlUseItem = false;
    }

    private static void DrawButton(SpriteBatch spriteBatch, Rectangle rectangle, string text)
    {
        DrawRectangle(spriteBatch, rectangle, new Color(49, 69, 87, 255));
        DrawOutline(spriteBatch, rectangle, new Color(150, 178, 196));
        float width = text.Length * 8f * uiScale;
        DrawString(spriteBatch, text, rectangle.X + (rectangle.Width - width) / 2, rectangle.Y + (int)(7 * uiScale), Color.White);
    }

    private static void DrawItemCount(SpriteBatch spriteBatch, long count, Rectangle slot)
    {
        string text = count.ToString();
        int textWidth = (int)(text.Length * 8 * uiScale);
        Vector2 position = new(slot.Right - textWidth - (int)(5 * uiScale), slot.Bottom - (int)(18 * uiScale));
        Utils.DrawBorderString(spriteBatch, text, position + Vector2.One, Color.Black, 0.8f * uiScale, 0f, 0f, text.Length);
        Utils.DrawBorderString(spriteBatch, text, position, Color.White, 0.8f * uiScale, 0f, 0f, text.Length);
    }

    private static void DrawString(SpriteBatch spriteBatch, string text, float x, float y, Color color) =>
        Utils.DrawBorderString(spriteBatch, text, new Vector2(x, y), color, uiScale, 0f, 0f, text.Length);

    private static void DrawRectangle(SpriteBatch spriteBatch, Rectangle rectangle, Color color) =>
        spriteBatch.Draw(TextureAssets.MagicPixel.Value, rectangle, color);

    private static void DrawOutline(SpriteBatch spriteBatch, Rectangle rectangle, Color color)
    {
        DrawRectangle(spriteBatch, new Rectangle(rectangle.X, rectangle.Y, rectangle.Width, 1), color);
        DrawRectangle(spriteBatch, new Rectangle(rectangle.X, rectangle.Bottom - 1, rectangle.Width, 1), color);
        DrawRectangle(spriteBatch, new Rectangle(rectangle.X, rectangle.Y, 1, rectangle.Height), color);
        DrawRectangle(spriteBatch, new Rectangle(rectangle.Right - 1, rectangle.Y, 1, rectangle.Height), color);
    }
}
