using System.Collections;
using System.Reflection;
using Lola.Application.Identity;
using Lola.Domain.Classification;

namespace Lola.Application.Security;

/// Applies ADR-0008 response masking in place: every string member
/// carrying [PiiClass] is replaced by "•••" + last-4 unless the
/// current VisibilityContext grants plaintext for that class. Shared
/// by REST and GraphQL through the MaskedInvoke seam both adapters
/// call (ADR-0011).
public static class PiiMasker
{
    private const int MaxDepth = 6;

    public static void MaskInPlace(object? root)
    {
        if (root is null) return;
        var visible = VisibilityContext.Current;
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        Mask(root, visible, visited, 0);
    }

    private static void Mask(
        object? node, IReadOnlySet<PiiClass> visible, HashSet<object> visited, int depth)
    {
        if (node is null || depth > MaxDepth) return;

        var type = node.GetType();
        if (type.IsPrimitive || node is string or decimal or DateTime or DateTimeOffset
            or Guid or Enum || type.IsEnum || Nullable.GetUnderlyingType(type) is not null)
            return;

        if (!visited.Add(node)) return;

        if (node is IEnumerable seq)
        {
            foreach (var item in seq)
                if (item is not null) Mask(item, visible, visited, depth + 1);
            return;
        }

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetCustomAttribute<PiiClassAttribute>() is { } attr &&
                prop.PropertyType == typeof(string) && prop.CanWrite)
            {
                var value = (string?)prop.GetValue(node);
                if (value is not null && !visible.Contains(attr.Class))
                    prop.SetValue(node, MaskValue(value));
            }
            else if (prop.CanRead && !prop.PropertyType.IsValueType &&
                     prop.PropertyType != typeof(string))
            {
                Mask(prop.GetValue(node), visible, visited, depth + 1);
            }
        }
    }

    /// "•••" + last-4 — enough to correlate for support, never enough
    /// to reconstruct (e.g. SSN keeps only the trailing four).
    public static string MaskValue(string value)
        => value.Length >= 4 ? $"•••{value[^4..]}" : "•••";
}
