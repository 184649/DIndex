using DIndex;
internal static partial class Program
{
static SearchRecord[] MakeCorpus(int count)
{
    var records = new SearchRecord[count];
    for (var i = 0; i < count; i++)
    {
        var name = (i % 20) switch
        {
            0 => "report.txt",
            1 => $"report_2026_budget_{i % 200:D3}.xlsx",
            2 => $"monthly_report_final_{i % 250:D3}.pdf",
            3 => $"報告書_2026_{i % 120:D3}.docx",
            4 => $"設計レビュー_資料_{i % 160:D3}.pdf",
            5 => "notes.txt",
            6 => "README.md",
            7 => $"invoice_{i % 500:D3}.pdf",
            8 => $"photo_{i % 1000:D4}.jpg",
            9 => $"archive_{i % 400:D3}.zip",
            10 => $"report_final_{i % 200:D3}.pdf",
            11 => $"Annual Report {i % 300:D3}.pdf",
            12 => $"budget_日本語_{i % 90:D3}.xlsx",
            13 => $"résumé_{i % 80:D3}.txt",
            14 => $"resume_{i % 80:D3}.txt",
            15 => "Reports",
            16 => "日報_資料.txt",
            17 => $"project-config_{i % 700:D3}.json",
            18 => "duplicate.txt",
            _ => $"data_{i % 250:D3}.csv"
        };
        var path = $"C:\\Users\\Bench\\Documents\\Project{i % 97:D2}\\Batch{i / 1000:D4}\\Item{i:D7}\\{name}";
        records[i] = new SearchRecord
        {
            Name = name, NameLower = name.ToLowerInvariant(), FullPath = path, FullPathLower = path.ToLowerInvariant(),
            IsDirectory = i % 37 == 0,
            UpdatedTicks = 638800000000000000L + (long)((i * 7919L) % 1000) * TimeSpan.TicksPerMinute,
        };
    }
    return records;
}
static string[] Words(string keyword) => string.IsNullOrWhiteSpace(keyword?.Trim()) ? Array.Empty<string>() : keyword.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToLowerInvariant()).ToArray();
static bool IsMatch(SearchRecord item, bool path, string[] words)
{
    var target = path ? item.FullPathLower : item.NameLower;
    return words.All(word => target.Contains(word, StringComparison.Ordinal));
}
static int Score(SearchRecord item, bool path, string[] words)
{
    var target = path ? item.FullPathLower : item.NameLower;
    return (item.NameLower.Equals(words[0], StringComparison.Ordinal) ? -10000 : 0) +
        (item.NameLower.StartsWith(words[0], StringComparison.Ordinal) ? -5000 : 0) +
        target.IndexOf(words[0], StringComparison.Ordinal) + (item.IsDirectory ? 0 : 30);
}
}
