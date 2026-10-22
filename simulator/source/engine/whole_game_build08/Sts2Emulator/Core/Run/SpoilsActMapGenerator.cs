using Sts2Emulator.Core.Rng;

namespace Sts2Emulator.Core.Run;

/// <summary>
/// <c>SpoilsActMap</c>: the hourglass map <c>SpoilsMap.ModifyGeneratedMap</c> hands back for
/// the act the quest card names, in place of the standard map. Every path funnels through
/// one Treasure in the middle column <c>GetRowCount() - 7</c> rows up, then widens again.
/// </summary>
/// <remarks>
/// A port rather than a reuse of <see cref="RunMapGenerator"/>'s standard passes, for the
/// same reason the game keeps it separate: the column limits, the centring priority list
/// and the stray-node redirect have no counterpart there, and the map skips the standard
/// post-processing (CenterGrid, SpreadAdjacentMapPoints, StraightenPaths) entirely.
///
/// The graph is built on <see cref="HashSet{T}"/>s exactly as <c>MapPoint</c> holds its
/// <c>Children</c> and <c>parents</c>. <c>RedirectToTreasure</c> removes a child and then
/// adds another, and a HashSet puts the new entry in the slot the removed one freed; a
/// list appends it. That order is what <c>MapPathPruning.FindAllPaths</c> walks, so it
/// decides which duplicate segment is pruned and how many draws the pruning costs.
/// <c>MapPathPruning</c> is ported alongside for the same reason.
///
/// Everything draws from its own <c>new Rng(seed, "spoils_map")</c>. The standard map the
/// game builds first (<c>ActModel.CreateMap</c>) reads a fresh <c>act_N_map</c> stream and
/// is thrown away, so it leaves no trace on any stream this or the rest of the run reads.
/// </remarks>
internal static class SpoilsActMapGenerator
{
    /// <summary><c>SpoilsMap.AfterCreated</c>: <c>SpoilsActIndex = 1</c>, act two.</summary>
    public const int SpoilsActIndex = 1;

    /// <summary><c>SpoilsMap.CanonicalVars</c>: <c>GoldVar(600)</c>.</summary>
    public const int SpoilsGold = 600;

    private const int MapWidth = 7;
    private const int PathCount = 7;

    // `MapPointType`, whose integer values are what `GenerateSegmentKey` embeds.
    private enum PointType
    {
        Unassigned,
        Unknown,
        Shop,
        Treasure,
        RestSite,
        Monster,
        Elite,
        Boss,
        Ancient,
    }

    private sealed class Point(int col, int row)
    {
        public readonly int Col = col;
        public readonly int Row = row;
        public readonly HashSet<Point> Parents = [];
        public readonly HashSet<Point> Children = [];
        public PointType Type;
        public bool CanBeModified = true;

        public void AddChild(Point child)
        {
            Children.Add(child);
            child.Parents.Add(this);
        }

        public void RemoveChild(Point child)
        {
            Children.Remove(child);
            child.Parents.Remove(this);
        }
    }

    // `MapPoint.CompareTo` is `MapCoord.CompareTo`: `(col, row)` lexicographically.
    private static readonly Comparer<Point> ByCoord = Comparer<Point>.Create(
        (a, b) => (a.Col, a.Row).CompareTo((b.Col, b.Row))
    );

    /// <summary>
    /// <c>SpoilsMap.ModifyGeneratedMap</c>'s condition: the act is the quest's act and a
    /// copy sits in the deck (<c>Pile.Type == PileType.Deck</c>).
    /// </summary>
    public static bool Applies(RunState state) =>
        state.CurrentActIndex == SpoilsActIndex
        && state.Deck.Any(card => card.DefId == RunConstants.SpoilsMapCard);

    /// <summary>
    /// Builds the hourglass into <c>state.MapNodes</c>: start at <c>(3, 0)</c>, boss at
    /// <c>(3, rooms + 1)</c>, point types stamped. Encounter ids, quest marks and map
    /// options are the caller's.
    /// </summary>
    public static void Generate(RunState state)
    {
        var map = new Map(state);
        map.Build();
        map.WriteTo(state);
    }

    private sealed class Map
    {
        private readonly int _mapLength;
        private readonly int _treasureRow;
        private readonly GameRng _rng;
        private readonly int _restCount;
        private readonly int _unknownCount;
        private readonly int _eliteCount;
        private readonly Point?[,] _grid;
        private readonly HashSet<Point> _startMapPoints = [];
        private readonly Point _boss;
        private readonly Point _start;

        public Map(RunState state)
        {
            // `_mapLength = act.GetNumberOfRooms(isMultiplayer) + 1`, solo.
            _mapLength = RunConstants.MapBossRowFor(state.Act);
            _rng = new GameRng(state.Rng.Seed, "spoils_map");
            // `act.GetMapPointTypes(_rng)` -- the act's own counts, drawn off THIS stream.
            (_restCount, _unknownCount) = RunMapGenerator.RollPointTypeCounts(state.Act, _rng);
            _eliteCount = RunConstants.MapEliteCountFor(state.AscensionLevel);
            _grid = new Point?[MapWidth, _mapLength];
            _treasureRow = _mapLength - 7;
            _boss = new Point(MapWidth / 2, _mapLength);
            _start = new Point(MapWidth / 2, 0);
        }

        public void Build()
        {
            GenerateHourglassMap();
            AssignPointTypes();
            PruneAndRepair();
        }

        public void WriteTo(RunState state)
        {
            state.MapNodes = [];
            state.CurrentMapCoord = (_start.Col, _start.Row);
            Array.Clear(state.MapOptionCoords);
            // Every point the grid still holds, plus the start and boss, which it never did.
            var points = AllMapPoints().Append(_start).Append(_boss).ToList();
            foreach (var point in points)
            {
                state.MapNodes[(point.Col, point.Row)] = new RunMapNode(point.Col, point.Row)
                {
                    NodeType = NodeType(point.Type),
                    CanBeModified = point.CanBeModified,
                };
            }

            foreach (var point in points)
            {
                var node = state.MapNodes[(point.Col, point.Row)];
                node.Children.AddRange(point.Children.Select(child => (child.Col, child.Row)));
                node.Parents.AddRange(point.Parents.Select(parent => (parent.Col, parent.Row)));
            }
        }

        private static int NodeType(PointType type) =>
            type switch
            {
                PointType.Unknown => RunConstants.NodeEvent,
                PointType.Shop => RunConstants.NodeShop,
                PointType.Treasure => RunConstants.NodeRelic,
                PointType.RestSite => RunConstants.NodeRest,
                PointType.Monster => RunConstants.NodeNormal,
                PointType.Elite => RunConstants.NodeElite,
                PointType.Boss => RunConstants.NodeBoss,
                PointType.Ancient => RunConstants.NodeAncient,
                _ => RunConstants.NodeNone,
            };

        // ── ActMap ───────────────────────────────────────────────────────────

        private int RowCount => _mapLength;

        // `ActMap.GetAllMapPoints`: column-major over the grid only.
        private IEnumerable<Point> AllMapPoints()
        {
            for (int c = 0; c < MapWidth; c++)
            {
                for (int r = 0; r < RowCount; r++)
                {
                    if (_grid[c, r] is { } point)
                    {
                        yield return point;
                    }
                }
            }
        }

        private IEnumerable<Point> PointsInRow(int row)
        {
            for (int c = 0; c < MapWidth; c++)
            {
                if (_grid[c, row] is { } point)
                {
                    yield return point;
                }
            }
        }

        // `ActMap.GetPoint`: the boss and start answer for their own coordinates first.
        private Point? GetPoint(int col, int row)
        {
            if (col == _boss.Col && row == _boss.Row)
                return _boss;
            if (col == _start.Col && row == _start.Row)
                return _start;
            if (col >= 0 && col < MapWidth && row >= 0 && row < RowCount)
                return _grid[col, row];
            return null;
        }

        // ── SpoilsActMap ─────────────────────────────────────────────────────

        private Point GetOrCreatePoint(int col, int row)
        {
            if (col >= 0 && col < MapWidth && row >= 0 && row < RowCount)
            {
                if (_grid[col, row] is { } existing)
                {
                    return existing;
                }
            }

            var point = new Point(col, row);
            _grid[col, row] = point;
            return point;
        }

        private void GenerateHourglassMap()
        {
            int center = MapWidth / 2;
            if (_treasureRow <= 0 || _treasureRow >= RowCount)
            {
                throw new InvalidOperationException(
                    "Treasure row is out of bounds for SpoilsActMap"
                );
            }

            for (int i = 0; i < PathCount; i++)
            {
                var start = GetOrCreatePoint(_rng.NextInt(0, MapWidth), 1);
                if (i == 1)
                {
                    while (_startMapPoints.Contains(start))
                    {
                        start = GetOrCreatePoint(_rng.NextInt(0, MapWidth), 1);
                    }
                }

                _startMapPoints.Add(start);
                PathGenerate(start);
            }

            var treasure = GetOrCreatePoint(center, _treasureRow);
            treasure.Type = PointType.Treasure;
            treasure.CanBeModified = false;
            foreach (var point in PointsInRow(_treasureRow).ToList())
            {
                if (point != treasure)
                {
                    RedirectToTreasure(point, treasure);
                }
            }

            ConnectRowToBoss();
            ConnectRowToStart();
        }

        private void PathGenerate(Point startingPoint)
        {
            var current = startingPoint;
            while (current.Row < _mapLength - 1)
            {
                var (col, row) = GenerateNextCoord(current);
                var next = GetOrCreatePoint(col, row);
                current.AddChild(next);
                current = next;
            }
        }

        private (int Col, int Row) GenerateNextCoord(Point current)
        {
            int row = current.Row + 1;
            var (minCol, maxCol) = AllowedColumnsForRow(row);
            int center = MapWidth / 2;
            var directions = new List<int> { -1, 0, 1 };
            int rowsToTreasure = _treasureRow - current.Row;
            if (rowsToTreasure > 3)
            {
                _rng.StableShuffle(directions, Comparer<int>.Default);
            }
            else if (rowsToTreasure > 0)
            {
                directions = CenteredPriorityList(current.Col, center);
            }
            else
            {
                _rng.StableShuffle(directions, Comparer<int>.Default);
            }

            foreach (int direction in directions)
            {
                int nextCol = NextColumn(current.Col, direction);
                if (nextCol < minCol || nextCol > maxCol || HasInvalidCrossover(current, nextCol))
                {
                    continue;
                }

                var point = GetPoint(nextCol, row);
                if (
                    (point == null || point.Parents.Contains(current) || point.Parents.Count < 3)
                    && (
                        current == _start
                        || current.Children.Count < 3
                        || (point != null && current.Children.Contains(point))
                    )
                )
                {
                    if (Math.Abs(nextCol - current.Col) > 1)
                    {
                        throw new InvalidOperationException(
                            $"Invalid step from ({current.Col}, {current.Row}) to column {nextCol}"
                        );
                    }

                    return (nextCol, row);
                }
            }

            int fallback = Math.Clamp(center, minCol, maxCol);
            if (Math.Abs(fallback - current.Col) > 1)
            {
                fallback = Math.Clamp(
                    current.Col + Math.Sign(fallback - current.Col),
                    minCol,
                    maxCol
                );
            }

            if (HasInvalidCrossover(current, fallback))
            {
                fallback = Math.Clamp(current.Col, minCol, maxCol);
            }

            if (Math.Abs(fallback - current.Col) > 1)
            {
                throw new InvalidOperationException(
                    $"Fallback step from ({current.Col}, {current.Row}) to column {fallback} exceeds adjacency"
                );
            }

            return (fallback, row);
        }

        private static List<int> CenteredPriorityList(int currentCol, int centerCol)
        {
            var list = new List<int>();
            int toward = Math.Sign(centerCol - currentCol);
            if (toward != 0)
            {
                list.Add(toward);
            }

            list.Add(0);
            if (toward != 0)
            {
                list.Add(-toward);
            }

            if (!list.Contains(-1))
            {
                list.Add(-1);
            }

            if (!list.Contains(1))
            {
                list.Add(1);
            }

            return list;
        }

        private static int NextColumn(int currentCol, int direction) =>
            direction switch
            {
                -1 => Math.Max(0, currentCol - 1),
                0 => currentCol,
                1 => Math.Min(MapWidth - 1, currentCol + 1),
                _ => currentCol,
            };

        private (int MinCol, int MaxCol) AllowedColumnsForRow(int row)
        {
            int center = MapWidth / 2;
            int fromTreasure = Math.Abs(row - _treasureRow);
            int fromTop = _mapLength - 1 - row;
            int topLimit = Math.Min(center, Math.Max(0, fromTop) + 1);
            int halfWidth = Math.Min(center, Math.Min(fromTreasure, topLimit));
            return (Math.Max(0, center - halfWidth), Math.Min(MapWidth - 1, center + halfWidth));
        }

        private bool HasInvalidCrossover(Point current, int targetCol)
        {
            int delta = targetCol - current.Col;
            if (delta == 0)
            {
                return false;
            }

            var sibling = GetPoint(targetCol, current.Row);
            if (sibling == null)
            {
                return false;
            }

            foreach (var child in sibling.Children)
            {
                if (child.Col - sibling.Col == -delta)
                {
                    return true;
                }
            }

            return false;
        }

        private void RedirectToTreasure(Point stray, Point treasure)
        {
            foreach (var parent in stray.Parents.ToList())
            {
                parent.RemoveChild(stray);
                parent.AddChild(treasure);
            }

            foreach (var child in stray.Children.ToList())
            {
                stray.RemoveChild(child);
                treasure.AddChild(child);
            }

            _grid[stray.Col, stray.Row] = null;
        }

        private void ConnectRowToBoss()
        {
            int row = RowCount - 1;
            for (int c = 0; c < MapWidth; c++)
            {
                if (_grid[c, row] is { } point && !point.Children.Contains(_boss))
                {
                    point.AddChild(_boss);
                }
            }
        }

        private void ConnectRowToStart()
        {
            for (int c = 0; c < MapWidth; c++)
            {
                if (_grid[c, 1] is { } point && !_start.Children.Contains(point))
                {
                    _start.AddChild(point);
                }
            }
        }

        private void AssignPointTypes()
        {
            foreach (var point in PointsInRow(RowCount - 1))
            {
                point.Type = PointType.RestSite;
                point.CanBeModified = false;
            }

            foreach (var point in PointsInRow(1))
            {
                point.Type = PointType.Monster;
                point.CanBeModified = false;
            }

            var queue = new Queue<PointType>(
                Enumerable
                    .Repeat(PointType.RestSite, _restCount)
                    .Concat(Enumerable.Repeat(PointType.Shop, RunConstants.MapShopCount))
                    .Concat(Enumerable.Repeat(PointType.Elite, _eliteCount))
                    .Concat(Enumerable.Repeat(PointType.Unknown, _unknownCount))
            );
            AssignRemainingTypesToRandomPoints(queue);
            foreach (var point in AllMapPoints().Where(p => p.Type == PointType.Unassigned))
            {
                point.Type = PointType.Monster;
            }

            _boss.Type = PointType.Boss;
            _start.Type = PointType.Ancient;
        }

        private void AssignRemainingTypesToRandomPoints(Queue<PointType> queue)
        {
            for (int pass = 0; pass < 3; pass++)
            {
                if (queue.Count <= 0)
                {
                    break;
                }

                var candidates = AllMapPoints()
                    .Where(p => p != _boss && p != _start)
                    .Where(p => p.Type == PointType.Unassigned)
                    .ToList();
                _rng.StableShuffle(candidates, ByCoord);
                foreach (var point in candidates)
                {
                    if (queue.Count == 0)
                    {
                        break;
                    }

                    var type = NextValidPointType(queue, point);
                    if (type != PointType.Unassigned)
                    {
                        point.Type = type;
                    }
                }
            }
        }

        // `ShouldIgnoreMapPointRulesForMapPointType` is always false here: only the Big
        // Game Hunter modifier fills `PointTypesThatIgnoreRules`.
        private PointType NextValidPointType(Queue<PointType> queue, Point point)
        {
            int count = queue.Count;
            for (int i = 0; i < count; i++)
            {
                var type = queue.Dequeue();
                if (IsValidPointType(type, point))
                {
                    return type;
                }

                queue.Enqueue(type);
            }

            return PointType.Unassigned;
        }

        private bool IsValidPointType(PointType type, Point point) =>
            IsValidForUpper(type, point)
            && IsValidForLower(type, point)
            && IsValidWithParents(type, point)
            && IsValidWithChildren(type, point)
            && IsValidWithSiblings(type, point);

        private static bool IsValidForLower(PointType type, Point point) =>
            point.Row >= 6 || type is not (PointType.RestSite or PointType.Elite);

        private bool IsValidForUpper(PointType type, Point point) =>
            point.Row < _mapLength - 3 || type != PointType.RestSite;

        private static bool HasParentChildRestriction(PointType type) =>
            type is PointType.Elite or PointType.RestSite or PointType.Treasure or PointType.Shop;

        private static bool IsValidWithParents(PointType type, Point point) =>
            !HasParentChildRestriction(type)
            || !point.Parents.Concat(point.Children).Any(p => p.Type == type);

        private static bool IsValidWithChildren(PointType type, Point point) =>
            !HasParentChildRestriction(type) || !point.Children.Any(p => p.Type == type);

        private static bool IsValidWithSiblings(PointType type, Point point) =>
            type
                is not (
                    PointType.RestSite
                    or PointType.Monster
                    or PointType.Unknown
                    or PointType.Elite
                    or PointType.Shop
                )
            || !point
                .Parents.SelectMany(parent => parent.Children)
                .Where(sibling => sibling != point)
                .Any(sibling => sibling.Type == type);

        // ── MapPathPruning ───────────────────────────────────────────────────

        private void PruneAndRepair()
        {
            for (int i = 0; i < 3; i++)
            {
                PruneDuplicateSegments();
                if (!RepairPrunedPointTypes())
                {
                    break;
                }
            }
        }

        private bool RepairPrunedPointTypes()
        {
            bool repaired = false;
            repaired |= RepairPointType(PointType.Shop, RunConstants.MapShopCount);
            repaired |= RepairPointType(PointType.Elite, _eliteCount);
            repaired |= RepairPointType(PointType.RestSite, _restCount);
            return repaired | RepairPointType(PointType.Unknown, _unknownCount);
        }

        private bool RepairPointType(PointType type, int targetCount)
        {
            int missing = targetCount - AllMapPoints().Count(p => p.Type == type);
            if (missing <= 0)
            {
                return false;
            }

            bool repaired = false;
            var candidates = AllMapPoints()
                .Where(p => p.Type == PointType.Monster && p.CanBeModified)
                .ToList();
            _rng.StableShuffle(candidates, ByCoord);
            foreach (var point in candidates)
            {
                if (missing == 0)
                {
                    break;
                }

                if (IsValidPointType(type, point))
                {
                    point.Type = type;
                    missing--;
                    repaired = true;
                }
            }

            return repaired;
        }

        private void PruneDuplicateSegments()
        {
            int iterations = 0;
            var matching = FindMatchingSegments();
            while (PrunePaths(matching))
            {
                iterations++;
                if (iterations > 50)
                {
                    throw new InvalidOperationException(
                        $"Unable to prune matching segments in {iterations} iterations"
                    );
                }

                matching = FindMatchingSegments();
            }
        }

        private List<List<Point[]>> FindMatchingSegments()
        {
            var segments = new SortedDictionary<string, List<Point[]>>(StringComparer.Ordinal);
            foreach (var path in FindAllPaths(_start))
            {
                AddSegmentsToDictionary(path, segments);
            }

            return segments.Values.Where(list => list.Count > 1).ToList();
        }

        private static List<List<Point>> FindAllPaths(Point current)
        {
            if (current.Type == PointType.Boss)
            {
                return
                [
                    [current],
                ];
            }

            var paths = new List<List<Point>>();
            foreach (var child in current.Children)
            {
                foreach (var tail in FindAllPaths(child))
                {
                    paths.Add([current, .. tail]);
                }
            }

            return paths;
        }

        private static void AddSegmentsToDictionary(
            IReadOnlyList<Point> path,
            IDictionary<string, List<Point[]>> segments
        )
        {
            for (int i = 0; i < path.Count - 1; i++)
            {
                // IsValidSegmentStartMapPoint
                if (path[i].Children.Count <= 1 && path[i].Row != 0)
                {
                    continue;
                }

                for (int j = 2; j < path.Count - i; j++)
                {
                    // IsValidSegmentEndMapPoint
                    if (path[i + j].Parents.Count < 2)
                    {
                        continue;
                    }

                    var segment = path.Skip(i).Take(j + 1).ToArray();
                    string key = SegmentKey(segment);
                    if (!segments.TryGetValue(key, out var existing))
                    {
                        segments[key] = [segment];
                    }
                    else if (!existing.Any(other => OverlappingSegment(other, segment)))
                    {
                        existing.Add(segment);
                    }
                }
            }
        }

        private static string SegmentKey(IReadOnlyList<Point> segment)
        {
            var start = segment[0];
            var end = segment[^1];
            string prefix =
                start.Row == 0
                    ? $"{start.Row}-{end.Col},{end.Row}-"
                    : $"{start.Col},{start.Row}-{end.Col},{end.Row}-";
            return prefix + string.Join(",", segment.Select(point => (int)point.Type));
        }

        private static bool OverlappingSegment(IReadOnlyList<Point> a, IReadOnlyList<Point> b)
        {
            if (a.Count < 3 || b.Count < 3)
            {
                return false;
            }

            for (int i = 1; i <= a.Count - 2; i++)
            {
                if (ReferenceEquals(a[i], b[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private bool PrunePaths(IEnumerable<List<Point[]>> matching)
        {
            foreach (var group in matching)
            {
                _rng.Shuffle(group);
                if (PruneAllButLast(group) != 0)
                {
                    return true;
                }

                if (BreakAParentChildRelationshipInAnySegment(group))
                {
                    return true;
                }
            }

            return false;
        }

        private int PruneAllButLast(IReadOnlyList<Point[]> matches)
        {
            int pruned = 0;
            foreach (var match in matches)
            {
                if (pruned == matches.Count - 1)
                {
                    return pruned;
                }

                if (PruneSegment(match))
                {
                    pruned++;
                }
            }

            return pruned;
        }

        private bool PruneSegment(Point[] segment)
        {
            bool result = false;
            for (int i = 0; i < segment.Length - 1; i++)
            {
                var point = segment[i];
                if (!IsInMap(point))
                {
                    return true;
                }

                if (
                    point.Children.Count > 1
                    || point.Parents.Count > 1
                    || point.Parents.Any(parent => parent.Children.Count == 1 && !IsRemoved(parent))
                )
                {
                    continue;
                }

                if (segment.Skip(i).Any(p => p.Children.Count > 1 && p.Parents.Count == 1))
                {
                    continue;
                }

                if (segment[^1].Parents.Count == 1)
                {
                    return false;
                }

                if (
                    !point
                        .Children.Where(child => !segment.Contains(child))
                        .Any(child => child.Parents.Count == 1)
                )
                {
                    RemovePoint(point);
                    result = true;
                }
            }

            return result;
        }

        private void RemovePoint(Point point)
        {
            _grid[point.Col, point.Row] = null;
            _startMapPoints.Remove(point);
            foreach (var child in point.Children.ToList())
            {
                point.RemoveChild(child);
            }

            foreach (var parent in point.Parents.ToList())
            {
                parent.RemoveChild(point);
            }
        }

        // `MapPathPruning.IsInMap`/`IsRemoved` index the grid directly, so they must not
        // be handed the boss, whose row is one past it. Neither ever is: a segment's last
        // point is never tested, and the boss is never anyone's parent.
        private bool IsInMap(Point point) =>
            _grid[point.Col, point.Row] != null
            || point.Type == PointType.Ancient
            || point.Type == PointType.Boss;

        private bool IsRemoved(Point point) => _grid[point.Col, point.Row] == null;

        private static bool BreakAParentChildRelationshipInAnySegment(List<Point[]> matches)
        {
            foreach (var match in matches)
            {
                if (BreakAParentChildRelationshipInSegment(match))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool BreakAParentChildRelationshipInSegment(Point[] segment)
        {
            bool result = false;
            for (int i = 0; i < segment.Length - 1; i++)
            {
                var point = segment[i];
                if (point.Children.Count >= 2)
                {
                    var child = segment[i + 1];
                    if (child.Parents.Count != 1)
                    {
                        point.RemoveChild(child);
                        result = true;
                    }
                }
            }

            return result;
        }
    }
}
