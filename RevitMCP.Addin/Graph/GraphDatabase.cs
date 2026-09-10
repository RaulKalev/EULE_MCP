using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using RevitMCP.Core.Safety;

namespace RevitMCP.Addin.Graph;

/// <summary>
/// SQLite access for one graph file. No Revit API dependency — safe to unit test in memory.
/// Writers use <see cref="CreateNew"/> on a local temp file and publish through
/// <see cref="GraphStore"/>; readers use <see cref="OpenReadOnly"/> on a local copy.
/// Connection pooling is disabled so disposing really closes the file handle (needed for the
/// atomic replace on shared folders).
/// </summary>
public sealed class GraphDatabase : IDisposable
{
    private static readonly object ProviderLock = new();
    private static bool _providerReady;

    private readonly SqliteConnection _connection;

    public string DataSource { get; }
    public bool IsReadOnly { get; }

    private GraphDatabase(SqliteConnection connection, string dataSource, bool isReadOnly)
    {
        _connection = connection;
        DataSource = dataSource;
        IsReadOnly = isReadOnly;
    }

    // ─── Provider / open ────────────────────────────────────────────────────

    /// <summary>
    /// Initialises the SQLitePCLRaw provider once per process. The add-in binds to the bundle it
    /// references at compile time (winsqlite3 in Revit, e_sqlite3 in the test project).
    /// </summary>
    public static void EnsureProvider()
    {
        lock (ProviderLock)
        {
            if (_providerReady) return;
            try
            {
                SQLitePCL.Batteries_V2.Init();
                _providerReady = true;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "The SQLite provider for the model graph could not be initialised: " + ex.Message, ex);
            }
        }
    }

    /// <summary>Creates a brand-new database file (any existing file at the path is deleted).</summary>
    public static GraphDatabase CreateNew(string path)
    {
        EnsureProvider();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        DeleteIfExists(path);
        DeleteIfExists(path + "-journal");
        DeleteIfExists(path + "-wal");
        DeleteIfExists(path + "-shm");

        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();

        var conn = new SqliteConnection(cs);
        conn.Open();
        var db = new GraphDatabase(conn, path, isReadOnly: false);
        // Build-time pragmas: the file is a private temp copy, so durability is irrelevant and
        // no side files (-journal/-wal) must be left behind for the publish step.
        db.Execute("PRAGMA journal_mode=MEMORY;");
        db.Execute("PRAGMA synchronous=OFF;");
        db.Execute("PRAGMA temp_store=MEMORY;");
        db.Execute(GraphSchema.CreateSql);
        return db;
    }

    /// <summary>In-memory database with the schema applied. Used by unit tests.</summary>
    public static GraphDatabase CreateInMemory()
    {
        EnsureProvider();
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = ":memory:",
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
        var conn = new SqliteConnection(cs);
        conn.Open();
        var db = new GraphDatabase(conn, ":memory:", isReadOnly: false);
        db.Execute(GraphSchema.CreateSql);
        return db;
    }

    /// <summary>Opens an existing file strictly read-only. Never call on the shared copy directly — see <see cref="GraphStore.OpenForRead"/>.</summary>
    public static GraphDatabase OpenReadOnly(string path)
    {
        EnsureProvider();
        if (!File.Exists(path))
            throw new FileNotFoundException("Graph database not found.", path);

        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
        var conn = new SqliteConnection(cs);
        conn.Open();
        return new GraphDatabase(conn, path, isReadOnly: true);
    }

    public void Dispose()
    {
        try { _connection.Close(); } catch { }
        _connection.Dispose();
    }

    // ─── Write ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Bulk-writes nodes, edges and meta inside one transaction. Duplicate node ids replace the
    /// previous row; duplicate edges are ignored (primary key on src,dst,rel).
    /// Returns the number of node and edge rows now in the database.
    /// </summary>
    public (long Nodes, long Edges) WriteGraph(
        IEnumerable<GraphNode> nodes,
        IEnumerable<GraphEdge> edges,
        IReadOnlyDictionary<string, string> meta)
    {
        if (IsReadOnly) throw new InvalidOperationException("Graph database was opened read-only.");

        using var tx = _connection.BeginTransaction();

        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT OR REPLACE INTO nodes (id, kind, name, category, level, workset, extra) " +
                "VALUES ($id, $kind, $name, $category, $level, $workset, $extra)";
            var pId = cmd.Parameters.Add("$id", SqliteType.Text);
            var pKind = cmd.Parameters.Add("$kind", SqliteType.Text);
            var pName = cmd.Parameters.Add("$name", SqliteType.Text);
            var pCategory = cmd.Parameters.Add("$category", SqliteType.Text);
            var pLevel = cmd.Parameters.Add("$level", SqliteType.Text);
            var pWorkset = cmd.Parameters.Add("$workset", SqliteType.Text);
            var pExtra = cmd.Parameters.Add("$extra", SqliteType.Text);

            foreach (var n in nodes)
            {
                if (string.IsNullOrEmpty(n.Id)) continue;
                pId.Value = n.Id;
                pKind.Value = n.Kind ?? GraphSchema.Kinds.Element;
                pName.Value = (object?)n.Name ?? DBNull.Value;
                pCategory.Value = (object?)n.Category ?? DBNull.Value;
                pLevel.Value = (object?)n.Level ?? DBNull.Value;
                pWorkset.Value = (object?)n.Workset ?? DBNull.Value;
                pExtra.Value = (object?)n.Extra ?? DBNull.Value;
                cmd.ExecuteNonQuery();
            }
        }

        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR IGNORE INTO edges (src, dst, rel) VALUES ($src, $dst, $rel)";
            var pSrc = cmd.Parameters.Add("$src", SqliteType.Text);
            var pDst = cmd.Parameters.Add("$dst", SqliteType.Text);
            var pRel = cmd.Parameters.Add("$rel", SqliteType.Text);

            foreach (var e in edges)
            {
                if (string.IsNullOrEmpty(e.Src) || string.IsNullOrEmpty(e.Dst) || string.IsNullOrEmpty(e.Rel)) continue;
                pSrc.Value = e.Src;
                pDst.Value = e.Dst;
                pRel.Value = e.Rel;
                cmd.ExecuteNonQuery();
            }
        }

        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES ($key, $value)";
            var pKey = cmd.Parameters.Add("$key", SqliteType.Text);
            var pValue = cmd.Parameters.Add("$value", SqliteType.Text);
            foreach (var kv in meta)
            {
                pKey.Value = kv.Key;
                pValue.Value = (object?)kv.Value ?? DBNull.Value;
                cmd.ExecuteNonQuery();
            }
        }

        tx.Commit();

        var counts = Counts();
        // Counts are stored after the write so readers get them without a full table scan.
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES ($k1, $v1), ($k2, $v2)";
            cmd.Parameters.AddWithValue("$k1", GraphSchema.MetaKeys.NodeCount);
            cmd.Parameters.AddWithValue("$v1", counts.Nodes.ToString(CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$k2", GraphSchema.MetaKeys.EdgeCount);
            cmd.Parameters.AddWithValue("$v2", counts.Edges.ToString(CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }
        return counts;
    }

    // ─── Read: meta / nodes ────────────────────────────────────────────────

    public (long Nodes, long Edges) Counts()
    {
        var nodes = Scalar<long>("SELECT COUNT(*) FROM nodes");
        var edges = Scalar<long>("SELECT COUNT(*) FROM edges");
        return (nodes, edges);
    }

    public GraphMeta ReadMeta()
    {
        var meta = new GraphMeta();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM meta";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.GetString(0);
            var value = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            meta.Values[key] = value;
        }
        return meta;
    }

    public GraphNode? GetNode(string id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = NodeSelect + " WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadNode(reader, 0) : null;
    }

    public List<GraphNode> GetNodes(IEnumerable<string> ids)
    {
        var result = new List<GraphNode>();
        foreach (var id in ids)
        {
            var n = GetNode(id);
            if (n != null) result.Add(n);
        }
        return result;
    }

    // ─── Read: neighbors ───────────────────────────────────────────────────

    /// <param name="direction">"out" (id is src), "in" (id is dst) or "both".</param>
    public List<GraphNeighbor> Neighbors(string id, string? rel, string direction, int limit)
    {
        var dir = NormalizeDirection(direction, "both");
        var results = new List<GraphNeighbor>();
        if (limit <= 0) limit = 100;

        if (dir == "out" || dir == "both")
            CollectNeighbors(id, rel, outgoing: true, limit - results.Count, results);
        if ((dir == "in" || dir == "both") && results.Count < limit)
            CollectNeighbors(id, rel, outgoing: false, limit - results.Count, results);

        return results;
    }

    private void CollectNeighbors(string id, string? rel, bool outgoing, int limit, List<GraphNeighbor> into)
    {
        if (limit <= 0) return;
        using var cmd = _connection.CreateCommand();
        var join = outgoing ? "n.id = e.dst" : "n.id = e.src";
        var where = outgoing ? "e.src = $id" : "e.dst = $id";
        var relFilter = string.IsNullOrWhiteSpace(rel) ? string.Empty : " AND e.rel = $rel";
        cmd.CommandText =
            $"SELECT e.rel, {NodeColumns("n")} FROM edges e JOIN nodes n ON {join} " +
            $"WHERE {where}{relFilter} ORDER BY e.rel, n.kind, n.name, n.id LIMIT $limit";
        cmd.Parameters.AddWithValue("$id", id);
        if (!string.IsNullOrWhiteSpace(rel)) cmd.Parameters.AddWithValue("$rel", rel!.Trim().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$limit", limit);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            into.Add(new GraphNeighbor
            {
                Rel = reader.GetString(0),
                Node = ReadNode(reader, 1),
                Direction = outgoing ? "out" : "in"
            });
        }
    }

    // ─── Read: find ────────────────────────────────────────────────────────

    /// <summary>Filtered, paginated node lookup. All filters are optional; matching is case-insensitive (ASCII).</summary>
    public PagedResult<GraphNode> Find(
        string? kind, string? category, string? level, string? workset, string? nameContains,
        int page, int pageSize)
    {
        if (page < 0) page = 0;
        if (pageSize <= 0) pageSize = QueryLimits.Default.DefaultPageSize;

        var clauses = new List<string>();
        var parameters = new List<(string Name, object Value)>();

        void AddEquals(string column, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            clauses.Add($"{column} = ${column} COLLATE NOCASE");
            parameters.Add(($"${column}", value!.Trim()));
        }

        AddEquals("kind", kind?.ToLowerInvariant());
        AddEquals("category", category);
        AddEquals("level", level);
        AddEquals("workset", workset);
        if (!string.IsNullOrWhiteSpace(nameContains))
        {
            clauses.Add("name LIKE $name ESCAPE '\\'");
            parameters.Add(("$name", "%" + EscapeLike(nameContains!.Trim()) + "%"));
        }

        var where = clauses.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", clauses);

        long total;
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM nodes" + where;
            foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
            total = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        var items = new List<GraphNode>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = NodeSelect + where + " ORDER BY kind, name, id LIMIT $limit OFFSET $offset";
            foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
            cmd.Parameters.AddWithValue("$limit", pageSize);
            cmd.Parameters.AddWithValue("$offset", (long)page * pageSize);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) items.Add(ReadNode(reader, 0));
        }

        var hasMore = (long)(page + 1) * pageSize < total;
        return new PagedResult<GraphNode>
        {
            Items = items,
            ItemsReturned = items.Count,
            TotalAvailable = (int)Math.Min(total, int.MaxValue),
            Page = page,
            PageSize = pageSize,
            HasMore = hasMore,
            NextPageToken = hasMore ? (page + 1).ToString(CultureInfo.InvariantCulture) : null
        };
    }

    // ─── Read: path ────────────────────────────────────────────────────────

    /// <summary>
    /// Breadth-first shortest path treating edges as undirected (a feeder chain is traversed
    /// against the fed_by direction, for example). Each step reports the relationship and the
    /// edge direction relative to the walk.
    /// </summary>
    public GraphPathResult FindPath(string fromId, string toId, int maxHops, int maxVisited = 50_000)
    {
        var result = new GraphPathResult();
        if (maxHops < 1) maxHops = 1;
        if (GetNode(fromId) == null || GetNode(toId) == null)
            return result;

        if (fromId == toId)
        {
            result.Found = true;
            result.Steps.Add(new GraphPathStep { Node = GetNode(fromId)! });
            result.VisitedNodes = 1;
            return result;
        }

        var parent = new Dictionary<string, (string Parent, string Rel, string Direction)>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal) { fromId };
        var frontier = new List<string> { fromId };

        for (var hop = 0; hop < maxHops && frontier.Count > 0; hop++)
        {
            var next = new List<string>();
            foreach (var current in frontier)
            {
                foreach (var (neighbor, rel, direction) in AdjacentIds(current))
                {
                    if (!visited.Add(neighbor)) continue;
                    parent[neighbor] = (current, rel, direction);
                    if (neighbor == toId)
                    {
                        result.Found = true;
                        result.VisitedNodes = visited.Count;
                        BuildPath(fromId, toId, parent, result);
                        return result;
                    }
                    next.Add(neighbor);
                    if (visited.Count >= maxVisited)
                    {
                        result.SearchTruncated = true;
                        result.VisitedNodes = visited.Count;
                        return result;
                    }
                }
            }
            frontier = next;
        }

        result.VisitedNodes = visited.Count;
        return result;
    }

    private void BuildPath(
        string fromId, string toId,
        Dictionary<string, (string Parent, string Rel, string Direction)> parent,
        GraphPathResult result)
    {
        var chain = new List<GraphPathStep>();
        var current = toId;
        while (current != fromId)
        {
            var (p, rel, direction) = parent[current];
            chain.Add(new GraphPathStep { Node = GetNode(current) ?? new GraphNode { Id = current }, Rel = rel, Direction = direction });
            current = p;
        }
        chain.Add(new GraphPathStep { Node = GetNode(fromId) ?? new GraphNode { Id = fromId } });
        chain.Reverse();
        result.Steps = chain;
        result.Hops = chain.Count - 1;
    }

    private IEnumerable<(string Id, string Rel, string Direction)> AdjacentIds(string id)
    {
        var list = new List<(string, string, string)>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT dst, rel FROM edges WHERE src = $id";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) list.Add((reader.GetString(0), reader.GetString(1), "out"));
        }
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT src, rel FROM edges WHERE dst = $id";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) list.Add((reader.GetString(0), reader.GetString(1), "in"));
        }
        return list;
    }

    // ─── Read: subtree ─────────────────────────────────────────────────────

    /// <summary>
    /// Follows one relationship from a root, breadth-first, up to <paramref name="depth"/> levels.
    /// direction "in" collects nodes whose edges point AT the current node (e.g. everything
    /// fed_by a panel); "out" follows edges leaving it.
    /// </summary>
    public GraphSubtreeResult Subtree(string rootId, string rel, int depth, string direction, int maxNodes)
    {
        var result = new GraphSubtreeResult { Root = GetNode(rootId) };
        if (result.Root == null) return result;
        if (depth < 1) depth = 1;
        if (maxNodes <= 0) maxNodes = 500;
        var dir = NormalizeDirection(direction, "in");
        var relNorm = rel.Trim().ToLowerInvariant();

        var visited = new HashSet<string>(StringComparer.Ordinal) { rootId };
        var frontier = new List<string> { rootId };

        for (var level = 1; level <= depth && frontier.Count > 0; level++)
        {
            var next = new List<string>();
            foreach (var current in frontier)
            {
                var children = new List<GraphNeighbor>();
                if (dir == "in" || dir == "both") CollectNeighbors(current, relNorm, outgoing: false, int.MaxValue, children);
                if (dir == "out" || dir == "both") CollectNeighbors(current, relNorm, outgoing: true, int.MaxValue, children);

                foreach (var child in children)
                {
                    if (!visited.Add(child.Node.Id)) continue;
                    if (result.Nodes.Count >= maxNodes)
                    {
                        result.Truncated = true;
                        result.MaxDepthReached = level;
                        return result;
                    }
                    result.Nodes.Add(new GraphSubtreeNode { Node = child.Node, Depth = level, ParentId = current });
                    next.Add(child.Node.Id);
                }
            }
            if (next.Count > 0) result.MaxDepthReached = level;
            frontier = next;
        }

        return result;
    }

    // ─── Read: summary ─────────────────────────────────────────────────────

    public GraphSummaryResult Summarize(int topN = 25, int sampleSize = 25)
    {
        if (topN <= 0) topN = 25;
        if (sampleSize < 0) sampleSize = 0;
        var s = new GraphSummaryResult { TopN = topN };

        s.NodesByKind = GroupCount("SELECT kind, COUNT(*) FROM nodes GROUP BY kind ORDER BY 2 DESC", topN: int.MaxValue);
        s.EdgesByRel = GroupCount("SELECT rel, COUNT(*) FROM edges GROUP BY rel ORDER BY 2 DESC", topN: int.MaxValue);
        s.ElementsByCategory = GroupCount(
            "SELECT COALESCE(category, ''), COUNT(*) FROM nodes WHERE kind = 'element' GROUP BY category ORDER BY 2 DESC, 1", topN);
        s.ElementsByLevel = GroupCount(
            "SELECT COALESCE(level, ''), COUNT(*) FROM nodes WHERE kind = 'element' GROUP BY level ORDER BY 2 DESC, 1", topN);
        s.ElementsByWorkset = GroupCount(
            "SELECT COALESCE(workset, ''), COUNT(*) FROM nodes WHERE kind = 'element' GROUP BY workset ORDER BY 2 DESC, 1", topN);

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText =
                "SELECT p.id, COALESCE(p.name, ''), COALESCE(p.level, ''), " +
                " (SELECT COUNT(*) FROM edges e WHERE e.dst = p.id AND e.rel = 'fed_by') AS circuits, " +
                " (SELECT COUNT(*) FROM edges e2 JOIN edges e1 ON e1.src = e2.dst " +
                "    WHERE e1.rel = 'fed_by' AND e1.dst = p.id AND e2.rel = 'fed_by') AS fed " +
                "FROM nodes p WHERE p.kind = 'panel' ORDER BY circuits DESC, p.name LIMIT $top";
            cmd.Parameters.AddWithValue("$top", topN);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                s.Panels.Add(new GraphPanelSummary
                {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    Level = reader.GetString(2),
                    CircuitCount = reader.GetInt64(3),
                    FedElementCount = reader.GetInt64(4)
                });
            }
        }

        const string circuitsNoPanel =
            "FROM nodes c WHERE c.kind = 'circuit' AND NOT EXISTS (SELECT 1 FROM edges e WHERE e.src = c.id AND e.rel = 'fed_by')";
        const string circuitsNoElements =
            "FROM nodes c WHERE c.kind = 'circuit' AND NOT EXISTS (SELECT 1 FROM edges e WHERE e.dst = c.id AND e.rel = 'fed_by')";
        const string elementsNoLocation =
            "FROM nodes c WHERE c.kind = 'element' AND NOT EXISTS (SELECT 1 FROM edges e WHERE e.src = c.id AND e.rel = 'located_in')";

        s.CircuitsWithoutPanelCount = Scalar<long>("SELECT COUNT(*) " + circuitsNoPanel);
        s.CircuitsWithoutPanel = Sample(circuitsNoPanel, sampleSize);
        s.CircuitsWithoutElementsCount = Scalar<long>("SELECT COUNT(*) " + circuitsNoElements);
        s.CircuitsWithoutElements = Sample(circuitsNoElements, sampleSize);
        s.ElementsWithoutLocationCount = Scalar<long>("SELECT COUNT(*) " + elementsNoLocation);
        s.ElementsWithoutLocation = Sample(elementsNoLocation, sampleSize);

        return s;
    }

    private List<GraphNode> Sample(string fromWhere, int sampleSize)
    {
        var list = new List<GraphNode>();
        if (sampleSize == 0) return list;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT {NodeColumns("c")} {fromWhere} ORDER BY c.name, c.id LIMIT $n";
        cmd.Parameters.AddWithValue("$n", sampleSize);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(ReadNode(reader, 0));
        return list;
    }

    private List<GraphCount> GroupCount(string sql, int topN)
    {
        var list = new List<GraphCount>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = topN == int.MaxValue ? sql : sql + " LIMIT $top";
        if (topN != int.MaxValue) cmd.Parameters.AddWithValue("$top", topN);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new GraphCount
            {
                Key = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                Count = reader.GetInt64(1)
            });
        }
        return list;
    }

    // ─── Helpers ───────────────────────────────────────────────────────────

    private const string NodeSelect = "SELECT id, kind, name, category, level, workset, extra FROM nodes";

    private static string NodeColumns(string alias) =>
        $"{alias}.id, {alias}.kind, {alias}.name, {alias}.category, {alias}.level, {alias}.workset, {alias}.extra";

    private static GraphNode ReadNode(SqliteDataReader reader, int offset) => new()
    {
        Id = reader.GetString(offset),
        Kind = reader.IsDBNull(offset + 1) ? string.Empty : reader.GetString(offset + 1),
        Name = reader.IsDBNull(offset + 2) ? string.Empty : reader.GetString(offset + 2),
        Category = reader.IsDBNull(offset + 3) ? string.Empty : reader.GetString(offset + 3),
        Level = reader.IsDBNull(offset + 4) ? string.Empty : reader.GetString(offset + 4),
        Workset = reader.IsDBNull(offset + 5) ? string.Empty : reader.GetString(offset + 5),
        Extra = reader.IsDBNull(offset + 6) ? null : reader.GetString(offset + 6)
    };

    private static string NormalizeDirection(string? direction, string fallback)
    {
        var d = (direction ?? string.Empty).Trim().ToLowerInvariant();
        return d is "in" or "out" or "both" ? d : fallback;
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private void Execute(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture)!;
    }

    private static void DeleteIfExists(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
