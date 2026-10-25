using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Checkpoints;

/// <summary>Reads Revit's own warnings (<c>Document.GetWarnings()</c>) as <see cref="WarningKey"/>s (#98). API thread only.</summary>
internal static class RevitWarningReader
{
    /// <param name="unreadable">Warnings that threw while being read.</param>
    public static List<WarningKey> Read(Document doc, out int unreadable)
    {
        unreadable = 0;
        var result = new List<WarningKey>();
        IList<FailureMessage> messages;
        try { messages = doc.GetWarnings(); }
        catch { return result; }

        foreach (var message in messages)
        {
            try
            {
                var ids = new SortedSet<long>();
                foreach (var id in message.GetFailingElements()) ids.Add(id.Value);
                try
                {
                    foreach (var id in message.GetAdditionalElements()) ids.Add(id.Value);
                }
                catch { }

                result.Add(new WarningKey
                {
                    DefinitionId = message.GetFailureDefinitionId().Guid.ToString(),
                    Description = message.GetDescriptionText() ?? string.Empty,
                    Severity = message.GetSeverity().ToString(),
                    ElementIds = ids.ToArray()
                });
            }
            catch
            {
                unreadable++;
            }
        }
        return result;
    }
}
