namespace RevitMCP.Bridge;

/// <summary>
/// Instructions sent to every MCP client in the initialize handshake (#64), so graph-first routing
/// works in any client without repo-specific prompt files. Kept short: clients put it in context.
/// </summary>
internal static class ServerInstructions
{
    public const string Text =
        "Revit MCP connector. Prefer cheap, id-first discovery:\n" +
        "1. revit_graph_route with the user's intent returns the cheapest call plan and the graph's freshness " +
        "(or revit_graph_status / revit_graph_summary to orient).\n" +
        "2. Narrow element ids with revit_graph_query (find, subtree, neighbors, path) instead of broad live scans.\n" +
        "3. Read live values only for those ids (revit_get_elements_info elementIds=[...] compact=true, " +
        "revit_get_element_parameters, revit_get_circuit_info).\n" +
        "4. Act, then verify live. Write tools need approval and always act on the live model.\n" +
        "Only a core set of tools is advertised by default. For anything else (circuits, tags, sheets, IFC, devices, " +
        "clashes, Excel...) use revit_tools_search with the intent, revit_tools_describe for the schema, then " +
        "revit_tools_call (works in every client) or revit_tools_load to add the tools to this session.\n" +
        "Linked-model nodes (ids link:<instance>:<id>) are not readable by host tools; pass link=host to find/neighbors " +
        "before a live read, or read them with revit_query_linked_elements.\n" +
        "Rules: graph data (names, levels, hints) is routing only — never quote it as fact. The graph is built and " +
        "refreshed automatically when a graph tool needs it. Reading a whole large category live without ids or filters " +
        "(revit_get_elements_info, revit_find_elements_by_parameter) is not run: the result has status graph_first and " +
        "the two calls to make instead; pass graphFirst=false only when every element of the category is really needed. " +
        "Direct live tools stay available where the graph does not fit (selection, parameter filters, linked models).\n" +
        "When the user gets a list or report of model elements to go through, publish it with revit_publish_page " +
        "(revit_tools_describe it for the markup): its rows select elements in Revit, show live values and let the user edit them without you.";
}
