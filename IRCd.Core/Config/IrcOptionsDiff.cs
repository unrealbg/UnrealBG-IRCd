namespace IRCd.Core.Config;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

public static class IrcOptionsDiff
{
    public static IReadOnlyList<string> Diff(object? oldObj, object? newObj, int maxChanges = 100)
    {
        var changes = new List<string>();
        DiffInto(changes, prefix: string.Empty, oldObj, newObj, maxChanges);
        return changes;
    }

    private static void DiffInto(List<string> changes, string prefix, object? oldObj, object? newObj, int maxChanges)
    {
        if (changes.Count >= maxChanges)
            return;

        if (ReferenceEquals(oldObj, newObj))
            return;

        if (oldObj is null || newObj is null)
        {
            changes.Add($"{prefix}: {(oldObj is null ? "<null>" : "<set>")} -> {(newObj is null ? "<null>" : "<set>")}");
            return;
        }

        var t = oldObj.GetType();
        if (t != newObj.GetType())
        {
            changes.Add($"{prefix}: type {t.Name} -> {newObj.GetType().Name}");
            return;
        }

        if (IsSimple(t))
        {
            if (!Equals(oldObj, newObj))
                changes.Add($"{prefix}: {FormatValue(oldObj)} -> {FormatValue(newObj)}");
            return;
        }

        if (oldObj is IEnumerable oldEnum && newObj is IEnumerable newEnum && t != typeof(string))
        {
            var a = string.Join(",", EnumerateForDiff(oldEnum));
            var b = string.Join(",", EnumerateForDiff(newEnum));
            if (!string.Equals(a, b, StringComparison.Ordinal))
                changes.Add($"{prefix}: [{a}] -> [{b}]");
            return;
        }

        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead) continue;
            if (p.GetIndexParameters().Length != 0) continue;

            var name = string.IsNullOrEmpty(prefix) ? p.Name : prefix + "." + p.Name;

            object? oldV;
            object? newV;
            try
            {
                oldV = p.GetValue(oldObj);
                newV = p.GetValue(newObj);
            }
            catch
            {
                continue;
            }

            DiffInto(changes, name, oldV, newV, maxChanges);
            if (changes.Count >= maxChanges)
                return;
        }
    }

    private static IEnumerable<string> EnumerateForDiff(IEnumerable e)
    {
        var i = 0;
        foreach (var v in e)
        {
            if (i++ > 50)
            {
                yield return "...";
                yield break;
            }
            yield return FormatValue(v);
        }
    }

    private static bool IsSimple(Type t)
        => t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal);

    private static string FormatValue(object? v)
    {
        if (v is null) return "<null>";

        if (v is string s)
        {
            if (s.Length == 0) return "\"\"";
            if (s.Length > 80) s = s.Substring(0, 80) + "...";
            if (IsSensitiveString(s)) return "<redacted>";
            return "\"" + s + "\"";
        }

        return v.ToString() ?? "<value>";
    }

    private static bool IsSensitiveString(string s)
    {
        return s.Length > 0 && s.Length < 128 && s.Contains("password", StringComparison.OrdinalIgnoreCase);
    }
}
