using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;

namespace RevitMCP.Addin.Compat
{
    /// <summary>
    /// Isolates Selection.GetReferences/SetReferences, which were introduced in Revit 2023.
    /// Keeping the calls behind reflection preserves a compile-time backport path for Revit
    /// 2021/2022 while returning an explicit unsupported-version error at runtime.
    /// </summary>
    internal static class SelectionReferenceCompat
    {
        private static readonly MethodInfo? GetReferencesMethod =
            typeof(Selection).GetMethod("GetReferences", Type.EmptyTypes);

        private static readonly MethodInfo? SetReferencesMethod =
            typeof(Selection).GetMethod("SetReferences", new[] { typeof(IList<Reference>) });

        public static bool IsSupported
        {
            get { return GetReferencesMethod != null && SetReferencesMethod != null; }
        }

        public static bool TryGetReferences(
            Selection selection, out List<Reference> references, out string error)
        {
            references = new List<Reference>();
            error = string.Empty;

            if (GetReferencesMethod == null)
            {
                error = "Linked-element selection references require Revit 2023 or newer.";
                return false;
            }

            try
            {
                var result = GetReferencesMethod.Invoke(selection, null) as IEnumerable<Reference>;
                if (result != null)
                    references.AddRange(result);
                return true;
            }
            catch (Exception ex)
            {
                error = "Could not read selection references: " + Unwrap(ex).Message;
                return false;
            }
        }

        public static bool TrySetReferences(
            Selection selection, IList<Reference> references, out string error)
        {
            error = string.Empty;

            if (SetReferencesMethod == null)
            {
                error = "Selecting elements inside linked models requires Revit 2023 or newer.";
                return false;
            }

            try
            {
                SetReferencesMethod.Invoke(selection, new object[] { references });
                return true;
            }
            catch (Exception ex)
            {
                error = "Could not set linked-element selection: " + Unwrap(ex).Message;
                return false;
            }
        }

        private static Exception Unwrap(Exception exception)
        {
            var invocationException = exception as TargetInvocationException;
            return invocationException != null && invocationException.InnerException != null
                ? invocationException.InnerException
                : exception;
        }
    }
}
