#nullable disable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Tagging
{
    /// <summary>Where a retag puts a managed tag's head.</summary>
    public enum RetagHeadPlacement
    {
        /// <summary>Leave the head where it is.</summary>
        KeepExisting,

        /// <summary>Put the head at the position computed from direction and offsets.</summary>
        Computed,

        /// <summary>Use the computed position, or the free spot nearest to it when it collides.</summary>
        NearestFreeToComputed
    }

    /// <summary>
    /// Pure decisions of revit_retag / revit_preview_retag (no Revit API, unit tested).
    /// A retag changes only what the caller asked for: leader and orientation stay as each tag has
    /// them unless given, and an explicit direction always wins over the tag's current head.
    /// </summary>
    public static class RetagDecision
    {
        /// <summary>
        /// True when the request carries a real value for <paramref name="key"/>: a null, a JSON null
        /// or a blank string counts as not given, so the tag keeps its own value.
        /// </summary>
        public static bool IsArgumentGiven(IDictionary<string, object> arguments, string key)
        {
            if (arguments == null || !arguments.TryGetValue(key, out var value) || value == null)
                return false;
            if (value is JValue json)
            {
                if (json.Type == JTokenType.Null || json.Type == JTokenType.Undefined)
                    return false;
                value = json.Value;
                if (value == null)
                    return false;
            }
            return !(value is string text) || !string.IsNullOrWhiteSpace(text);
        }

        /// <summary>Returns <paramref name="requested"/> when the caller gave it, else the tag's existing value.</summary>
        public static T KeepUnlessSpecified<T>(bool specified, T requested, T existing)
        {
            return specified ? requested : existing;
        }

        /// <summary>
        /// Leader end condition: the requested one applies only when it was given and the tag ends up
        /// with a leader; otherwise the tag keeps its own.
        /// </summary>
        public static T ResolveLeaderEndCondition<T>(
            bool resultingHasLeader,
            bool endConditionSpecified,
            T requested,
            T existing)
        {
            return resultingHasLeader && endConditionSpecified ? requested : existing;
        }

        /// <summary>
        /// Head placement. Without collision detection the head is normalized to the computed position
        /// (direction, default Right). With collision detection: an explicit direction is honoured, so
        /// the head goes to the computed position in that direction, or to the free spot nearest to it;
        /// with no direction given, a tag whose current head is collision-free keeps it, and only a
        /// colliding tag is moved to the free spot nearest to the computed position.
        /// </summary>
        public static RetagHeadPlacement ChooseHeadPlacement(
            bool collisionDetectionEnabled,
            bool directionSpecified,
            bool hasExistingHead,
            bool existingHeadCollisionFree)
        {
            if (!collisionDetectionEnabled)
                return RetagHeadPlacement.Computed;
            if (!directionSpecified && hasExistingHead && existingHeadCollisionFree)
                return RetagHeadPlacement.KeepExisting;
            return RetagHeadPlacement.NearestFreeToComputed;
        }

        /// <summary>
        /// True when the existing head must be collision-checked to decide placement; lets the caller
        /// skip that check when an explicit direction makes it irrelevant.
        /// </summary>
        public static bool NeedsExistingHeadCheck(
            bool collisionDetectionEnabled,
            bool directionSpecified,
            bool hasExistingHead)
        {
            return collisionDetectionEnabled && !directionSpecified && hasExistingHead;
        }
    }
}
