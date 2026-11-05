#if NETFRAMEWORK
// Compiled only into the .NET Framework 4.8 builds (2024 programs): the newer .NET methods the
// add-in code uses, so the same code builds for 2024 through 2027. PolySharp covers the language
// features (records, init, required, ranges); this covers library methods.
using System.Globalization;
using System.Text;

namespace System
{
    internal static class NexusStringPolyfills
    {
        public static bool Contains(this string s, string value, StringComparison comparison) => s.IndexOf(value, comparison) >= 0;
        public static bool Contains(this string s, char value) => s.IndexOf(value) >= 0;
        public static bool Contains(this string s, char value, StringComparison comparison) => s.IndexOf(value.ToString(), comparison) >= 0;
        public static bool StartsWith(this string s, char value) => s.Length > 0 && s[0] == value;
        public static bool EndsWith(this string s, char value) => s.Length > 0 && s[s.Length - 1] == value;
        public static string[] Split(this string s, char separator, StringSplitOptions options) => s.Split(new[] { separator }, options);
        public static string[] Split(this string s, string separator, StringSplitOptions options = StringSplitOptions.None) => s.Split(new[] { separator }, options);
        public static int IndexOf(this string s, char value, StringComparison comparison) => s.IndexOf(value.ToString(), comparison);

        public static string Replace(this string s, string oldValue, string? newValue, StringComparison comparison)
        {
            if (string.IsNullOrEmpty(oldValue)) return s;
            var sb = new StringBuilder();
            int at = 0, found;
            while ((found = s.IndexOf(oldValue, at, comparison)) >= 0)
            {
                sb.Append(s, at, found - at).Append(newValue);
                at = found + oldValue.Length;
            }
            return sb.Append(s, at, s.Length - at).ToString();
        }

        public static int GetHashCode(this string s, StringComparison comparison) =>
            comparison is StringComparison.OrdinalIgnoreCase or StringComparison.CurrentCultureIgnoreCase or StringComparison.InvariantCultureIgnoreCase
                ? StringComparer.OrdinalIgnoreCase.GetHashCode(s)
                : s.GetHashCode();
    }

    internal static class NexusMath
    {
        public static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
        public static double Clamp(double value, double min, double max) => value < min ? min : value > max ? max : value;
    }
}

namespace System.Collections.Generic
{
    internal static class NexusCollectionPolyfills
    {
        public static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> pair, out TKey key, out TValue value)
        {
            key = pair.Key;
            value = pair.Value;
        }

        public static TValue? GetValueOrDefault<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key) =>
            dictionary.TryGetValue(key, out var value) ? value : default;

        public static TValue GetValueOrDefault<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key, TValue fallback) =>
            dictionary.TryGetValue(key, out var value) ? value : fallback;

        public static bool TryAdd<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key, TValue value)
        {
            if (dictionary.ContainsKey(key)) return false;
            dictionary.Add(key, value);
            return true;
        }

        public static bool Remove<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key, out TValue value)
        {
            if (!dictionary.TryGetValue(key, out value!)) return false;
            dictionary.Remove(key);
            return true;
        }
    }
}

namespace System.Linq
{
    internal static class NexusLinqPolyfills
    {
        public static IEnumerable<TSource> DistinctBy<TSource, TKey>(this IEnumerable<TSource> source, Func<TSource, TKey> key)
        {
            var seen = new HashSet<TKey>();
            foreach (var item in source)
                if (seen.Add(key(item))) yield return item;
        }

        public static TSource? MaxBy<TSource, TKey>(this IEnumerable<TSource> source, Func<TSource, TKey> key) =>
            source.OrderByDescending(key).FirstOrDefault();

        public static TSource? MinBy<TSource, TKey>(this IEnumerable<TSource> source, Func<TSource, TKey> key) =>
            source.OrderBy(key).FirstOrDefault();

        public static IEnumerable<(TFirst First, TSecond Second)> Zip<TFirst, TSecond>(this IEnumerable<TFirst> first, IEnumerable<TSecond> second) =>
            first.Zip(second, (a, b) => (a, b));

        public static IEnumerable<TSource[]> Chunk<TSource>(this IEnumerable<TSource> source, int size)
        {
            var chunk = new List<TSource>(size);
            foreach (var item in source)
            {
                chunk.Add(item);
                if (chunk.Count == size) { yield return chunk.ToArray(); chunk.Clear(); }
            }
            if (chunk.Count > 0) yield return chunk.ToArray();
        }
    }
}
#endif
