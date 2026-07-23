#nullable disable

using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Tagging
{
    public enum TagPlacementDirection
    {
        Right,
        Left,
        Up,
        Down
    }

    public enum TagAnchorPoint
    {
        Center,
        TopLeft,
        TopCenter,
        TopRight,
        LeftCenter,
        RightCenter,
        BottomLeft,
        BottomCenter,
        BottomRight
    }

    public sealed class SmartTagOptions
    {
        public TagPlacementDirection Direction { get; set; } = TagPlacementDirection.Right;
        public TagAnchorPoint AnchorPoint { get; set; } = TagAnchorPoint.Center;
        public bool HasLeader { get; set; }
        public bool HasLeaderSpecified { get; set; }
        public double AttachedLengthMillimeters { get; set; }
        public double FreeLengthMillimeters { get; set; }
        public TagOrientation Orientation { get; set; } = TagOrientation.Horizontal;
        public bool OrientationSpecified { get; set; }
        public double RotationRadians { get; set; }
        public bool DetectElementRotation { get; set; }
        public bool EnableCollisionDetection { get; set; } = true;
        public double CollisionGapMillimeters { get; set; } = 1.0;
        public double MinimumOffsetMillimeters { get; set; } = 300.0;
        public LeaderEndCondition LeaderEndCondition { get; set; } = LeaderEndCondition.Attached;
        public bool LeaderEndConditionSpecified { get; set; }
        public bool SkipAlreadyTagged { get; set; } = true;
    }

    public sealed class DirectionTagTypes
    {
        public ElementId LeftTagTypeId { get; set; } = ElementId.InvalidElementId;
        public ElementId RightTagTypeId { get; set; } = ElementId.InvalidElementId;
        public ElementId UpTagTypeId { get; set; } = ElementId.InvalidElementId;
        public ElementId DownTagTypeId { get; set; } = ElementId.InvalidElementId;

        public ElementId Resolve(TagPlacementDirection direction, ElementId defaultTagTypeId)
        {
            ElementId resolved;
            switch (direction)
            {
                case TagPlacementDirection.Left:
                    resolved = LeftTagTypeId;
                    break;
                case TagPlacementDirection.Up:
                    resolved = UpTagTypeId;
                    break;
                case TagPlacementDirection.Down:
                    resolved = DownTagTypeId;
                    break;
                default:
                    resolved = RightTagTypeId;
                    break;
            }

            return resolved == null || resolved == ElementId.InvalidElementId
                ? defaultTagTypeId
                : resolved;
        }
    }

    public sealed class SmartTagPlacementResult
    {
        public int CandidateCount { get; set; }
        public int PlacedCount { get; set; }
        public int SkippedAlreadyTaggedCount { get; set; }
        public int CollisionFallbackCount { get; set; }
        public string CollisionDiagnostics { get; set; } = string.Empty;
        public List<SmartTagPlacementItem> Items { get; } = new List<SmartTagPlacementItem>();
        public List<string> Errors { get; } = new List<string>();
    }

    public sealed class SmartTagPlacementItem
    {
        public long ElementId { get; set; }
        public long TagId { get; set; }
        public long TagTypeId { get; set; }
        public string TagTypeName { get; set; } = string.Empty;
        public string CategoryName { get; set; } = string.Empty;
        public bool WouldPlace { get; set; }
        public bool CollisionFree { get; set; }
        public double HeadX { get; set; }
        public double HeadY { get; set; }
        public double HeadZ { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    public sealed class TagStateSnapshot
    {
        public XYZ TagHeadPosition { get; set; }
        public bool HasLeader { get; set; }
        public LeaderEndCondition LeaderEndCondition { get; set; }
        public TagOrientation Orientation { get; set; }

        public TagStateSnapshot()
        {
        }

        public TagStateSnapshot(IndependentTag tag)
        {
            if (tag == null)
                return;

            TagHeadPosition = tag.TagHeadPosition;
            HasLeader = tag.HasLeader;
            try { Orientation = tag.TagOrientation; }
            catch { Orientation = TagOrientation.Horizontal; }
            try { LeaderEndCondition = tag.LeaderEndCondition; }
            catch { LeaderEndCondition = 0; }
        }

        public void ApplyTo(IndependentTag tag)
        {
            if (tag == null)
                return;

            if (TagHeadPosition != null)
            {
                try { tag.TagHeadPosition = TagHeadPosition; }
                catch { }
            }

            try { tag.HasLeader = HasLeader; }
            catch { }
            if (HasLeader)
            {
                try { tag.LeaderEndCondition = LeaderEndCondition; }
                catch { }
            }
            try { tag.TagOrientation = Orientation; }
            catch { }
        }
    }

    public sealed class TagAdjustmentProposal
    {
        public ElementId TagId { get; set; }
        public ElementId ReferencedElementId { get; set; }
        public TagStateSnapshot OldState { get; set; }
        public TagStateSnapshot NewState { get; set; }
        public string Reason { get; set; } = string.Empty;

        public bool IsSignificantChange(double toleranceFeet = 0.00164)
        {
            if (OldState == null || NewState == null)
                return false;

            if (OldState.TagHeadPosition != null && NewState.TagHeadPosition != null &&
                (NewState.TagHeadPosition - OldState.TagHeadPosition).GetLength() > toleranceFeet)
                return true;

            return OldState.HasLeader != NewState.HasLeader ||
                   OldState.Orientation != NewState.Orientation ||
                   (NewState.HasLeader && OldState.LeaderEndCondition != NewState.LeaderEndCondition);
        }
    }
}
