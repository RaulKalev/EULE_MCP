using System.Text.Json.Nodes;
using RevitMCP.Addin.Approval;
using RevitMCP.Addin.Dialogs;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>Worker mode: pre-approved document operations and Revit dialog auto-answers (docs/worker-mode.md).</summary>
public class WorkerPolicyTests
{
    private static WorkerPolicy Policy(string json) => WorkerPolicy.FromConfig(JsonNode.Parse(json) as JsonObject);

    private static readonly WorkerPolicy Full = Policy("""
        { "approval": { "worker": {
            "enabled": true,
            "operations": ["open", "sync", "close", "relinquish", "reloadLatest", "checkout"],
            "folders": ["P:\\Projektid", "\\\\server\\central\\"]
        } } }
        """);

    private static WorkerPolicy.Call Sync(string path, bool opened = true) => new()
    {
        ToolName = "revit_sync_with_central", Path = path, OpenedByConnector = opened
    };

    [Fact]
    public void MissingConfig_IsDisabled()
    {
        Assert.False(WorkerPolicy.FromConfig(null).IsActive);
        Assert.False(Policy("""{ "approval": { "directEditByDefault": true } }""").IsActive);
    }

    [Fact]
    public void Enabled_WithoutFolders_PreApprovesNothing()
    {
        var policy = Policy("""{ "approval": { "worker": { "enabled": true, "operations": ["open"] } } }""");
        Assert.False(policy.IsActive);
        Assert.Contains(policy.Problems, p => p.Contains("no folders"));
        Assert.False(policy.Covers(new WorkerPolicy.Call { ToolName = "revit_open_document", Path = @"P:\Projektid\a.rvt" }, out _));
    }

    [Fact]
    public void Disabled_FlagWins_EvenWithFolders()
    {
        var policy = Policy("""{ "approval": { "worker": { "enabled": false, "operations": ["open"], "folders": ["P:\\X"] } } }""");
        Assert.False(policy.IsActive);
    }

    [Fact]
    public void UnknownOperations_AndRelativeFolders_AreReported()
    {
        var policy = Policy("""{ "approval": { "worker": { "enabled": true, "operations": ["open", "delete"], "folders": ["Projektid", "P:\\Ok"] } } }""");
        Assert.Equal(new[] { "open" }, policy.Operations);
        Assert.Equal(new[] { @"P:\OK" }, policy.Folders);
        Assert.Equal(2, policy.Problems.Count);
    }

    [Fact]
    public void OperationNames_AreCaseInsensitive()
    {
        var policy = Policy("""{ "approval": { "worker": { "enabled": true, "operations": ["SYNC", "reloadlatest"], "folders": ["P:\\X"] } } }""");
        Assert.Equal(new[] { WorkerPolicy.OpSync, WorkerPolicy.OpReloadLatest }, policy.Operations);
    }

    [Fact]
    public void Open_InsideFolder_IsCovered()
    {
        Assert.True(Full.Covers(new WorkerPolicy.Call { ToolName = "revit_open_document", Path = @"p:/projektid/2403/EN.rvt" }, out var reason));
        Assert.Contains("worker mode", reason);
    }

    [Fact]
    public void Open_OutsideFolder_OrPrefixLookalike_IsNotCovered()
    {
        Assert.False(Full.Covers(new WorkerPolicy.Call { ToolName = "revit_open_document", Path = @"D:\Muu\EN.rvt" }, out _));
        Assert.False(Full.Covers(new WorkerPolicy.Call { ToolName = "revit_open_document", Path = @"P:\ProjektidVana\EN.rvt" }, out _));
        Assert.False(Full.Covers(new WorkerPolicy.Call { ToolName = "revit_open_document", Path = @"P:\Projektid\..\Muu\EN.rvt" }, out _));
    }

    [Fact]
    public void Open_WithActivate_IsNotCovered()
    {
        Assert.False(Full.Covers(new WorkerPolicy.Call { ToolName = "revit_open_document", Path = @"P:\Projektid\EN.rvt", Activate = true }, out var reason));
        Assert.Contains("activate", reason);
    }

    [Fact]
    public void Sync_NeedsADocumentTheConnectorOpened()
    {
        Assert.False(Full.Covers(Sync(@"C:\Users\raul\AppData\Local\RevitMCP\Locals\EN_raul_mcp.rvt"), out _));   // no central, outside
        var local = new WorkerPolicy.Call
        {
            ToolName = "revit_sync_with_central",
            Path = @"C:\Users\raul\AppData\Local\RevitMCP\Locals\EN_raul_mcp.rvt",
            CentralPath = @"\\server\central\EN.rvt",
            OpenedByConnector = true
        };
        Assert.True(Full.Covers(local, out _));                       // the local is outside, its central is inside
        Assert.False(Full.Covers(new WorkerPolicy.Call
        {
            ToolName = local.ToolName, Path = local.Path, CentralPath = local.CentralPath, OpenedByConnector = false
        }, out var reason));
        Assert.Contains("opened", reason);
    }

    [Fact]
    public void AllDocuments_IsNeverCovered()
    {
        Assert.False(Full.Covers(new WorkerPolicy.Call { ToolName = "revit_sync_with_central", AllDocuments = true }, out var reason));
        Assert.Contains("all=true", reason);
    }

    [Fact]
    public void Close_WithUnsavedChanges_NeedsSave()
    {
        var call = new WorkerPolicy.Call
        {
            ToolName = "revit_close_document", Path = @"P:\Projektid\EN.rvt", OpenedByConnector = true, IsModified = true
        };
        Assert.False(Full.Covers(call, out var reason));
        Assert.Contains("save=true", reason);

        Assert.True(Full.Covers(new WorkerPolicy.Call
        {
            ToolName = call.ToolName, Path = call.Path, OpenedByConnector = true, IsModified = true, Save = true
        }, out _));
        Assert.True(Full.Covers(new WorkerPolicy.Call
        {
            ToolName = call.ToolName, Path = call.Path, OpenedByConnector = true, IsModified = false
        }, out _));
    }

    [Fact]
    public void OperationNotListed_IsNotCovered_WithAReason()
    {
        var policy = Policy("""{ "approval": { "worker": { "enabled": true, "operations": ["open"], "folders": ["P:\\Projektid"] } } }""");
        Assert.False(policy.Covers(Sync(@"P:\Projektid\EN.rvt"), out var reason));
        Assert.Contains("sync", reason);
    }

    [Fact]
    public void OtherTools_AreNeverCovered_AndNeedNoReason()
    {
        Assert.False(Full.Covers(new WorkerPolicy.Call { ToolName = "revit_delete", Path = @"P:\Projektid\EN.rvt", OpenedByConnector = true }, out var reason));
        Assert.Null(reason);
        Assert.False(Full.Covers(new WorkerPolicy.Call { ToolName = "revit_checkpoint_rollback", Path = @"P:\Projektid\EN.rvt", OpenedByConnector = true }, out _));
    }

    [Fact]
    public void Registry_MatchesNormalizedPaths()
    {
        WorkerDocumentRegistry.Clear();
        WorkerDocumentRegistry.Add(@"c:/locals/EN_raul_mcp.rvt");
        Assert.True(WorkerDocumentRegistry.Contains(@"C:\Locals\EN_raul_mcp.rvt"));
        WorkerDocumentRegistry.Remove(@"C:\LOCALS\EN_RAUL_MCP.RVT");
        Assert.False(WorkerDocumentRegistry.Contains(@"C:\Locals\EN_raul_mcp.rvt"));
        Assert.False(WorkerDocumentRegistry.Contains(null));
    }
}

public class DialogRulesTests
{
    private static DialogRules Rules(string json) => DialogRules.FromConfig(JsonNode.Parse(json) as JsonObject);

    [Fact]
    public void NoConfig_ShowsEverything()
    {
        var rules = DialogRules.FromConfig(null);
        Assert.False(rules.AutoAnswer);
        Assert.Equal(DialogAction.Show, rules.Decide("TaskDialog_Unresolved_References", duringConnectorWork: true).Action);
    }

    [Fact]
    public void Defaults_AnswerMissingLinks_DuringConnectorWorkOnly()
    {
        var rules = Rules("""{ "dialogs": { "autoAnswer": true } }""");
        var during = rules.Decide("TaskDialog_Unresolved_References", duringConnectorWork: true);
        Assert.Equal(DialogAction.Answer, during.Action);
        Assert.Equal(DialogRules.CommandLink2, during.Result);
        Assert.Equal(DialogAction.Show, rules.Decide("TaskDialog_Unresolved_References", duringConnectorWork: false).Action);
    }

    [Fact]
    public void UnknownDialog_ShowsByDefault_OrCancelsWhenAsked()
    {
        Assert.Equal(DialogAction.Show, Rules("""{ "dialogs": { "autoAnswer": true } }""").Decide("X", true).Action);
        var cancel = Rules("""{ "dialogs": { "autoAnswer": true, "unknown": "cancel" } }""").Decide("X", true);
        Assert.Equal(DialogAction.Cancel, cancel.Action);
        Assert.Equal(DialogRules.Cancel, cancel.Result);
    }

    [Fact]
    public void ConfiguredRules_OverrideDefaults_AndShowDisablesOne()
    {
        var rules = Rules("""
            { "dialogs": { "autoAnswer": true, "unknown": "cancel", "rules": {
                "TaskDialog_Unresolved_References": "show",
                "TaskDialog_Some_Question": 6
            } } }
            """);
        Assert.Equal(DialogAction.Show, rules.Decide("TaskDialog_Unresolved_References", true).Action);
        var yes = rules.Decide("taskdialog_some_question", true);
        Assert.Equal(DialogAction.Answer, yes.Action);
        Assert.Equal(DialogRules.Yes, yes.Result);
    }

    [Fact]
    public void BadValues_AreReported()
    {
        var rules = Rules("""{ "dialogs": { "autoAnswer": true, "unknown": "maybe", "rules": { "A": -3, "B": "later" } } }""");
        Assert.False(rules.CancelUnknown);
        Assert.Equal(3, rules.Problems.Count);
        Assert.False(rules.Rules.ContainsKey("A"));
    }

    [Fact]
    public void ImportWarnings_AreClosedByMessage_EvenWithAnUnknownId()
    {
        var rules = Rules("""{ "dialogs": { "autoAnswer": true } }""");
        var lost = rules.Decide("TaskDialog_Whatever", "Some entities were lost during import.", true);
        Assert.Equal(DialogAction.Answer, lost.Action);
        Assert.Equal(DialogRules.Close, lost.Result);
        var range = rules.Decide(null, "Some numerical data within the imported file was out of range.  This numerical data has been truncated.", true);
        Assert.Equal(DialogRules.Close, range.Result);
    }

    [Fact]
    public void IdRule_WinsOverMessageRule_AndLongestMessageWins()
    {
        var rules = Rules("""
            { "dialogs": { "autoAnswer": true,
                "rules": { "Known_Id": 1 },
                "messageRules": { "lost": "show", "were lost during import": 2 } } }
            """);
        Assert.Equal(DialogRules.Ok, rules.Decide("Known_Id", "Some entities were lost during import.", true).Result);
        Assert.Equal(DialogRules.Cancel, rules.Decide("X", "Some entities were lost during import.", true).Result);
        Assert.Equal(DialogAction.Show, rules.Decide("X", "Something got lost", true).Action);
    }

    [Fact]
    public void ScopeAlways_AnswersTheUsersDialogs_ByRuleOnly()
    {
        var connector = Rules("""{ "dialogs": { "autoAnswer": true, "unknown": "cancel" } }""");
        Assert.Equal(DialogAction.Show, connector.Decide("X", "Some entities were lost during import.", false).Action);

        var always = Rules("""{ "dialogs": { "autoAnswer": true, "unknown": "cancel", "scope": "always" } }""");
        Assert.True(always.AlwaysScope);
        Assert.Equal(DialogAction.Answer, always.Decide("X", "Some entities were lost during import.", false).Action);
        Assert.Equal(DialogAction.Show, always.Decide("Other", "Save changes?", false).Action);   // never cancel the user's dialogs
        Assert.Equal(DialogAction.Cancel, always.Decide("Other", "Save changes?", true).Action);
    }

    [Fact]
    public void CloseFallsBackToOk()
    {
        Assert.Equal(new[] { DialogRules.Ok, DialogRules.Cancel }, DialogRules.Fallbacks(DialogRules.Close));
        Assert.Empty(DialogRules.Fallbacks(DialogRules.Yes));
    }

    [Fact]
    public void Journal_CollectsDialogsOfTheCurrentCallOnly()
    {
        DialogJournal.Reset();
        DialogJournal.Record(new DialogRecord { DialogId = "Before" });
        Assert.False(DialogJournal.InConnectorWork);
        using (DialogJournal.Enter("revit_open_document", "r1", "Claude"))
        {
            Assert.True(DialogJournal.InConnectorWork);
            Assert.Equal("revit_open_document", DialogJournal.Context.Tool);
            using (DialogJournal.Enter("nested", "r2", "x"))
                Assert.Equal("r1", DialogJournal.Context.RequestId);
            Assert.True(DialogJournal.InConnectorWork);
            DialogJournal.Record(new DialogRecord { DialogId = "During", Action = nameof(DialogAction.Answer), Result = 1002, Accepted = true });
            var current = DialogJournal.DrainCurrent();
            Assert.Single(current);
            Assert.Contains("command link 2", current[0].ToWarning());
        }
        Assert.False(DialogJournal.InConnectorWork);
        Assert.Equal(new[] { "During", "Before" }, DialogJournal.Recent(10).Select(d => d.DialogId));
    }
}
