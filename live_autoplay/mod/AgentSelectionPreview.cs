using System;
using System.Collections.Generic;
using System.Linq;

namespace STS2_MCP;

// Every native NCardGridSelectionScreen preview overlay (v0.111.0 sources). Upgrade and enchant screens
// open their preview automatically when MaxSelect is reached; on NDeckEnchantSelectScreen the main
// "Confirm" button is PreviewSelection, so only the open preview's own Confirm completes the selection.
internal static class AgentSelectionPreview
{
    internal static readonly string[] Containers =
    {
        "%UpgradeSinglePreviewContainer", "%UpgradeMultiPreviewContainer",
        "%EnchantSinglePreviewContainer", "%EnchantMultiPreviewContainer",
        "%PreviewContainer", // NDeckTransformSelectScreen (Confirm) and NDeckCardSelectScreen (%PreviewConfirm)
    };

    private static readonly string[] ConfirmButtons = { "Confirm", "%PreviewConfirm" };

    internal static string? OpenContainer<TNode>(Func<string, TNode?> find, Func<TNode, bool> visible) where TNode : class
        => Containers.FirstOrDefault(name => find(name) is { } node && visible(node));

    // Null when no preview is open: the caller keeps the screen's own native confirm handler.
    internal static Dictionary<string, object?>? ConfirmOpen<TNode, TButton>(Func<string, TNode?> find, Func<TNode, bool> visible,
        Func<TNode, string, TButton?> button, Func<TButton, bool> enabled, Action<TButton> click)
        where TNode : class where TButton : class
    {
        if (OpenContainer(find, visible) is not { } name) return null;
        var node = find(name)!;
        var confirm = ConfirmButtons.Select(b => button(node, b)).FirstOrDefault(b => b != null);
        if (confirm == null || !enabled(confirm))
            return new() { ["status"] = "error", ["message"] = "error:preview_confirm_disabled" };
        click(confirm);
        return new() { ["status"] = "ok", ["message"] = "Confirming selection from preview" };
    }
}
