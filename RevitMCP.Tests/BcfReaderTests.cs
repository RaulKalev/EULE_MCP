using System.IO;
using System.IO.Compression;
using System.Text;
using RevitMCP.Addin.Coordination.Bcf;
using Xunit;

namespace RevitMCP.Tests;

public class BcfReaderTests
{
    private const string Topic1 = "a1b2c3d4-0000-0000-0000-000000000001";
    private const string Topic2 = "a1b2c3d4-0000-0000-0000-000000000002";

    private static Stream Zip(params (string Name, string Content)[] files)
    {
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                var entry = zip.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }
        buffer.Position = 0;
        return buffer;
    }

    private const string Markup21 = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Markup xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
          <Header><File IfcProject="0001" isExternal="true"><Filename>EL.ifc</Filename></File></Header>
          <Topic Guid="a1b2c3d4-0000-0000-0000-000000000002" TopicType="Clash" TopicStatus="Active">
            <Title>Cable tray vs beam</Title>
            <Priority>High</Priority>
            <Index>7</Index>
            <Labels>EL</Labels>
            <Labels>Level 2</Labels>
            <CreationDate>2026-10-01T10:00:00Z</CreationDate>
            <CreationAuthor>coord@example.com</CreationAuthor>
            <AssignedTo>el@example.com</AssignedTo>
            <Description>Tray runs through the beam</Description>
          </Topic>
          <Comment Guid="c-1">
            <Date>2026-10-02T10:00:00Z</Date>
            <Author>coord@example.com</Author>
            <Comment>Please lower the tray</Comment>
            <Viewpoint Guid="v-1"/>
          </Comment>
          <Viewpoints Guid="v-1">
            <Viewpoint>viewpoint.bcfv</Viewpoint>
            <Snapshot>snapshot.png</Snapshot>
          </Viewpoints>
        </Markup>
        """;

    private const string Viewpoint21 = """
        <?xml version="1.0" encoding="UTF-8"?>
        <VisualizationInfo Guid="v-1">
          <Components>
            <Selection>
              <Component IfcGuid="3$$$$$$$$$$$$$$$$$$$$$">
                <OriginatingSystem>Autodesk Revit 2024 (ENU)</OriginatingSystem>
                <AuthoringToolId>123456</AuthoringToolId>
              </Component>
              <Component IfcGuid="0000000000000000000001" />
            </Selection>
            <Visibility DefaultVisibility="true">
              <Exceptions><Component IfcGuid="0000000000000000000002" /></Exceptions>
            </Visibility>
            <Coloring>
              <Color Color="FFFF0000"><Component IfcGuid="3$$$$$$$$$$$$$$$$$$$$$" /></Color>
            </Coloring>
          </Components>
          <PerspectiveCamera>
            <CameraViewPoint><X>1.5</X><Y>2</Y><Z>3</Z></CameraViewPoint>
            <CameraDirection><X>0</X><Y>1</Y><Z>0</Z></CameraDirection>
            <CameraUpVector><X>0</X><Y>0</Y><Z>1</Z></CameraUpVector>
            <FieldOfView>60</FieldOfView>
          </PerspectiveCamera>
          <ClippingPlanes>
            <ClippingPlane>
              <Location><X>0</X><Y>0</Y><Z>4.2</Z></Location>
              <Direction><X>0</X><Y>0</Y><Z>1</Z></Direction>
            </ClippingPlane>
          </ClippingPlanes>
        </VisualizationInfo>
        """;

    private const string Markup30 = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Markup xmlns="http://www.buildingsmart-tech.org/bcf/markup/3.0">
          <Topic Guid="a1b2c3d4-0000-0000-0000-000000000001" TopicType="Clash" TopicStatus="Open" ServerAssignedId="3">
            <Title>Conduit vs duct</Title>
            <Labels><Label>EL</Label><Label>HVAC</Label></Labels>
            <CreationDate>2026-10-01T09:00:00Z</CreationDate>
            <Comments>
              <Comment Guid="c-2"><Date>2026-10-03T08:00:00Z</Date><Author>a</Author><Comment>First</Comment></Comment>
            </Comments>
            <Viewpoints>
              <ViewPoint Guid="v-2"><Viewpoint>vp.bcfv</Viewpoint><Snapshot>vp.png</Snapshot></ViewPoint>
            </Viewpoints>
          </Topic>
        </Markup>
        """;

    private const string Viewpoint30 = """
        <?xml version="1.0" encoding="UTF-8"?>
        <VisualizationInfo xmlns="http://www.buildingsmart-tech.org/bcf/visinfo/3.0" Guid="v-2">
          <Components>
            <Visibility DefaultVisibility="false">
              <Exceptions>
                <Component IfcGuid="0000000000000000000003" />
                <Component IfcGuid="0000000000000000000004" />
              </Exceptions>
            </Visibility>
          </Components>
          <OrthogonalCamera>
            <CameraViewPoint><X>0</X><Y>0</Y><Z>10</Z></CameraViewPoint>
            <CameraDirection><X>0</X><Y>0</Y><Z>-1</Z></CameraDirection>
            <CameraUpVector><X>0</X><Y>1</Y><Z>0</Z></CameraUpVector>
            <ViewToWorldScale>12.5</ViewToWorldScale>
          </OrthogonalCamera>
        </VisualizationInfo>
        """;

    private static BcfFile ReadSample() => BcfReader.Read(Zip(
        ("bcf.version", """<?xml version="1.0"?><Version VersionId="2.1"><DetailedVersion>2.1</DetailedVersion></Version>"""),
        ("project.bcfp", """<?xml version="1.0"?><ProjectExtension><Project ProjectId="p"><Name>School</Name></Project></ProjectExtension>"""),
        ($"{Topic2}/markup.bcf", Markup21),
        ($"{Topic2}/viewpoint.bcfv", Viewpoint21),
        ($"{Topic2}/snapshot.png", "PNGDATA"),
        ($"{Topic1}/markup.bcf", Markup30),
        ($"{Topic1}/vp.bcfv", Viewpoint30)));

    [Fact]
    public void Read_HeaderAndTopicOrder()
    {
        var file = ReadSample();
        Assert.Equal("2.1", file.Version);
        Assert.Equal("School", file.ProjectName);
        Assert.Equal(2, file.Topics.Count);
        // Ordered by Index: ServerAssignedId 3 before Index 7.
        Assert.Equal(Topic1, file.Topics[0].Guid);
        Assert.Equal(1, file.Topics[0].Number);
        Assert.Equal(Topic2, file.Topics[1].Guid);
        Assert.Equal(2, file.Topics[1].Number);
        Assert.Empty(file.Warnings);
    }

    [Fact]
    public void Read_Bcf21Topic()
    {
        var topic = ReadSample().Topics.Single(t => t.Guid == Topic2);
        Assert.Equal("Cable tray vs beam", topic.Title);
        Assert.Equal("Clash", topic.TopicType);
        Assert.Equal("Active", topic.TopicStatus);
        Assert.Equal("High", topic.Priority);
        Assert.Equal(7, topic.Index);
        Assert.Equal(new[] { "EL", "Level 2" }, topic.Labels);
        Assert.Equal("el@example.com", topic.AssignedTo);
        Assert.Equal(new[] { "EL.ifc" }, topic.Files);

        var comment = Assert.Single(topic.Comments);
        Assert.Equal("Please lower the tray", comment.Text);
        Assert.Equal("v-1", comment.ViewpointGuid);

        var viewpoint = Assert.Single(topic.Viewpoints);
        Assert.Equal("v-1", viewpoint.Guid);
        Assert.Equal("snapshot.png", viewpoint.SnapshotFileName);
        Assert.Equal(7, viewpoint.SnapshotBytes);
        Assert.Equal(2, viewpoint.Selection.Count);
        Assert.Equal("123456", viewpoint.Selection[0].AuthoringToolId);
        Assert.Equal("Autodesk Revit 2024 (ENU)", viewpoint.Selection[0].OriginatingSystem);
        Assert.True(viewpoint.DefaultVisibility);
        Assert.Single(viewpoint.VisibilityExceptions);
        Assert.Equal("FFFF0000", Assert.Single(viewpoint.Colored).Color);
        Assert.Equal("perspective", viewpoint.Camera!.Kind);
        Assert.Equal(1.5, viewpoint.Camera.ViewPoint.X);
        Assert.Equal(60, viewpoint.Camera.FieldOfView);
        Assert.Equal(4.2, Assert.Single(viewpoint.ClippingPlanes).Location.Z);
    }

    [Fact]
    public void Read_Bcf30Topic()
    {
        var topic = ReadSample().Topics.Single(t => t.Guid == Topic1);
        Assert.Equal(3, topic.Index);
        Assert.Equal(new[] { "EL", "HVAC" }, topic.Labels);
        Assert.Equal("First", Assert.Single(topic.Comments).Text);
        var viewpoint = Assert.Single(topic.Viewpoints);
        Assert.Equal("vp.bcfv", viewpoint.FileName);
        Assert.Null(viewpoint.SnapshotFileName); // listed but not in the zip
        Assert.False(viewpoint.DefaultVisibility);
        Assert.Equal(2, viewpoint.VisibilityExceptions.Count);
        Assert.Equal("orthogonal", viewpoint.Camera!.Kind);
        Assert.Equal(12.5, viewpoint.Camera.ViewToWorldScale);
    }

    [Fact]
    public void Read_Bcf20ComponentsWithoutSelection()
    {
        var file = BcfReader.Read(Zip(
            ($"{Topic1}/markup.bcf", $"""<Markup><Topic Guid="{Topic1}"><Title>Old</Title></Topic></Markup>"""),
            ($"{Topic1}/viewpoint.bcfv", """
                <VisualizationInfo><Components>
                  <Component IfcGuid="0000000000000000000005" Selected="true" Visible="true"><AuthoringToolId>42</AuthoringToolId></Component>
                  <Component IfcGuid="0000000000000000000006" Selected="false" Visible="false" />
                </Components></VisualizationInfo>
                """)));
        var viewpoint = Assert.Single(Assert.Single(file.Topics).Viewpoints);
        Assert.Equal("42", Assert.Single(viewpoint.Selection).AuthoringToolId);
        Assert.Equal("0000000000000000000006", Assert.Single(viewpoint.VisibilityExceptions).IfcGuid);
    }

    [Fact]
    public void Read_BadTopicIsSkippedWithAWarning()
    {
        var file = BcfReader.Read(Zip(
            ($"{Topic1}/markup.bcf", "not xml"),
            ($"{Topic2}/markup.bcf", Markup21)));
        Assert.Single(file.Topics);
        Assert.Contains(file.Warnings, w => w.Contains(Topic1));
        Assert.Contains(file.Warnings, w => w.Contains("viewpoint.bcfv"));
    }

    [Fact]
    public void Read_NotABcf()
    {
        var file = BcfReader.Read(Zip(("readme.txt", "hello")));
        Assert.Empty(file.Topics);
        Assert.Contains(file.Warnings, w => w.Contains("not a BCF"));
    }

    [Fact]
    public void ClashComponents_PreferSelectionThenVisibleThenColored()
    {
        var file = ReadSample();
        var (selected, source) = BcfClashComponents.Of(file.Topics.Single(t => t.Guid == Topic2));
        Assert.Equal("selection", source);
        Assert.Equal(2, selected.Count);

        var (visible, visibleSource) = BcfClashComponents.Of(file.Topics.Single(t => t.Guid == Topic1));
        Assert.Equal("visible", visibleSource);
        Assert.Equal(2, visible.Count);

        var empty = new BcfTopic();
        Assert.Equal("none", BcfClashComponents.Of(empty).Source);
    }

    [Fact]
    public void IfcGuid_KnownValues()
    {
        Assert.Equal("0000000000000000000000", IfcGuid.FromGuid(Guid.Empty));
        Assert.Equal("3$$$$$$$$$$$$$$$$$$$$$", IfcGuid.FromGuid(new Guid("ffffffff-ffff-ffff-ffff-ffffffffffff")));
        // The last hex digit is the last 4 bits of the last character.
        Assert.Equal("0000000000000000000001", IfcGuid.FromGuid(new Guid("00000000-0000-0000-0000-000000000001")));
        // The top byte goes to the first two characters: 0x40 = 01 000000.
        Assert.Equal("1000000000000000000000", IfcGuid.FromGuid(new Guid("40000000-0000-0000-0000-000000000000")));
    }

    [Fact]
    public void IfcGuid_RoundTrips()
    {
        for (var i = 0; i < 200; i++)
        {
            var guid = Guid.NewGuid();
            var compressed = IfcGuid.FromGuid(guid);
            Assert.Equal(22, compressed.Length);
            Assert.True(IfcGuid.TryToGuid(compressed, out var back));
            Assert.Equal(guid, back);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("000000000000000000000")]
    [InlineData("4000000000000000000000")]
    [InlineData("00000000000000000000-0")]
    public void IfcGuid_RejectsMalformed(string? value) => Assert.False(IfcGuid.IsValid(value));
}
