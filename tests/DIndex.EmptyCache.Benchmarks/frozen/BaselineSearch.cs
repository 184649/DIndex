using System.Collections.Concurrent;

namespace DIndex;

public sealed class BaselineSearch
{
    private readonly ConcurrentDictionary<string, SearchRecord> _items = new(StringComparer.OrdinalIgnoreCase);
    public BaselineSearch(IEnumerable<SearchRecord> records) { foreach (var record in records) _items[record.FullPath] = record; }
    public ConcurrentDictionary<string, SearchRecord> BenchmarkItems => _items;
    public SearchRecord[] Snapshot() => _items.Values.ToArray();
    public List<SearchRecord> Search(string keyword, bool pathSearch, int limit, CancellationToken cancellationToken)
    {
        keyword = (keyword ?? string.Empty).Trim();
        var values = _items.Values;

        if (string.IsNullOrWhiteSpace(keyword))
        {
            return values.OrderByDescending(x => x.UpdatedTicks).Take(limit).ToList();
        }

        var words = keyword.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                           .Select(x => x.ToLowerInvariant())
                           .ToArray();

        bool IsMatch(SearchRecord item)
        {
            var target = pathSearch ? item.FullPathLower : item.NameLower;
            for (var i = 0; i < words.Length; i++)
            {
                if (!target.Contains(words[i], StringComparison.Ordinal)) return false;
            }
            return true;
        }

        int Score(SearchRecord item)
        {
            var target = pathSearch ? item.FullPathLower : item.NameLower;
            var first = target.IndexOf(words[0], StringComparison.Ordinal);
            var exactBonus = item.NameLower.Equals(words[0], StringComparison.Ordinal) ? -10000 : 0;
            var prefixBonus = item.NameLower.StartsWith(words[0], StringComparison.Ordinal) ? -5000 : 0;
            var dirPenalty = item.IsDirectory ? 0 : 30;
            return exactBonus + prefixBonus + first + dirPenalty;
        }

        if (values.Count > 50000)
        {
            return values.AsParallel()
                         .WithCancellation(cancellationToken)
                         .Where(IsMatch)
                         .OrderBy(Score)
                         .ThenBy(x => x.Name)
                         .Take(Math.Max(1, limit))
                         .ToList();
        }

        return values.Where(IsMatch)
                     .OrderBy(Score)
                     .ThenBy(x => x.Name)
                     .Take(Math.Max(1, limit))
                     .ToList();
    }

}
