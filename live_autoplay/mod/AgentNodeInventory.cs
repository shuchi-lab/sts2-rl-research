using System;
using System.Collections.Generic;

namespace STS2_MCP;

// Turbo-only preorder inventory of one scene root. A single walk records every
// live node (visible or not) with its parent slot and subtree end, so the many
// whole-tree FindAll/FTUE searches in one state build share one GetChildren
// walk instead of repeating it. Only node identity and order are reused:
// liveness and the parent link of each match and its ancestors are re-read per
// query, and visibility/text/buttons stay with the caller. The snapshot is
// rebuilt on a new root, a new process frame, or any Invalidate (wired to
// SceneTree tree_changed/node_added/node_removed), so same-frame adds, removes,
// reparents and moves are seen exactly as a fresh traversal would see them.
internal sealed class AgentNodeInventory<TNode> where TNode : class
{
    private readonly Func<TNode, IReadOnlyList<TNode>> _children;
    private readonly Func<TNode, bool> _live;
    private readonly Func<TNode, TNode?> _parent;
    private readonly List<TNode> _nodes = new();
    private readonly List<int> _up = new(), _end = new();
    private readonly Dictionary<TNode, int> _index = new(ReferenceEqualityComparer.Instance);
    private int[] _stamp = Array.Empty<int>();
    private bool[] _reachable = Array.Empty<bool>();
    private TNode? _root;
    private long _frame = -1, _builtVersion = -1;
    private int _query;

    public AgentNodeInventory(Func<TNode, IReadOnlyList<TNode>> children, Func<TNode, bool> live,
        Func<TNode, TNode?> parent)
    {
        _children = children;
        _live = live;
        _parent = parent;
    }

    public long Version { get; private set; }
    public long Builds { get; private set; }
    public long Queries { get; private set; }

    public void Invalidate() => Version++;

    // Visits, in fresh-traversal preorder, each node under start that passes the
    // cheap managed `match` and is reachable from start through live nodes;
    // `visit` returns false to stop. Returns false without visiting anything
    // when start is not in root's tree, so the caller falls back to its walk.
    public bool TryVisit(TNode root, long frame, TNode start, Func<TNode, bool> match, Func<TNode, bool> visit)
    {
        if (!ReferenceEquals(root, _root) || frame != _frame || Version != _builtVersion)
            Build(root, frame);
        if (!_index.TryGetValue(start, out int s))
            return false;
        Queries++;
        _query++;
        for (int j = s; j < _end[s]; j++)
        {
            if (match(_nodes[j]) && Reachable(j, s) && !visit(_nodes[j]))
                break;
        }
        return true;
    }

    private void Build(TNode root, long frame)
    {
        long version = Version;
        _nodes.Clear();
        _up.Clear();
        _end.Clear();
        _index.Clear();
        // Fresh traversal never yields or descends a non-live node, and
        // queue_free cannot be undone, so pruning here matches it.
        if (_live(root))
            Add(root, -1);
        if (_stamp.Length < _nodes.Count)
        {
            _stamp = new int[_nodes.Count * 2];
            _reachable = new bool[_nodes.Count * 2];
            _query = 0;
        }
        _root = root;
        _frame = frame;
        _builtVersion = version;
        Builds++;
    }

    private void Add(TNode node, int up)
    {
        int i = _nodes.Count;
        _nodes.Add(node);
        _up.Add(up);
        _end.Add(0);
        _index[node] = i;
        IReadOnlyList<TNode> children;
        try
        {
            children = _children(node);
        }
        catch (ObjectDisposedException)
        {
            children = Array.Empty<TNode>();
        }
        foreach (var child in children)
        {
            if (_live(child))
                Add(child, i);
        }
        _end[i] = _nodes.Count;
    }

    // Live now, and still linked to the recorded parent all the way up to start.
    private bool Reachable(int j, int s)
    {
        if (_stamp[j] == _query)
            return _reachable[j];
        bool ok;
        try
        {
            ok = _live(_nodes[j]) &&
                 (j == s || (ReferenceEquals(_parent(_nodes[j]), _nodes[_up[j]]) && Reachable(_up[j], s)));
        }
        catch (ObjectDisposedException)
        {
            ok = false;
        }
        _stamp[j] = _query;
        _reachable[j] = ok;
        return ok;
    }
}
