using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rewards;

namespace STS2_MCP;

public static partial class McpMod
{
    private static CanvasLayer? _agentLayer;
    private static PanelContainer? _agentPanel;
    private static Label? _agentHeading, _agentStatus, _agentRanking;
    private static Label? _agentDelaySeconds;
    private static Label? _agentTextSizeLabel;
    private static HSlider? _agentDelaySlider;
    private static ScrollContainer? _agentRankingScroll;
    private static int _agentTextSize = 32;
    private const float AgentControlPanelScale = .6f;
    private static bool _agentPanelOnRight;
    private static Button? _agentOpenService;
    private static AgentServiceLauncher? _agentServiceLauncher;
    private static string _agentServiceLaunchMessage = "";
    private sealed class AgentRatingBadge
    {
        internal required PanelContainer Panel;
        internal required Label Preference;
        internal required Label Q;
        internal required StyleBoxFlat Background;
    }
    private static readonly Dictionary<ulong, AgentRatingBadge> _agentBadges = new();

    private static void CreateAgentOverlay()
    {
        _agentLayer = new CanvasLayer { Name = "STS2AgentOverlay", Layer = 1001 };
        _agentPanel = new PanelContainer { Position = new Vector2(16, 128), CustomMinimumSize = new Vector2(620, 0) };
        var background = new StyleBoxFlat { BgColor = new Color(.025f, .04f, .06f, .91f), CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8, CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            ContentMarginLeft = 14, ContentMarginRight = 14, ContentMarginTop = 12, ContentMarginBottom = 12 };
        _agentPanel.AddThemeStyleboxOverride("panel", background);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 6);
        _agentHeading = AgentLabel("Choice + Wide177.5k", 28);
        _agentHeading.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _agentStatus = AgentLabel("Starting", 22);
        _agentStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _agentStatus.MaxLinesVisible = 3;
        _agentRanking = AgentLabel("", _agentTextSize);
        _agentRanking.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _agentRanking.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var headingRow = new HBoxContainer();
        _agentHeading.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _agentOpenService = AgentOverlayButton("Open", 22);
        _agentOpenService.CustomMinimumSize = new Vector2(110, 0);
        _agentOpenService.TooltipText = "Start the frozen Python agents. Ratings load in Advice mode.";
        _agentOpenService.Pressed += OpenAgentService;
        headingRow.AddChild(_agentHeading); headingRow.AddChild(_agentOpenService);
        column.AddChild(headingRow); column.AddChild(_agentStatus);
        var row = new HBoxContainer();
        foreach (var (label, mode) in new[] { ("Advice", "advice"), ("Step", "step"), ("Auto", "auto"), ("Pause", "paused") })
        {
            var button = AgentOverlayButton(label, 24);
            button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            string captured = mode;
            button.Pressed += () => SetAgentMode(captured);
            row.AddChild(button);
        }
        column.AddChild(row);
        var reading = new HBoxContainer();
        var smaller = AgentOverlayButton("A-", 22);
        var larger = AgentOverlayButton("A+", 22);
        _agentTextSizeLabel = AgentLabel("Ratings: " + Math.Clamp(_agentTextSize + 16, 36, 56), 22);
        _agentTextSizeLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        smaller.Pressed += () => SetAgentOverlayTextSize(_agentTextSize - 2);
        larger.Pressed += () => SetAgentOverlayTextSize(_agentTextSize + 2);
        reading.AddChild(smaller); reading.AddChild(larger); reading.AddChild(_agentTextSizeLabel);
        foreach (var (label, right) in new[] { ("Left", false), ("Right", true) })
        {
            var position = AgentOverlayButton(label, 22);
            bool captured = right;
            position.Pressed += () => { _agentPanelOnRight = captured; UpdateAgentOverlayLayout(); };
            reading.AddChild(position);
        }
        column.AddChild(reading);
        var timing = new HBoxContainer();
        timing.AddChild(AgentLabel("Delay", 22));
        var delay = new HSlider { MinValue = .1, MaxValue = 3, Step = .1, Value = _agentDelay,
            CustomMinimumSize = new Vector2(170, 24), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var seconds = AgentLabel(_agentDelay.ToString("0.0", CultureInfo.InvariantCulture) + "s", 22);
        _agentDelaySlider = delay; _agentDelaySeconds = seconds;
        delay.ValueChanged += value => { _agentDelay = value; seconds.Text = value.ToString("0.0", CultureInfo.InvariantCulture) + "s"; };
        timing.AddChild(delay); timing.AddChild(seconds); column.AddChild(timing);
        var explanation = AgentLabel("Preference is relative, not win probability.\nQ is the model's reward score.", 18);
        explanation.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(explanation);
        var listControls = new HBoxContainer();
        var visibility = AgentOverlayButton("Show score list", 22);
        visibility.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        visibility.Pressed += () => { _agentRankingScroll!.Visible = !_agentRankingScroll.Visible;
            visibility.Text = _agentRankingScroll.Visible ? "Hide score list" : "Show score list";
            UpdateAgentOverlayLayout(); };
        listControls.AddChild(visibility); listControls.AddChild(AgentLabel("F6 pauses", 20)); column.AddChild(listControls);
        _agentRankingScroll = new ScrollContainer { Visible = false, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill, ClipContents = true,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, VerticalScrollMode = ScrollContainer.ScrollMode.Auto };
        _agentRankingScroll.AddChild(_agentRanking); column.AddChild(_agentRankingScroll);
        _agentPanel.AddChild(column); _agentLayer.AddChild(_agentPanel);
        ((SceneTree)Engine.GetMainLoop()).Root.AddChild(_agentLayer);
        InitializeAgentServiceLauncher();
        UpdateAgentOverlayLayout();
    }

    private static void InitializeAgentServiceLauncher()
    {
        try
        {
            string directory = Path.GetDirectoryName(typeof(McpMod).Assembly.Location)!;
            var command = AgentServiceCommand.Load(Path.Combine(directory, ConfigFileName), LoadPort());
            _agentServiceLauncher = new AgentServiceLauncher(command, () => Time.GetTicksMsec() / 1000.0);
            if (command.AutoStart) OpenAgentService();
        }
        catch (Exception error)
        {
            _agentOpenService!.Disabled = true;
            _agentOpenService.TooltipText = error.GetBaseException().Message;
            _agentMessage = "Agent startup is not configured. Reinstall the autoplay mod.";
        }
    }

    private static void OpenAgentService()
    {
        if (_agentServiceLauncher == null) return;
        _agentServiceLauncher.Open(AgentServiceConnected());
        if (_agentServiceLauncher.Message.Length > 0) _agentMessage = _agentServiceLauncher.Message;
    }

    private static bool AgentServiceConnected() => _agentLastContact > 0
        && Time.GetTicksMsec() / 1000.0 - _agentLastContact < 5;

    private static Button AgentOverlayButton(string text, int size)
    {
        var button = new Button { Text = text, FocusMode = Control.FocusModeEnum.None };
        button.AddThemeFontSizeOverride("font_size", size);
        return button;
    }

    private static void SetAgentOverlayTextSize(int size)
    {
        _agentTextSize = Math.Clamp(size, 20, 40);
        _agentRanking!.AddThemeFontSizeOverride("font_size", _agentTextSize);
        _agentTextSizeLabel!.Text = "Ratings: " + Math.Clamp(_agentTextSize + 16, 36, 56);
        UpdateAgentOverlayLayout();
    }

    private static AgentRatingBadge CreateAgentRatingBadge()
    {
        var background = new StyleBoxFlat { BgColor = new Color(.01f, .018f, .028f, .9f),
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
            ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 2, ContentMarginBottom = 2 };
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        panel.AddThemeStyleboxOverride("panel", background);
        var column = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        column.AddThemeConstantOverride("separation", -2);
        var preference = AgentLabel("", 48);
        var q = AgentLabel("", 28);
        preference.HorizontalAlignment = q.HorizontalAlignment = HorizontalAlignment.Center;
        preference.AddThemeColorOverride("font_outline_color", Colors.Black);
        preference.AddThemeConstantOverride("outline_size", 2);
        column.AddChild(preference); column.AddChild(q); panel.AddChild(column);
        _agentLayer!.AddChild(panel);
        return new() { Panel = panel, Preference = preference, Q = q, Background = background };
    }

    private static void UpdateAgentRatingBadge(AgentRatingBadge badge, AgentCandidate candidate,
        (double Q, double Preference) score, Rect2 anchor)
    {
        bool ancient = candidate.Kind == "ancient_relic";
        bool row = ancient || candidate.Anchor is NEventOptionButton or NRewardButton
            || candidate.Anchor is NRestSiteButton rest && AgentRestBadgeHasStackedRows(rest);
        int primary = ancient ? Math.Clamp(_agentTextSize + 16, 36, 56) : Math.Clamp(_agentTextSize, 28, 36);
        int secondary = ancient ? Math.Clamp(primary - 20, 22, 32) : Math.Clamp(primary - 8, 20, 28);
        badge.Preference.Text = score.Preference.ToString("0.0", CultureInfo.InvariantCulture) + "%";
        badge.Q.Text = "Q " + score.Q.ToString("0.00", CultureInfo.InvariantCulture);
        badge.Panel.CustomMinimumSize = Vector2.Zero;
        void FontSizes()
        {
            badge.Preference.AddThemeFontSizeOverride("font_size", primary);
            badge.Q.AddThemeFontSizeOverride("font_size", secondary);
        }
        FontSizes();
        var viewport = badge.Panel.GetViewportRect();
        if (candidate.Anchor is NHandCardHolder && (anchor.Size.X <= 0 || anchor.Size.Y <= 0))
        { badge.Panel.Visible = false; return; }
        if (!viewport.Intersects(anchor))
        { badge.Panel.Visible = false; return; }
        float margin = 8, gap = 12;
        var size = badge.Panel.GetCombinedMinimumSize();
        Vector2 position;
        if (row)
        {
            // Keep each rating in its own row's vertical band, beside the native
            // button rather than on top of its icon, name or description.
            float height = Math.Max(1, anchor.Size.Y - 6);
            int minPrimary = ancient ? 36 : 28, minSecondary = ancient ? 22 : 20;
            while ((size.Y > height || size.X > 180) && (primary > minPrimary || secondary > minSecondary))
            {
                if (secondary > minSecondary) secondary -= 2;
                else if (primary > minPrimary) primary -= 2;
                FontSizes(); size = badge.Panel.GetCombinedMinimumSize();
            }
            if (size.Y > height)
            { badge.Panel.Visible = false; return; }
            size.X = Math.Max(ancient ? 180 : 140, size.X);
            float left = anchor.Position.X - gap - size.X;
            float right = anchor.End.X + gap;
            if (right + size.X <= viewport.End.X - margin)
                position = new Vector2(right, anchor.Position.Y + (anchor.Size.Y - size.Y) * .5f);
            else if (left >= viewport.Position.X + margin)
                position = new Vector2(left, anchor.Position.Y + (anchor.Size.Y - size.Y) * .5f);
            else
            { badge.Panel.Visible = false; return; }
        }
        else
        {
            // Floating above a card/item leaves its native text and icon clear.
            float y = anchor.Position.Y - size.Y - 4;
            if (y < viewport.Position.Y + margin) y = anchor.End.Y + 4;
            position = new Vector2(anchor.Position.X + (anchor.Size.X - size.X) * .5f, y);
        }
        position.X = Math.Clamp(position.X, viewport.Position.X + margin,
            Math.Max(viewport.Position.X + margin, viewport.End.X - size.X - margin));
        position.Y = Math.Clamp(position.Y, viewport.Position.Y + margin,
            Math.Max(viewport.Position.Y + margin, viewport.End.Y - size.Y - margin));
        badge.Panel.Size = size; badge.Panel.Position = position;
        bool chosen = candidate.Id == _agentChosen;
        badge.Preference.Modulate = chosen ? Colors.LightGreen : Colors.White;
        badge.Q.Modulate = Colors.White;
        badge.Background.BorderColor = chosen ? Colors.LightGreen : new Color(.45f, .5f, .55f, .85f);
        badge.Panel.Visible = true;
    }

    private static bool AgentRestBadgeHasStackedRows(NRestSiteButton anchor)
    {
        var parent = anchor.GetParent();
        if (parent is VBoxContainer) return true;
        var rect = anchor.GetGlobalRect();
        // Rest scenes may arrange icons horizontally. Only certify a vertical
        // row from another native option in the same column and a separate band.
        var children = parent.GetChildren();
        using var ownedChildren = (Godot.Collections.Array)children;
        return children.OfType<NRestSiteButton>().Any(other =>
        {
            if (ReferenceEquals(other, anchor) || !other.IsVisibleInTree()) return false;
            var sibling = other.GetGlobalRect();
            float overlap = Math.Min(rect.End.X, sibling.End.X) - Math.Max(rect.Position.X, sibling.Position.X);
            return overlap >= Math.Min(rect.Size.X, sibling.Size.X) * .5f
                && (rect.End.Y <= sibling.Position.Y || sibling.End.Y <= rect.Position.Y);
        });
    }

    private static Rect2 AgentRatingAnchorBounds(Control anchor)
    {
        if (anchor is NHandCardHolder hand)
        {
            // Native CardContainer and holder roots have zero size. HandIndex
            // is the actual visible 64x64 number label above each fanned card.
            if (GetInstanceFieldValue(hand, "_handIndexLabel") is not Control label
                || !GodotObject.IsInstanceValid(label) || !label.IsVisibleInTree()
                || label.Size.X <= 0 || label.Size.Y <= 0) return default;
            var transform = label.GetGlobalTransformWithCanvas();
            return new Rect2(transform * Vector2.Zero, Vector2.Zero)
                .Expand(transform * new Vector2(label.Size.X, 0))
                .Expand(transform * label.Size)
                .Expand(transform * new Vector2(0, label.Size.Y));
        }
        return anchor.GetGlobalRect();
    }

    private static void UpdateAgentOverlayLayout()
    {
        var viewport = _agentPanel!.GetViewportRect().Size;
        if (viewport.X <= 0 || viewport.Y <= 0) return;
        float margin = Math.Min(16, viewport.X * .02f);
        float width = Math.Min(620, Math.Min(viewport.X * .46f, viewport.X - margin * 2));
        float top = Math.Min(128, viewport.Y * .18f);
        // Leave the lower quarter of the game available for the hand and its controls.
        float availableHeight = Math.Max(1, viewport.Y * .74f - top - margin);
        float height = Math.Min(viewport.Y * .64f, availableHeight);
        float contentWidth = Math.Max(1, width - 28);
        _agentPanel.CustomMinimumSize = new Vector2(width, 0);
        _agentHeading!.CustomMinimumSize = new Vector2(contentWidth, 0);
        _agentStatus!.CustomMinimumSize = new Vector2(contentWidth, 0);
        _agentRanking!.CustomMinimumSize = new Vector2(Math.Max(1, contentWidth - 28), 0);
        _agentPanel.Size = new Vector2(width, _agentRankingScroll!.Visible ? height : 1);
        _agentPanel.Scale = Vector2.One * AgentControlPanelScale;
        float displayedWidth = width * AgentControlPanelScale;
        _agentPanel.Position = new Vector2(_agentPanelOnRight ? Math.Max(margin, viewport.X - displayedWidth - margin) : margin, top);
    }

    private static Label AgentLabel(string text, int size)
    {
        var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", new Color(.92f, .95f, 1));
        return label;
    }

    private static void UpdateAgentOverlay()
    {
        if (_agentLayer == null || !GodotObject.IsInstanceValid(_agentLayer)) CreateAgentOverlay();
        UpdateAgentOverlayLayout();
        _agentDelaySlider!.MaxValue = Math.Max(_agentDelaySlider.MaxValue, _agentDelay);
        _agentDelaySlider.SetValueNoSignal(_agentDelay);
        _agentDelaySeconds!.Text = _agentDelay.ToString("0.##", CultureInfo.InvariantCulture) + "s";
        _agentHeading!.Text = "Choice + Wide177.5k";
        bool connected = AgentServiceConnected();
        if (_agentServiceLauncher != null)
        {
            _agentServiceLauncher.Poll(connected);
            _agentOpenService!.Text = _agentServiceLauncher.Text(connected);
            _agentOpenService.Disabled = !_agentServiceLauncher.CanOpen(connected);
            if (_agentServiceLauncher.Message != _agentServiceLaunchMessage)
            {
                _agentServiceLaunchMessage = _agentServiceLauncher.Message;
                if (_agentServiceLaunchMessage.Length > 0) _agentMessage = _agentServiceLaunchMessage;
            }
        }
        string status = _agentError ?? _agentMessage;
        if (status.Length > 170) status = status[..167] + "…";
        string owner = _agentDecision.Owner switch
        { "battle" => "Combat", "strategy" => "Choices", "automatic" => "Continue", _ => "Waiting" };
        _agentStatus!.Text = (_agentMode.ToUpperInvariant()) + " · " + (connected ? "connected" : "service offline")
            + " · " + owner + "\n" + status;
        _agentStatus.Modulate = _agentError != null ? Colors.OrangeRed : (_agentMode == "auto" ? Colors.LightGreen : Colors.White);
        var allRated = _agentDecision.Candidates.Where(c => _agentScores.ContainsKey(c.Id))
            .OrderByDescending(c => _agentScores[c.Id].Q).ToArray();
        _agentRanking!.Text = string.Join("\n\n", allRated.Select((c, rank) =>
        {
            var s = _agentScores[c.Id];
            return (rank + 1) + ". " + (c.Id == _agentChosen ? "▶ " : "") + c.Label
                + "\n" + s.Preference.ToString("0.0", CultureInfo.InvariantCulture) + "% preference · Q " + s.Q.ToString("0.000", CultureInfo.InvariantCulture);
        }).Concat(_agentDecision.Candidates.Where(c => _agentExcluded.Contains(c.Id))
            .Select(c => c.Label + "\nUnavailable for this policy")));
        if (_agentDecision.Owner == "automatic" && allRated.Length == 0)
            _agentRanking.Text = string.Join("\n\n", _agentDecision.Candidates.Select(c => "Automatic: " + c.Label));

        var visible = new HashSet<ulong>();
        foreach (var group in allRated.Where(c => c.Anchor != null && GodotObject.IsInstanceValid(c.Anchor) && c.Anchor.IsVisibleInTree())
                     .GroupBy(c => c.Anchor!.GetInstanceId()))
        {
            var best = group.First();
            var anchor = best.Anchor!;
            ulong key = anchor.GetInstanceId(); visible.Add(key);
            if (!_agentBadges.TryGetValue(key, out var badge) || !GodotObject.IsInstanceValid(badge.Panel))
            {
                badge = CreateAgentRatingBadge(); _agentBadges[key] = badge;
            }
            var score = _agentScores[best.Id];
            UpdateAgentRatingBadge(badge, best, score, AgentRatingAnchorBounds(anchor));
        }
        foreach (var key in _agentBadges.Keys.ToArray())
            if (!visible.Contains(key))
            {
                var badge = _agentBadges[key];
                if (GodotObject.IsInstanceValid(badge.Panel)) badge.Panel.QueueFree();
                _agentBadges.Remove(key);
            }
    }
}
