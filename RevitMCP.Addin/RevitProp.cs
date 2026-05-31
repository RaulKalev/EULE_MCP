using System;

namespace RevitMCP.Addin;

/// <summary>
/// Helpers for reading Revit API properties that can throw on certain element/system types.
/// Centralizes the "best-effort read, fall back to default" pattern that is otherwise repeated
/// as inline <c>try { x = expr; } catch { }</c> blocks across the DTO builders.
/// </summary>
public static class RevitProp
{
    /// <summary>
    /// Evaluates <paramref name="read"/> and returns its value, or <paramref name="fallback"/>
    /// if the Revit API throws. Behaviour matches the previous inline try/catch reads — the
    /// exception is swallowed and the supplied default is returned.
    /// </summary>
    public static T TryRead<T>(Func<T> read, T fallback = default!)
    {
        try { return read(); }
        catch { return fallback; }
    }
}
