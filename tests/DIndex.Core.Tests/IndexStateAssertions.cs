using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using DIndex;

// Test-only diagnostics. Never clear dirty flags or mutate the service to make a test pass.
internal static class IndexStateAssertions
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task WaitForCleanStaticSnapshot(FileIndexService service, IEnumerable<string> expectedPaths,
        string reason, int timeoutMs = 10000)
    {
        var expected = expectedPaths.Order(StringComparer.Ordinal).ToArray();
        var initial = Capture(service);
        var timer = Stopwatch.StartNew();
        TimeSpan? cleanSince = null;
        var reportedDirty = initial.IsDirty;
        if (reportedDirty) Console.WriteLine($"  SETTLING {reason}: initial={Describe(initial)}");
        while (true)
        {
            var state = Capture(service);
            if (state.IsDirty && !reportedDirty)
            {
                // Keep the actual reason in successful CI logs too, rather than hiding the race.
                Console.WriteLine($"  SETTLING {reason}: initial={Describe(initial)}; observed={Describe(state)}");
                reportedDirty = true;
            }
            if (state.IsRebuilding || state.IsPartial || state.CachedOnly != 0 || state.Active is null
                || state.Active.IsCanceled || state.Active.NeedsRecovery || state.Active.FailureVersion != 0 || state.Active.FailedPathCount != 0
                || !ReferenceEquals(initial.ActiveReference, state.ActiveReference))
                throw new Exception($"{reason}: cannot treat recovery, read failure, partial/cache state, or a replaced generation as a benign watcher tail. initial={Describe(initial)}; observed={Describe(state)}");

            AssertExactPaths(service, expected, reason, state);
            if (!state.IsDirty)
            {
                cleanSince ??= timer.Elapsed;
                // A short quiet window catches notifications delivered just after the build task.
                if (timer.Elapsed - cleanSince.Value >= TimeSpan.FromMilliseconds(100))
                {
                    if (reportedDirty) Console.WriteLine($"  SETTLED {reason} after {timer.ElapsedMilliseconds} ms: {Describe(state)}");
                    return;
                }
            }
            else
            {
                cleanSince = null;
                if (state.Active.PendingCount == 0 && !state.Active.Applying && !state.Active.Unpublished)
                    throw new Exception($"{reason}: dirty without a known drainable watcher batch. initial={Describe(initial)}; observed={Describe(state)}");
            }
            if (timer.ElapsedMilliseconds >= timeoutMs)
                throw new Exception($"{reason}: watcher tail did not settle within {timeoutMs} ms. initial={Describe(initial)}; observed={Describe(state)}");
            await Task.Delay(20);
        }
    }

    public static State Capture(FileIndexService service)
    {
        lock (Field<object>(service, "_gate"))
        {
            var snapshot = Field<object>(service, "_snapshot");
            var active = Field<object?>(service, "_active");
            var building = Field<object?>(service, "_building");
            // Hold the active queue lock across both the aggregate dirty flag and its reasons.
            // Otherwise the pump can finish between those reads and manufacture an unexplained
            // dirty diagnostic. Recovery flags are monotonic within a session and checked below.
            lock (active is null ? Field<object>(service, "_gate") : Field<object>(active, "_lock"))
                return new State(service.IsDirty, service.IsRebuilding,
                    Property<bool>(snapshot, "IsPartial"), Field<int>(service, "_cachedOnly"),
                    service.SnapshotVersion, service.RequestVersion, service.Count,
                    Session(active), Session(building), active);
        }
    }

    public static string Describe(State state) => JsonSerializer.Serialize(state, JsonOptions);

    private static SessionState? Session(object? session)
    {
        if (session is null) return null;
        lock (Field<object>(session, "_lock"))
        {
            var pending = Field<Dictionary<string, bool>>(session, "_pending");
            var failedPaths = (string[])session.GetType().GetMethod("FailedPathsSnapshot")!.Invoke(session, null)!;
            return new SessionState(RuntimeHelpers.GetHashCode(session), Property<bool>(session, "IsDirty"),
                pending.Count, pending.Take(10).Select(x => new PendingPath(x.Key, x.Value)).ToArray(),
                Field<bool>(session, "_applying"), Field<bool>(session, "_unpublished"),
                Property<bool>(session, "NeedsRecovery"), Property<int>(session, "FailureVersion"),
                failedPaths.Length, failedPaths.Take(10).ToArray(), Field<int>(session, "_activeWorkers"),
                Property<CancellationToken>(session, "Token").IsCancellationRequested);
        }
    }

    private static void AssertExactPaths(FileIndexService service, string[] expected, string reason, State state)
    {
        var actual = service.Snapshot().Select(x => x.FullPath).Order(StringComparer.Ordinal).ToArray();
        if (expected.SequenceEqual(actual, StringComparer.Ordinal)) return;
        var differences = JsonSerializer.Serialize(new
        {
            expectedCount = expected.Length, actualCount = actual.Length,
            missing = expected.Except(actual, StringComparer.Ordinal).Take(10).ToArray(),
            unexpected = actual.Except(expected, StringComparer.Ordinal).Take(10).ToArray()
        });
        throw new Exception($"{reason}: exact static snapshot changed during watcher settling: {differences}; state={Describe(state)}");
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, PrivateInstance)!.GetValue(target)!;
    private static T Property<T>(object target, string name) => (T)target.GetType().GetProperty(name)!.GetValue(target)!;

    internal sealed record PendingPath(string Path, bool Subtree);
    internal sealed record SessionState(int Identity, bool IsDirty, int PendingCount, PendingPath[] PendingPaths,
        bool Applying, bool Unpublished, bool NeedsRecovery, int FailureVersion, int FailedPathCount,
        string[] FailedPaths, int ActiveWorkers, bool IsCanceled);
    internal sealed record State(bool IsDirty, bool IsRebuilding, bool IsPartial, int CachedOnly,
        long SnapshotVersion, long RequestVersion, int Count, SessionState? Active, SessionState? Building,
        [property: JsonIgnore] object? ActiveReference);
}
