using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ModelFamilies;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>
/// The body of revit_(preview_)edit_model_family_connectors (#174): checks every operation against the family first,
/// then removes, creates and edits connectors, links them, and reads each one back. The preview runs the same body and
/// the session rolls it back.
/// </summary>
internal static class ModelFamilyConnectorExecutor
{
    public const string RemoveArg = "remove";
    public const string CreateArg = "connectors";
    public const string EditArg = "edit";

    /// <summary>One line for the approval dialog.</summary>
    public static string Summary(Autodesk.Revit.UI.UIApplication uiapp, RevitMCP.Core.Models.McpToolRequest request)
    {
        var plan = ModelFamilyConnectorParser.Parse(request.Arguments ?? new Dictionary<string, object?>(), RemoveArg, CreateArg, EditArg, new List<string>());
        var parts = new List<string>();
        if (plan.Create.Count > 0)
            parts.Add($"create {plan.Create.Count} connector(s) ({string.Join(", ", plan.Create.Select(c => c.Name.Length > 0 ? $"{c.Name}: {ModelFamilyConnectorTables.WireName(c.Domain)}" : ModelFamilyConnectorTables.WireName(c.Domain)))})");
        if (plan.Edit.Count > 0) parts.Add($"edit connector(s) {string.Join(", ", plan.Edit.Select(e => e.ConnectorId))}");
        if (plan.Remove.Count > 0) parts.Add($"delete connector(s) {string.Join(", ", plan.Remove)}");
        return $"MEP connectors: {(parts.Count > 0 ? string.Join("; ", parts) : "no valid operation")} in {ModelFamilySession.DescribeTarget(request)}.";
    }

    public static object? Run(ModelFamilyContext context)
    {
        var doc = context.FamilyDoc;
        var problems = context.Problems;
        var warnings = context.Warnings;
        var plan = ModelFamilyConnectorParser.Parse(context.Args, RemoveArg, CreateArg, EditArg, problems);

        // Ã¢â€â‚¬Ã¢â€â‚¬ 1. Check everything against the family before changing it Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬
        var toRemove = new List<ConnectorElement>();
        for (var i = 0; i < plan.Remove.Count; i++)
            if (ModelFamilyLookup.Element<ConnectorElement>(doc, new JValue(plan.Remove[i]), $"{RemoveArg}[{i}]", "a connector", problems) is { } connector)
                toRemove.Add(connector);
        var removedIds = new HashSet<long>(toRemove.Select(c => c.Id.Value));
        foreach (var other in ModelFamilyConnectorRevit.All(doc))
        {
            var partner = ModelFamilyConnectorRevit.Linked(other);
            if (partner == null) continue;
            if (removedIds.Contains(other.Id.Value) && !removedIds.Contains(partner.Id.Value))
                warnings.Add($"Connector {partner.Id.Value} is linked to connector {other.Id.Value}, which is removed: it loses that link.");
            else if (!removedIds.Contains(other.Id.Value) && removedIds.Contains(partner.Id.Value))
                warnings.Add($"Connector {other.Id.Value} is linked to connector {partner.Id.Value}, which is removed: it loses that link.");
        }

        var hosts = new Dictionary<int, ModelFamilyConnectorHost>();
        foreach (var spec in plan.Create)
            if (ModelFamilyConnectorRevit.ResolveHost(doc, spec.Host, $"{spec.Source}.host", problems, warnings) is { } host)
                hosts[spec.Index] = host;

        var edits = new List<(ConnectorEditSpec Spec, ConnectorElement Connector, ConnectorDomain Domain, ConnectorSystemType? System)>();
        foreach (var spec in plan.Edit)
        {
            var connector = ModelFamilyLookup.Element<ConnectorElement>(doc, new JValue(spec.ConnectorId), $"{spec.Source}.connector", "a connector", problems);
            if (connector == null) continue;
            if (ModelFamilyConnectorRevit.Domain(connector) is not { } domain)
            {
                problems.Add($"{spec.Source}.connector: connector {spec.ConnectorId} has a domain this tool does not edit ({ModelFamilyConnectorRevit.DomainWireName(connector)}).");
                continue;
            }
            var system = ModelFamilyConnectorParser.ResolveSystemType(domain, spec.SystemType, spec.Source, problems, required: false);
            var current = ModelFamilyConnectorRevit.ShapeName(connector);
            var shape = ModelFamilyConnectorTables.CheckShapeAndSize(domain, spec.Shape, spec.Diameter, spec.Width, spec.Height, spec.Source, problems,
                current == "Invalid" ? null : current);
            if (spec.Shape != null) spec.Shape = shape;
            if (spec.Angle != null && domain == ConnectorDomain.Electrical) problems.Add($"{spec.Source}.angle: an electrical connector has no angle.");
            edits.Add((spec, connector, domain, system));
        }

        foreach (var spec in plan.Create.Concat<ConnectorSettings>(plan.Edit))
        {
            if (spec.LinkTo?.Id is { } linkId)
            {
                var target = ModelFamilyLookup.Element<ConnectorElement>(doc, new JValue(linkId), $"{spec.Source}.linkTo", "a connector", problems);
                var domain = spec is ConnectorCreateSpec create ? create.Domain : edits.FirstOrDefault(e => ReferenceEquals(e.Spec, spec)).Domain;
                if (target != null && ModelFamilyConnectorRevit.Domain(target) is { } targetDomain && targetDomain != domain)
                    problems.Add($"{spec.Source}.linkTo: connector {linkId} is {ModelFamilyConnectorTables.WireName(targetDomain)}; a connector links only to one of its own domain ({ModelFamilyConnectorTables.WireName(domain)}).");
            }
            else if (spec.LinkTo?.CreatedIndex is { } index && spec is ConnectorCreateSpec self && plan.Create.FirstOrDefault(c => c.Index == index) is { } other && other.Domain != self.Domain)
                problems.Add($"{spec.Source}.linkTo: connectors[{index}] is {ModelFamilyConnectorTables.WireName(other.Domain)}; a connector links only to one of its own domain.");
            foreach (var (name, family) in spec.Associations)
                if (family != null && ModelFamilyLookup.Parameter(doc, family) == null)
                {
                    var close = ModelFamilyJson.Suggest(family, ModelFamilyLookup.Parameters(doc).Select(p => p.Definition.Name));
                    problems.Add($"{spec.Source}.associations.{name}: the family has no parameter '{family}'" +
                                 (close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(n => $"'{n}'"))}?" : ". Create it first with revit_edit_model_family_parameters (#172, model-family-parameters.md)."));
                }
        }

        var category = doc.OwnerFamily?.FamilyCategory;
        if (category != null)
        {
            var builtIn = string.Empty;
            try { builtIn = ((BuiltInCategory)category.Id.Value).ToString(); }
            catch { /* a category without a built-in id */ }
            foreach (var domain in plan.Create.Select(c => c.Domain).Distinct())
                if (ModelFamilyConnectorTables.CategoryWarning(builtIn, category.Name, domain) is { } warning) warnings.Add(warning);
        }

        if (problems.Count > 0) return null;

        // Ã¢â€â‚¬Ã¢â€â‚¬ 2. Remove Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬
        if (toRemove.Count > 0)
            context.Transact("Remove connectors", () => { doc.Delete(toRemove.Select(c => c.Id).ToList()); });

        // Ã¢â€â‚¬Ã¢â€â‚¬ 3. Create Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬
        var created = new Dictionary<int, ConnectorElement>();
        foreach (var spec in plan.Create)
        {
            var host = hosts[spec.Index];
            ConnectorElement? connector = null;
            string? refused = null;
            context.Transact($"Create {spec.Source}", () =>
            {
                try { connector = ModelFamilyConnectorRevit.Create(doc, spec, host.Reference); }
                catch (Exception ex) when (ex is not OperationCanceledException) { refused = ex.Message.TrimEnd('.'); }
            });
            if (refused != null || connector == null || doc.GetElement(connector.Id) is not ConnectorElement)
            {
                problems.Add(host.IsPlane
                    ? $"{spec.Source}.host: Revit did not create {ModelFamilyConnectorTables.AConnector(spec.Domain)} on reference plane '{(string?)host.Describe["plane"]}'{(refused != null ? $" ({refused})" : string.Empty)}. Host it on a planar face of a form instead: {{\"element\": <form id>, \"face\": \"back\"}}; the face can be locked to that plane."
                    : $"{spec.Source}: Revit did not create the connector on {host.Describe.ToString(Newtonsoft.Json.Formatting.None)}{(refused != null ? $": {refused}" : string.Empty)}.");
                continue;
            }
            created[spec.Index] = connector;
        }
        if (problems.Count > 0) return null;

        // Ã¢â€â‚¬Ã¢â€â‚¬ 4. Settings of the new connectors, then the edits Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬
        var applied = new Dictionary<long, JObject>();
        foreach (var spec in plan.Create)
            applied[created[spec.Index].Id.Value] = Apply(context, created[spec.Index], spec, spec.Domain, spec.Name, null);
        foreach (var (spec, connector, domain, system) in edits)
            applied[connector.Id.Value] = Apply(context, connector, spec, domain, null, system);

        // Ã¢â€â‚¬Ã¢â€â‚¬ 5. Links, once every connector exists Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬
        foreach (var (spec, connector) in plan.Create.Select(c => ((ConnectorSettings)c, created[c.Index]))
                     .Concat(edits.Select(e => ((ConnectorSettings)e.Spec, e.Connector))))
        {
            if (!spec.LinkGiven) continue;
            var target = spec.LinkTo == null ? null
                : spec.LinkTo.CreatedIndex is { } index ? created[index]
                : doc.GetElement(new ElementId(spec.LinkTo.Id!.Value)) as ConnectorElement;
            string? error = null;
            context.Transact($"Link {spec.Source}", () =>
            {
                try { connector.SetLinkedConnectorElement(target); }
                catch (Exception ex) when (ex is not OperationCanceledException) { error = ex.Message.TrimEnd('.'); }
            });
            var now = ModelFamilyConnectorRevit.Linked(connector);
            if (error != null)
                problems.Add($"{spec.Source}.linkTo: Revit refused to {(target == null ? "remove the link" : $"link it to {spec.LinkTo}")}: {error}. Linked connectors must be different connectors of the same domain, of a kind that supports links.");
            else if (target == null ? now != null : now == null || now.Id != target.Id)
                problems.Add($"{spec.Source}.linkTo: Revit did not keep the link (linked connector is now {(now == null ? "none" : now.Id.Value.ToString())}).");
            applied[connector.Id.Value]["linkTo"] = now?.Id.Value;
        }
        if (problems.Count > 0) return null;

        // Ã¢â€â‚¬Ã¢â€â‚¬ 6. Read back Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬
        var index2 = new ModelFamilyConnectorRevit.HostIndex(doc);
        var createdRows = new JArray();
        foreach (var spec in plan.Create)
        {
            var connector = created[spec.Index];
            var row = ModelFamilyConnectorRevit.Row(doc, connector, index2);
            row.AddFirst(new JProperty("name", spec.Name.Length > 0 ? spec.Name : null));
            row.AddFirst(new JProperty("index", spec.Index));
            row["requestedHost"] = hosts[spec.Index].Describe;
            row["applied"] = applied[connector.Id.Value];
            createdRows.Add(row);
        }
        var editedRows = new JArray();
        foreach (var (spec, connector, _, _) in edits)
        {
            var row = ModelFamilyConnectorRevit.Row(doc, connector, index2);
            row["applied"] = applied[connector.Id.Value];
            editedRows.Add(row);
        }

        var summary = new List<string>();
        if (created.Count > 0) summary.Add($"{created.Count} connector(s) created");
        if (edits.Count > 0) summary.Add($"{edits.Count} edited");
        if (toRemove.Count > 0) summary.Add($"{toRemove.Count} removed");
        context.Summary = string.Join(", ", summary);
        return new JObject
        {
            ["removed"] = new JArray(removedIds),
            ["created"] = createdRows,
            ["edited"] = editedRows,
            ["note"] = "host is inferred from the connector's origin and direction (Revit does not expose a connector's host); requestedHost is what the call asked for."
        };
    }

    /// <summary>
    /// Applies the settings a created and an edited connector share, each step reading back what Revit stored. Bad
    /// values become problems (the whole edit is then rolled back); what Revit adjusts becomes a warning.
    /// </summary>
    private static JObject Apply(ModelFamilyContext context, ConnectorElement connector, ConnectorSettings spec, ConnectorDomain domain,
        string? label, ConnectorSystemType? system)
    {
        var doc = context.FamilyDoc;
        var problems = context.Problems;
        var warnings = context.Warnings;
        var source = spec.Source;
        var applied = new JObject();

        context.Transact($"Settings of {source}", () =>
        {
            if (!string.IsNullOrEmpty(label))
            {
                var description = connector.get_Parameter(BuiltInParameter.RBS_CONNECTOR_DESCRIPTION);
                if (description == null || description.IsReadOnly || !TrySet(() => description.Set(label)))
                    warnings.Add($"{source}.name: the connector's description could not be set; '{label}' is only echoed in the result.");
            }

            if (system != null)
            {
                var wanted = ModelFamilyConnectorRevit.ParseEnum<MEPSystemClassification>(system.Classification);
                bool valid;
                try { valid = connector.IsSystemClassificationValid(wanted); }
                catch { valid = false; }
                if (!valid) problems.Add($"{source}.systemType: Revit does not accept {system.WireName} for this connector; remove it and create a new one.");
                else
                {
                    try { connector.SystemClassification = wanted; }
                    catch (Exception ex) { problems.Add($"{source}.systemType: Revit refused {system.WireName}: {ex.Message.TrimEnd('.')}. Remove the connector and create it again."); }
                    if (ModelFamilyConnectorRevit.Classification(connector) != system.Classification)
                        problems.Add($"{source}.systemType: the connector is still {ModelFamilyConnectorTables.ClassificationWireName(ModelFamilyConnectorRevit.Classification(connector))}; remove it and create it again with systemType {system.WireName}.");
                    applied["systemType"] = ModelFamilyConnectorTables.ClassificationWireName(ModelFamilyConnectorRevit.Classification(connector));
                }
            }

            if (spec is ConnectorEditSpec && spec.Shape != null && ModelFamilyConnectorRevit.ShapeName(connector) != spec.Shape)
            {
                var shapeParameter = connector.get_Parameter(BuiltInParameter.CONNECTOR_PROFILE_TYPE);
                var wantedShape = ModelFamilyConnectorRevit.ParseEnum<ConnectorProfileType>(spec.Shape);
                if (shapeParameter == null || shapeParameter.IsReadOnly || !TrySet(() => shapeParameter.Set((int)wantedShape)) ||
                    ModelFamilyConnectorRevit.ShapeName(connector) != spec.Shape)
                    problems.Add($"{source}.shape: Revit did not change the shape to {spec.Shape.ToLowerInvariant()}; remove the connector and create it again with that shape.");
                else applied["shape"] = spec.Shape.ToLowerInvariant();
            }

            if (spec.Diameter is { } diameter)
            {
                var error = ModelFamilyConnectorRevit.SetLength(connector, BuiltInParameter.CONNECTOR_DIAMETER, diameter);
                if (error != null && ModelFamilyConnectorRevit.SetLength(connector, BuiltInParameter.CONNECTOR_RADIUS, diameter / 2) is { } radiusError)
                    problems.Add($"{source}.diameter: {error}; {radiusError}.");
            }
            if (spec.Width is { } width && ModelFamilyConnectorRevit.SetLength(connector, BuiltInParameter.CONNECTOR_WIDTH, width) is { } widthError)
                problems.Add($"{source}.width: {widthError}.");
            if (spec.Height is { } height && ModelFamilyConnectorRevit.SetLength(connector, BuiltInParameter.CONNECTOR_HEIGHT, height) is { } heightError)
                problems.Add($"{source}.height: {heightError}.");

            if (spec.Angle is { } angle)
            {
                var angleParameter = connector.get_Parameter(BuiltInParameter.CONNECTOR_ANGLE);
                if (angleParameter == null) problems.Add($"{source}.angle: this {ModelFamilyConnectorTables.WireName(domain)} connector has no angle parameter.");
                else
                {
                    var (row, error) = ModelFamilyConnectorRevit.SetValue(doc, angleParameter, new ConnectorValue { Number = angle });
                    if (error != null) problems.Add($"{source}.angle: {error}.");
                    else applied["angle"] = row;
                }
            }

            if (spec.Properties.Count > 0)
            {
                var rows = new JObject();
                foreach (var (name, value) in spec.Properties)
                {
                    var parameter = ModelFamilyAssociations.ElementParameter(connector, name);
                    if (parameter == null)
                    {
                        problems.Add($"{source}.properties.{name}: this {ModelFamilyConnectorTables.WireName(domain)} connector has no parameter '{name}'. Settable: {string.Join(", ", ModelFamilyConnectorRevit.SettableNames(connector))}.");
                        continue;
                    }
                    var (row, error) = ModelFamilyConnectorRevit.SetValue(doc, parameter, value);
                    if (error != null)
                    {
                        problems.Add($"{source}.properties.{name}: {error}." + (parameter.IsReadOnly ? $" Settable: {string.Join(", ", ModelFamilyConnectorRevit.SettableNames(connector))}." : string.Empty));
                        continue;
                    }
                    if (row!["matches"]?.Type == JTokenType.Boolean && !(bool)row["matches"]!)
                        warnings.Add($"{source}.properties.{name}: Revit stored {row["stored"]} {(string?)row["unit"]} for the requested {value.ToJson().ToString(Newtonsoft.Json.Formatting.None)}.");
                    rows[name] = row;
                }
                applied["properties"] = rows;
            }

            if (spec.Associations.Count > 0)
            {
                var rows = new JObject();
                foreach (var (name, familyName) in spec.Associations)
                {
                    var parameter = ModelFamilyAssociations.ElementParameter(connector, name);
                    if (parameter == null || !ModelFamilyAssociations.CanAssociate(doc, parameter))
                    {
                        problems.Add($"{source}.associations.{name}: this {ModelFamilyConnectorTables.WireName(domain)} connector has no parameter '{name}' that can be associated. Associable: {string.Join(", ", ModelFamilyConnectorRevit.AssociableNames(doc, connector))}.");
                        continue;
                    }
                    var family = familyName == null ? null : ModelFamilyLookup.Parameter(doc, familyName, $"{source}.associations.{name}", problems);
                    if (familyName != null && family == null) continue;
                    var error = ModelFamilyAssociations.Associate(doc, parameter, family);
                    if (error != null) problems.Add($"{source}.associations.{name}: {error}");
                    else rows[name] = family?.Definition.Name;
                }
                applied["associations"] = rows;
            }

            if (spec.Primary == true)
            {
                try { connector.AssignAsPrimary(); }
                catch (Exception ex) { problems.Add($"{source}.primary: Revit refused to make it the primary connector: {ex.Message.TrimEnd('.')}."); }
            }
            else if (spec.Primary == false && SafeIsPrimary(connector))
                problems.Add($"{source}.primary: a connector stops being primary only when another one of its domain is made primary; set primary=true on that one.");

            if (spec.Flip)
            {
                var before = connector.CoordinateSystem.BasisZ;
                try { connector.FlipDirection(); }
                catch (Exception ex) { problems.Add($"{source}.flip: Revit refused to flip the connector: {ex.Message.TrimEnd('.')}."); }
                applied["flip"] = new JObject { ["before"] = ModelFamilyLookup.Direction(before) };
            }
        });

        if (applied["flip"] is JObject flipped) flipped["after"] = ModelFamilyLookup.Direction(connector.CoordinateSystem.BasisZ);
        if (spec.Primary == true)
        {
            applied["primary"] = SafeIsPrimary(connector);
            if (!SafeIsPrimary(connector)) problems.Add($"{source}.primary: Revit did not make it the primary connector.");
        }

        if (spec.Position is { } position) applied["position"] = Move(context, connector, position, source);
        return applied;
    }

    /// <summary>
    /// Moves a connector within its face plane to the requested point (projected onto that plane) and reads the
    /// origin back. A connector Revit keeps in place (fixed by its host) is reported as a warning.
    /// </summary>
    private static JObject Move(ModelFamilyContext context, ConnectorElement connector, double[] positionMm, string source)
    {
        var doc = context.FamilyDoc;
        var origin = connector.Origin;
        var normal = connector.CoordinateSystem.BasisZ;
        var requested = ModelFamilyLookup.Xyz(positionMm);
        var (projected, distance) = ModelFamilyConnectorTables.ProjectOntoPlane(
            new[] { requested.X, requested.Y, requested.Z }, new[] { origin.X, origin.Y, origin.Z }, new[] { normal.X, normal.Y, normal.Z });
        var target = new XYZ(projected[0], projected[1], projected[2]);
        if (distance > 0.5 / ModelFamilyJson.MmPerFoot)
            context.Warnings.Add($"{source}.position: the point is {ModelFamilyJson.ToMm(distance)} mm off the connector's face; it was projected onto the face.");
        var delta = target - origin;
        string? error = null;
        if (delta.GetLength() > 1e-9)
            context.Transact($"Move {source}", () =>
            {
                try { ElementTransformUtils.MoveElement(doc, connector.Id, delta); }
                catch (Exception ex) when (ex is not OperationCanceledException) { error = ex.Message.TrimEnd('.'); }
            });
        var actual = connector.Origin;
        if (actual.DistanceTo(target) > 0.5 / ModelFamilyJson.MmPerFoot)
            context.Warnings.Add($"{source}.position: Revit kept the connector at {ModelFamilyLookup.Mm(actual).ToString(Newtonsoft.Json.Formatting.None)}" +
                                 $"{(error != null ? $" ({error})" : string.Empty)}. Constrain it instead: dimension it to the reference planes with revit_edit_model_family_references (model-family-references.md).");
        return new JObject
        {
            ["requested"] = new JArray(positionMm),
            ["projected"] = ModelFamilyLookup.Mm(target),
            ["actual"] = ModelFamilyLookup.Mm(actual)
        };
    }

    private static bool SafeIsPrimary(ConnectorElement connector)
    {
        try { return connector.IsPrimary; }
        catch { return false; }
    }

    private static bool TrySet(Func<bool> set)
    {
        try { return set(); }
        catch { return false; }
    }
}
