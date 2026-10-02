namespace RevitMCP.Addin.Graph;

/// <summary>
/// Flat SQLite schema for the per-model routing graph. Documented in <c>Graph/SCHEMA.md</c>.
/// The graph is a routing layer only: it stores element ids, names and relationships so an
/// agent can locate the right ids cheaply. It never replaces a live parameter read.
/// </summary>
public static class GraphSchema
{
    /// <summary>Bump when the table layout or node/edge vocabulary changes incompatibly.</summary>
    /// <summary>2 = edge ownership (edge_owners) for incremental updates (#61). Version 1 graphs stay readable.</summary>
    public const int SchemaVersion = 2;

    /// <summary>File name suffix of a graph database, e.g. <c>Project.graph.db</c>.</summary>
    public const string FileExtension = ".graph.db";

    /// <summary>Config key (dot-path) in the user/company config JSON that names the shared folder.</summary>
    public const string SharedFolderConfigKey = "graph.sharedFolder";

    /// <summary>Attached to every status/query/summary response.</summary>
    public const string RoutingNote = "Graph values are for routing only; fetch live data by ID for actual values.";

    /// <summary>Prefix for workset node ids — worksets have their own id space, separate from element ids.</summary>
    public const string WorksetIdPrefix = "ws:";

    /// <summary>
    /// Prefix for linked-model node ids (#62): <c>link:&lt;linkInstanceId&gt;:&lt;linkedElementId&gt;</c>. The link
    /// instance id namespaces the linked document, so equal element ids in different links (or the host)
    /// never collide. Host ids stay plain element ids.
    /// </summary>
    public const string LinkIdPrefix = "link:";

    public static class Kinds
    {
        public const string Element = "element";
        public const string Type    = "type";
        public const string Panel   = "panel";
        public const string Circuit = "circuit";
        public const string Space   = "space";
        public const string Level   = "level";
        public const string Workset = "workset";
        public const string Sheet   = "sheet";
        public const string View    = "view";
        /// <summary>A Revit link instance in the host model (#62). Its id is the host element id.</summary>
        public const string Link    = "link";

        public static readonly string[] All =
        {
            Element, Type, Panel, Circuit, Space, Level, Workset, Sheet, View, Link
        };
    }

    public static class Rels
    {
        /// <summary>element/panel → circuit, circuit → panel (supply side).</summary>
        public const string FedBy     = "fed_by";
        /// <summary>element → space (room or MEP space that contains it).</summary>
        public const string LocatedIn = "located_in";
        /// <summary>element → host element (wall, ceiling, floor, other family instance).</summary>
        public const string HostedOn  = "hosted_on";
        /// <summary>element/panel/space → type.</summary>
        public const string TypeOf    = "type_of";
        /// <summary>element/space → view in which a tag points at it.</summary>
        public const string TaggedIn  = "tagged_in";
        /// <summary>view → sheet.</summary>
        public const string OnSheet   = "on_sheet";
        /// <summary>any node → workset.</summary>
        public const string InWorkset = "in_workset";
        /// <summary>any node → level.</summary>
        public const string OnLevel   = "on_level";
        /// <summary>linked-model node → the link instance it was read through (#62).</summary>
        public const string InLink    = "in_link";

        public static readonly string[] All =
        {
            FedBy, LocatedIn, HostedOn, TypeOf, TaggedIn, OnSheet, InWorkset, OnLevel, InLink
        };
    }

    public static class MetaKeys
    {
        // Required by the schema contract
        public const string ModelPath      = "model_path";
        public const string ModelName      = "model_name";
        public const string BuiltAt        = "built_at";
        public const string CentralVersion = "central_version";
        public const string ElementCount   = "element_count";
        public const string SchemaVersion  = "schema_version";
        public const string BuiltBy        = "built_by";

        // Additional diagnostics
        public const string VersionSource  = "version_source";
        public const string IsWorkshared   = "is_workshared";
        public const string RevitVersion   = "revit_version";
        public const string NodeCount      = "node_count";
        public const string EdgeCount      = "edge_count";
        public const string BuildDurationMs = "build_duration_ms";
        public const string ProjectKey     = "project_key";

        // Incremental updates (#61)
        public const string ElementLimitReached = "element_limit_reached";
        public const string LastFullBuildAt     = "last_full_build_at";
        public const string IncrementalUpdates  = "incremental_updates";
        // #63: the routing-parameter allowlist the graph was built with, and where it came from.
        public const string RoutingParameters       = "routing_parameters";
        public const string RoutingParametersSource = "routing_parameters_source";
    }

    /// <summary>DDL executed on a fresh database. Kept flat on purpose.</summary>
    public const string CreateSql = @"
CREATE TABLE IF NOT EXISTS nodes (
    id       TEXT PRIMARY KEY,
    kind     TEXT NOT NULL,
    name     TEXT,
    category TEXT,
    level    TEXT,
    workset  TEXT,
    extra    TEXT
);
CREATE TABLE IF NOT EXISTS edges (
    src TEXT NOT NULL,
    dst TEXT NOT NULL,
    rel TEXT NOT NULL,
    PRIMARY KEY (src, dst, rel)
);
CREATE TABLE IF NOT EXISTS edge_owners (
    src   TEXT NOT NULL,
    dst   TEXT NOT NULL,
    rel   TEXT NOT NULL,
    owner TEXT NOT NULL,
    PRIMARY KEY (src, dst, rel, owner)
);
CREATE TABLE IF NOT EXISTS node_params (
    node_id TEXT NOT NULL,
    name    TEXT NOT NULL,
    value   TEXT NOT NULL,
    norm    TEXT NOT NULL,
    PRIMARY KEY (node_id, name)
);
CREATE TABLE IF NOT EXISTS meta (
    key   TEXT PRIMARY KEY,
    value TEXT
);
CREATE INDEX IF NOT EXISTS ix_nodes_kind     ON nodes(kind);
CREATE INDEX IF NOT EXISTS ix_nodes_category ON nodes(category);
CREATE INDEX IF NOT EXISTS ix_nodes_level    ON nodes(level);
CREATE INDEX IF NOT EXISTS ix_edges_src      ON edges(src);
CREATE INDEX IF NOT EXISTS ix_edges_dst      ON edges(dst);
CREATE INDEX IF NOT EXISTS ix_edges_rel      ON edges(rel);
CREATE INDEX IF NOT EXISTS ix_edge_owners_owner ON edge_owners(owner);
CREATE INDEX IF NOT EXISTS ix_node_params_name_norm ON node_params(name, norm);
";

    public static bool IsKind(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Array.IndexOf(Kinds.All, value!.Trim().ToLowerInvariant()) >= 0;

    public static bool IsRel(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Array.IndexOf(Rels.All, value!.Trim().ToLowerInvariant()) >= 0;
}
