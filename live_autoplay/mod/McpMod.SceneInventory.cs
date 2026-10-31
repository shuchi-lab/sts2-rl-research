using System;
using System.Collections.Generic;
using Godot;

namespace STS2_MCP;

// Turbo wiring for AgentNodeInventory over the live SceneTree root; see
// AgentNodeInventory.cs. Normal mode, other threads and nodes outside the
// root's tree keep the original recursive walks.
public static partial class McpMod
{
    private static readonly AgentNodeInventory<Node> _sceneInventory =
        new(SceneInventoryChildren, IsLiveNode, SceneInventoryParent);
    private static int _sceneInventoryThread = -1;

    private static void InitializeSceneInventory(SceneTree tree)
    {
        _sceneInventoryThread = System.Environment.CurrentManagedThreadId;
        // tree_changed covers add, remove and move_child; the node signals are
        // belt and braces for the same hierarchy edits.
        tree.Connect(SceneTree.SignalName.TreeChanged, Callable.From(_sceneInventory.Invalidate));
        tree.Connect(SceneTree.SignalName.NodeAdded, Callable.From<Node>(_ => _sceneInventory.Invalidate()));
        tree.Connect(SceneTree.SignalName.NodeRemoved, Callable.From<Node>(_ => _sceneInventory.Invalidate()));
    }

    private static bool TryVisitSceneInventory(Node start, Func<Node, bool> match, Func<Node, bool> visit)
    {
        if (!_agentTurbo.Enabled || System.Environment.CurrentManagedThreadId != _sceneInventoryThread)
            return false;
        if (Engine.GetMainLoop() is not SceneTree { Root: { } root })
            return false;
        return _sceneInventory.TryVisit(root, (long)Engine.GetProcessFrames(), start, match, visit);
    }

    private static IReadOnlyList<Node> SceneInventoryChildren(Node node)
    {
        var children = node.GetChildren();
        // Same owned-container disposal as FindAllRecursive; the Nodes are borrowed.
        using var ownedChildren = (Godot.Collections.Array)children;
        var list = new List<Node>(children.Count);
        foreach (var child in children)
            list.Add(child);
        return list;
    }

    private static Node? SceneInventoryParent(Node node) => node.GetParent();
}
