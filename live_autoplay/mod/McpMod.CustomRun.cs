using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;

namespace STS2_MCP;

// Custom-run screen (NCustomRunScreen) support: select a character, set the seed,
// set the ascension level, and embark. Mirrors ExecuteCharacterSelectMenuOption but
// targets the custom-run screen, whose Lobby.SetSeed accepts a seed (standard mode
// rejects it). Enables seeded runs for the differential-test harness on v0.107.
public static partial class McpMod
{
    private static Dictionary<string, object?> ExecuteCustomRunMenuOption(
        NCustomRunScreen screen,
        string option,
        string? seed)
    {
        bool Is(string s) => string.Equals(option, s, StringComparison.OrdinalIgnoreCase);

        if (Is("back"))
        {
            var backBtn = GetInstanceFieldValue(screen, "_backButton") as NClickableControl;
            if (backBtn != null && backBtn.IsEnabled)
            {
                backBtn.ForceClick();
                return new Dictionary<string, object?> { ["status"] = "ok", ["message"] = "Going back" };
            }
            return Error("Back button not available");
        }

        // {"action":"menu_select","option":"ascension","seed":"<level>"} — reuse the
        // seed field to carry the ascension level (menu_select has no extra params).
        if (Is("ascension") || Is("set_ascension"))
        {
            var panel = GetInstanceFieldValue(screen, "_ascensionPanel") as NAscensionPanel;
            if (panel == null)
                return Error("Ascension panel not available");
            if (!int.TryParse(seed, out var level))
                return Error("ascension requires the level in the 'seed' field, e.g. {\"option\":\"ascension\",\"seed\":\"20\"}");
            try
            {
                panel.SetAscensionLevel(level);
            }
            catch (Exception ex)
            {
                return Error($"SetAscensionLevel failed: {ex.Message}");
            }
            return new Dictionary<string, object?>
            {
                ["status"] = "ok",
                ["message"] = $"Set ascension to {panel.Ascension}",
            };
        }

        if (Is("confirm") || Is("embark"))
        {
            if (!string.IsNullOrWhiteSpace(seed))
            {
                try
                {
                    screen.Lobby.SetSeed(seed.Trim());
                }
                catch (Exception ex)
                {
                    return Error($"Seeded embark failed before starting the run: {ex.Message}");
                }
            }

            var confirmBtn = GetInstanceFieldValue(screen, "_confirmButton") as NClickableControl;
            if (confirmBtn != null && confirmBtn.IsEnabled)
            {
                var msg = string.IsNullOrEmpty(seed) ? "Embarking on custom run" : $"Embarking on custom run (seed: {seed})";
                confirmBtn.ForceClick();
                return new Dictionary<string, object?> { ["status"] = "ok", ["message"] = msg };
            }
            return Error("Confirm button not available — select a character first");
        }

        // Character selection by id (e.g. IRONCLAD) or title.
        var buttons = FindAll<NCharacterSelectButton>(screen);
        foreach (var btn in buttons)
        {
            if (btn.Character != null && (
                string.Equals(btn.Character.Id.Entry, option, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(SafeGetText(() => btn.Character.Title), option, StringComparison.OrdinalIgnoreCase)))
            {
                if (btn.IsLocked)
                    return Error($"Character '{option}' is locked");
                btn.Select();
                return new Dictionary<string, object?>
                {
                    ["status"] = "ok",
                    ["message"] = $"Selected {SafeGetText(() => btn.Character.Title)}. Use 'confirm' to embark.",
                };
            }
        }
        return Error(
            $"Character '{option}' not found. Available: "
            + string.Join(", ", buttons.Where(b => !b.IsLocked).Select(b => b.Character?.Id.Entry)));
    }
}
