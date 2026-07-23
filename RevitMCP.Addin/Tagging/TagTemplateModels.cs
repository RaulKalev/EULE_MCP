#nullable disable

using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Tagging
{
    public sealed class HostLocalFrame
    {
        public XYZ Anchor { get; set; }
        public XYZ Right { get; set; }
        public XYZ Front { get; set; }
        public XYZ ViewNormal { get; set; }
        public double RotationRadians { get; set; }
        public string OrientationSource { get; set; } = string.Empty;
        public string AnchorSource { get; set; } = string.Empty;
        public bool UsedFallback { get; set; }
        public bool FacingFlipped { get; set; }
        public bool HandFlipped { get; set; }
        public bool Mirrored { get; set; }
    }

    public sealed class TagPlacementTemplate
    {
        public long SourceTagId { get; set; }
        public long SourceHostElementId { get; set; }
        public long SourceViewId { get; set; }
        public long TagTypeId { get; set; }
        public string TagFamilyName { get; set; } = string.Empty;
        public string TagTypeName { get; set; } = string.Empty;
        public long HostCategoryId { get; set; }
        public long HostFamilyId { get; set; }
        public long HostTypeId { get; set; }
        public string HostCategoryName { get; set; } = string.Empty;
        public string HostFamilyName { get; set; } = string.Empty;
        public string HostTypeName { get; set; } = string.Empty;
        public HostAnchorMode AnchorMode { get; set; }
        public string AnchorSource { get; set; } = string.Empty;
        public string OrientationSource { get; set; } = string.Empty;
        public bool OrientationFallbackUsed { get; set; }
        public bool SourceFacingFlipped { get; set; }
        public bool SourceHandFlipped { get; set; }
        public bool SourceMirrored { get; set; }
        public double LocalRightOffsetMillimeters { get; set; }
        public double LocalFrontOffsetMillimeters { get; set; }
        public PlacementSide PlacementSide { get; set; }
        public double DistanceFromAnchorMillimeters { get; set; }
        public TagRotationMode RotationMode { get; set; }
        public double SourceHostRotationDegrees { get; set; }
        public double SourceTagRotationDegrees { get; set; }
        public double RelativeRotationDegrees { get; set; }
        public TagOrientation Orientation { get; set; }
        public bool HasLeader { get; set; }
        public LeaderEndCondition LeaderEndCondition { get; set; }
        public bool HasLeaderElbow { get; set; }
        public double LeaderElbowLocalRightOffsetMillimeters { get; set; }
        public double LeaderElbowLocalFrontOffsetMillimeters { get; set; }
        public bool HasFreeLeaderEnd { get; set; }
        public double LeaderEndLocalRightOffsetMillimeters { get; set; }
        public double LeaderEndLocalFrontOffsetMillimeters { get; set; }
    }

    public sealed class TagTemplateOverride
    {
        public long ExpectedSourceTagId { get; set; }
        public long ExpectedSourceHostElementId { get; set; }
        public long ExpectedSourceViewId { get; set; }
        public long ExpectedTagTypeId { get; set; }
        public bool HasAnchorMode { get; set; }
        public HostAnchorMode AnchorMode { get; set; }
        public bool HasLocalRightOffset { get; set; }
        public double LocalRightOffsetMillimeters { get; set; }
        public bool HasLocalFrontOffset { get; set; }
        public double LocalFrontOffsetMillimeters { get; set; }
        public bool HasRotationMode { get; set; }
        public TagRotationMode RotationMode { get; set; }
        public bool HasRelativeRotation { get; set; }
        public double RelativeRotationDegrees { get; set; }
        public bool HasOrientation { get; set; }
        public TagOrientation Orientation { get; set; }
        public bool HasLeader { get; set; }
        public bool LeaderValue { get; set; }
    }

    public sealed class TagTemplateRequestOptions
    {
        public long SourceTagId { get; set; }
        public TagTemplateScopeMode ScopeMode { get; set; } =
            TagTemplateScopeMode.SameFamily;
        public List<long> ExplicitElementIds { get; } = new List<long>();
        public HostAnchorMode AnchorMode { get; set; } =
            HostAnchorMode.SmartTagCenter;
        public bool IncludeSourceHost { get; set; }
        public bool SkipAlreadyTagged { get; set; } = true;
        public bool ReplaceExistingTags { get; set; }
        public bool IncludeAllHostTypes { get; set; } = true;
        public bool EnableCollisionDetection { get; set; }
        public double CollisionGapMillimeters { get; set; } = 1.0;
        public double MinimumOffsetMillimeters { get; set; }
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 100;
        public TagTemplateOverride Override { get; set; }
    }

    public sealed class TagTemplateTargetItem
    {
        public long HostElementId { get; set; }
        public string CategoryName { get; set; } = string.Empty;
        public string FamilyName { get; set; } = string.Empty;
        public string TypeName { get; set; } = string.Empty;
        public bool Eligible { get; set; }
        public bool AlreadyTagged { get; set; }
        public bool CollisionAdjusted { get; set; }
        public bool CollisionFree { get; set; } = true;
        public string HostOrientationSource { get; set; } = string.Empty;
        public bool OrientationFallbackUsed { get; set; }
        public bool FacingFlipped { get; set; }
        public bool HandFlipped { get; set; }
        public bool Mirrored { get; set; }
        public long CreatedTagId { get; set; }
        public string Status { get; set; } = "Skipped";
        public string Reason { get; set; } = string.Empty;
        public List<long> ExistingTagIds { get; } = new List<long>();
        public double ProposedHeadX { get; set; }
        public double ProposedHeadY { get; set; }
        public double ProposedHeadZ { get; set; }
        internal Element HostElement { get; set; }
        internal XYZ ProposedHead { get; set; }
        internal HostLocalFrame HostFrame { get; set; }
    }

    public sealed class TagTemplateAnalysisResult
    {
        public TagPlacementTemplate Template { get; set; }
        public int CandidateCount { get; set; }
        public int EligibleCount { get; set; }
        public int AlreadyTaggedCount { get; set; }
        public int SkippedCount { get; set; }
        public int UnsupportedCount { get; set; }
        public List<TagTemplateTargetItem> Targets { get; } =
            new List<TagTemplateTargetItem>();
        public List<string> Warnings { get; } = new List<string>();
        public List<string> Errors { get; } = new List<string>();
        internal IndependentTag SourceTag { get; set; }
        internal FamilyInstance SourceHost { get; set; }
        internal View SourceView { get; set; }
        internal Reference SourceReference { get; set; }
        internal FamilySymbol TagType { get; set; }
    }

    public sealed class TagTemplatePlacementResult
    {
        public int CreatedCount { get; set; }
        public int SkippedCount { get; set; }
        public int FailedCount { get; set; }
        public int CollisionAdjustedCount { get; set; }
        public string CollisionDiagnostics { get; set; } = string.Empty;
        public List<TagTemplateTargetItem> Items { get; } =
            new List<TagTemplateTargetItem>();
        public List<string> Errors { get; } = new List<string>();
    }
}
