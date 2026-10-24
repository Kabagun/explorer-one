using ExplorerOne;
using ExplorerOne.Core;
using ExplorerOne.Opening;

internal static class PresentationCases
{
    internal static IEnumerable<(string Name, Func<Task> Run)> All =>
    [
        ("Native original appearance is claimed without recapturing or rewriting alpha zero", () =>
        {
            var main = Id(1); var source = Id(2);
            var native = new FakeNative(main) { AppearanceAvailable = true };
            using var presentation = new SourceWindowPresentation(native);
            native.AddWindow(source); native.SuppressNatively(source, 0, new(0, 255, 2));
            presentation.OnShown(source, main);
            Check(native.StyleWrites == 0 && native.AlphaWrites == 0 && native.NativeClaims.SequenceEqual([source]));
            presentation.OnTransfer(source, TransferResult.Unsafe);
            Check(native.Style(source) == 0 && native.NativeReleased.SequenceEqual([source]));
            Check(native.ReleaseAppearances.Single().Style == 0 && presentation.NextExpiryDelay is null);
            Check(native.NativeRestoreBegins.SequenceEqual([source]) && native.NativeWritesBeforeRestore == 0);
            return Task.CompletedTask;
        }),
        ("Managed rollback marks restoring before a native pre-show hook can reassert alpha zero", () =>
        {
            var main = Id(1); var source = Id(2);
            var native = new FakeNative(main) { AppearanceAvailable = true, ReassertOnStyleWrites = true };
            using var presentation = new SourceWindowPresentation(native);
            native.AddWindow(source); native.SuppressNatively(source, 0, new(0, 255, 2));
            presentation.OnShown(source, main); presentation.OnTransfer(source, TransferResult.Unsafe);
            Check(native.Style(source) == 0 && native.NativeReleased.SequenceEqual([source]));
            Check(native.NativeRestoreBegins.SequenceEqual([source]) && native.NativeWritesBeforeRestore == 0 && native.NativeReassertions == 0);
            return Task.CompletedTask;
        }),
        ("Unavailable or throwing native restore handoffs perform no appearance writes and retry", () =>
        {
            foreach (var throws in new[] { false, true })
            {
                var main = Id(1); var source = Id(2);
                var native = new FakeNative(main) { AppearanceAvailable = true, NativeRestoreBeginFailures = 1, NativeRestoreBeginThrows = throws };
                var clock = new FakeClock();
                using var presentation = new SourceWindowPresentation(native, clock);
                native.AddWindow(source); native.SuppressNatively(source, 0, new(0, 255, 2));
                presentation.OnShown(source, main); presentation.OnTransfer(source, TransferResult.Unsafe);
                Check(native.StyleWrites == 0 && native.AlphaWrites == 0 && native.NativeReleased.Count == 0);
                Check(presentation.NextExpiryDelay == TimeSpan.FromMilliseconds(100));
                clock.Advance(TimeSpan.FromMilliseconds(100)); presentation.ObserveCurrentMain();
                Check(native.Style(source) == 0 && native.NativeReleased.SequenceEqual([source]) && native.NativeWritesBeforeRestore == 0);
            }
            return Task.CompletedTask;
        }),
        ("Native prelayered originals restore exactly before ownership is released", () =>
        {
            var main = Id(1); var source = Id(2);
            var original = new LayeredWindowAttributes(0x123456, 73, 3);
            var native = new FakeNative(main) { AppearanceAvailable = true, ReassertOnStyleWrites = true };
            using var presentation = new SourceWindowPresentation(native);
            native.AddWindow(source); native.SuppressNatively(source, 0x80010, original);
            presentation.OnShown(source, main);
            native.SetAppearance(source, native.Style(source) | 0x80, native.Attributes(source));
            presentation.RestoreAll();
            Check(native.Style(source) == 0x80090 && native.Attributes(source) == original);
            Check(native.ReleaseAppearances.Single() == (0x80090u, original));
            return Task.CompletedTask;
        }),
        ("Native timer restoration relinquishes managed ownership without re-hiding", () =>
        {
            var main = Id(1); var source = Id(2);
            var native = new FakeNative(main) { AppearanceAvailable = true };
            using var presentation = new SourceWindowPresentation(native);
            native.AddWindow(source); native.SuppressNatively(source, 0, new(0, 255, 2));
            presentation.OnShown(source, main);
            native.RestoreNatively(source);
            presentation.ObserveCurrentMain(); presentation.OnShown(source); presentation.Dispose();
            Check(native.Style(source) == 0 && native.StyleWrites == 0 && native.AlphaWrites == 0);
            Check(native.NativeReleased.Count == 0 && native.MinimizeAttempts.Count == 0 && presentation.NextExpiryDelay is null);
            return Task.CompletedTask;
        }),
        ("A newer native capture cookie is never restored or released through an old token", () =>
        {
            var main = Id(1); var source = Id(2);
            var native = new FakeNative(main) { AppearanceAvailable = true };
            using var presentation = new SourceWindowPresentation(native);
            native.AddWindow(source); native.SuppressNatively(source, 0, new(0, 255, 2));
            presentation.OnShown(source, main);
            native.SuppressNatively(source, 0x80080, new(7, 91, 3), cookie: 2);
            presentation.RestoreAll(); presentation.Dispose();
            Check(native.Style(source) == 0x80080 && native.Attributes(source).Alpha == 0);
            Check(native.NativeReleased.Count == 0 && native.StyleWrites == 0 && native.AlphaWrites == 0);
            return Task.CompletedTask;
        }),
        ("Claiming a native snapshot keeps its remaining deadline instead of granting four more seconds", () =>
        {
            var main = Id(1); var source = Id(2);
            var native = new FakeNative(main) { AppearanceAvailable = true, NativeRemaining = TimeSpan.FromMilliseconds(500) };
            var clock = new FakeClock();
            using var presentation = new SourceWindowPresentation(native, clock);
            native.AddWindow(source); native.SuppressNatively(source, 0, new(0, 255, 2));
            presentation.OnShown(source, main);
            Check(presentation.NextExpiryDelay == TimeSpan.FromMilliseconds(500));
            clock.Advance(TimeSpan.FromMilliseconds(500)); presentation.ObserveCurrentMain();
            Check(native.Style(source) == 0 && native.NativeReleased.SequenceEqual([source]));
            return Task.CompletedTask;
        }),
        ("An unavailable native snapshot is never recaptured or minimized by managed fallback", () =>
        {
            var main = Id(1); var source = Id(2);
            var native = new FakeNative(main) { AppearanceAvailable = true, NativeClaimUnavailable = true };
            using var presentation = new SourceWindowPresentation(native);
            native.AddWindow(source); native.SuppressNatively(source, 0, new(0, 255, 2));
            presentation.OnShown(source, main); presentation.RestoreAll();
            Check(native.StyleWrites == 0 && native.AlphaWrites == 0 && native.MinimizeAttempts.Count == 0);
            native.RestoreNatively(source); presentation.OnShown(source);
            Check(native.Style(source) == 0 && native.StyleWrites == 0);
            return Task.CompletedTask;
        }),
        ("A raced SHOW claim retries on error and restores the newly published native original immediately", () =>
        {
            var main = Id(1); var source = Id(2);
            var native = new FakeNative(main) { AppearanceAvailable = true, NativeClaimUnavailable = true };
            using var presentation = new SourceWindowPresentation(native);
            native.AddWindow(source); native.SuppressNatively(source, 0, new(0, 255, 2));
            presentation.OnShown(source, main);
            Check(native.StyleWrites == 0 && native.AlphaWrites == 0);
            native.NativeClaimUnavailable = false;
            presentation.OnTransfer(source, TransferResult.Unsafe);
            Check(native.Style(source) == 0 && native.NativeClaims.SequenceEqual([source]) && native.NativeReleased.SequenceEqual([source]));
            Check(native.MinimizeAttempts.Count == 0 && presentation.NextExpiryDelay is null);
            return Task.CompletedTask;
        }),
        ("Observe adopts a delayed native claim without opacity writes and keeps its remaining deadline", () =>
        {
            var main = Id(1); var source = Id(2);
            var native = new FakeNative(main) { AppearanceAvailable = true, NativeClaimUnavailable = true };
            var clock = new FakeClock();
            using var presentation = new SourceWindowPresentation(native, clock);
            native.AddWindow(source); native.SuppressNatively(source, 0, new(0, 255, 2));
            presentation.OnShown(source, main);
            clock.Advance(TimeSpan.FromMilliseconds(250)); native.NativeClaimUnavailable = false;
            native.NativeRemaining = TimeSpan.FromMilliseconds(750);
            presentation.ObserveCurrentMain();
            Check(native.NativeClaims.SequenceEqual([source]) && native.StyleWrites == 0 && native.AlphaWrites == 0);
            Check(presentation.NextExpiryDelay == TimeSpan.FromMilliseconds(750));
            clock.Advance(TimeSpan.FromMilliseconds(750)); presentation.ObserveCurrentMain();
            Check(native.Style(source) == 0 && native.NativeReleased.SequenceEqual([source]));
            return Task.CompletedTask;
        }),
        ("A foreign alpha change relinquishes the native marker without overwriting its appearance", () =>
        {
            var main = Id(1); var source = Id(2);
            var native = new FakeNative(main) { AppearanceAvailable = true };
            using var presentation = new SourceWindowPresentation(native);
            native.AddWindow(source); native.SuppressNatively(source, 0, new(0, 255, 2));
            presentation.OnShown(source, main);
            var foreign = new LayeredWindowAttributes(7, 91, 3);
            native.SetAppearance(source, 0x80080, foreign);
            presentation.OnTransfer(source, TransferResult.Unsafe);
            Check(native.Style(source) == 0x80080 && native.Attributes(source) == foreign);
            Check(native.StyleWrites == 0 && native.AlphaWrites == 0 && native.NativeReleased.SequenceEqual([source]));
            Check(native.NativeRestoreBegins.Count == 0);
            return Task.CompletedTask;
        }),
        ("Failed native-original rollback keeps its token through the bounded retry", () =>
        {
            var main = Id(1); var source = Id(2); var original = new LayeredWindowAttributes(7, 73, 3);
            var native = new FakeNative(main) { AppearanceAvailable = true };
            var clock = new FakeClock();
            using var presentation = new SourceWindowPresentation(native, clock);
            native.AddWindow(source); native.SuppressNatively(source, 0x80010, original);
            presentation.OnShown(source, main); native.AlphaWriteFailures = 2;
            presentation.OnTransfer(source, TransferResult.Unsafe);
            Check(native.NativeReleased.Count == 0 && presentation.NextExpiryDelay == TimeSpan.FromMilliseconds(100));
            Check(native.NativeRestoring(source) && native.NativeWritesBeforeRestore == 0);
            native.ReassertNative(source);
            Check(native.NativeReassertions == 0);
            clock.Advance(TimeSpan.FromMilliseconds(100)); presentation.ObserveCurrentMain();
            Check(native.Style(source) == 0x80010 && native.Attributes(source) == original && native.NativeReleased.SequenceEqual([source]));
            return Task.CompletedTask;
        }),
        ("The native timer can finish an in-progress restore without a later managed overwrite", () =>
        {
            var main = Id(1); var source = Id(2); var original = new LayeredWindowAttributes(7, 73, 3);
            var native = new FakeNative(main) { AppearanceAvailable = true };
            var clock = new FakeClock();
            using var presentation = new SourceWindowPresentation(native, clock);
            native.AddWindow(source); native.SuppressNatively(source, 0x80010, original);
            presentation.OnShown(source, main); native.AlphaWriteFailures = 2;
            presentation.OnTransfer(source, TransferResult.Unsafe);
            Check(native.NativeRestoring(source));
            native.RestoreNatively(source);
            var writes = (native.StyleWrites, native.AlphaWrites);
            clock.Advance(TimeSpan.FromSeconds(4)); presentation.ObserveCurrentMain(); presentation.Dispose();
            Check(native.Style(source) == 0x80010 && native.Attributes(source) == original);
            Check(writes == (native.StyleWrites, native.AlphaWrites) && native.NativeReleased.Count == 0 && presentation.NextExpiryDelay is null);
            return Task.CompletedTask;
        }),
        ("Native property claims verify the trusted owner and release the valid marker first", () =>
        {
            var source = Id(2); var properties = new FakeAppearanceProperties(source);
            var native = new NativeSourceAppearance(properties, () => properties.Owner);
            Check(native.TryClaim(source, out var snapshot) == NativeSourceAppearanceClaim.Claimed);
            Check(snapshot.OriginalStyle == 0 && snapshot.Attributes == new LayeredWindowAttributes(0, 255, 2));
            Check(native.Remaining(snapshot) == TimeSpan.FromMilliseconds(400) && native.Owns(source, snapshot));
            Check(native.Release(source, snapshot));
            Check(properties.Removed.First() == "Flags" && properties.Values.Count == 0);
            return Task.CompletedTask;
        }),
        ("Native restoring state authenticates the full token and remains owned until release", () =>
        {
            var source = Id(2); var properties = new FakeAppearanceProperties(source);
            var native = new NativeSourceAppearance(properties, () => properties.Owner);
            Check(native.TryClaim(source, out var snapshot) == NativeSourceAppearanceClaim.Claimed);
            Check(properties.Values["Claimed"] == 1 && native.BeginRestore(source, snapshot));
            Check(properties.Values["Claimed"] == 2 && native.Owns(source, snapshot) && native.BeginRestore(source, snapshot));
            Check(native.TryClaim(source, out _) == NativeSourceAppearanceClaim.Unavailable);
            Check(native.Release(source, snapshot) && properties.Removed.First() == "Flags" && properties.Values.Count == 0);
            return Task.CompletedTask;
        }),
        ("A rejected restoring property write leaves adoption intact without beginning rollback", () =>
        {
            var source = Id(2); var properties = new FakeAppearanceProperties(source) { RejectRestore = true };
            var native = new NativeSourceAppearance(properties, () => properties.Owner);
            Check(native.TryClaim(source, out var snapshot) == NativeSourceAppearanceClaim.Claimed);
            Check(!native.BeginRestore(source, snapshot) && properties.Values["Claimed"] == 1 && native.Owns(source, snapshot));
            Check(properties.Removed.Count == 0);
            return Task.CompletedTask;
        }),
        ("A changed cookie during the restoring handoff never authorizes rollback", () =>
        {
            var source = Id(2); var properties = new FakeAppearanceProperties(source);
            var native = new NativeSourceAppearance(properties, () => properties.Owner);
            Check(native.TryClaim(source, out var snapshot) == NativeSourceAppearanceClaim.Claimed);
            properties.AfterSet = (name, value) => { if (name == "Claimed" && value == 2) properties.Values["WindowCookie"]++; };
            Check(!native.BeginRestore(source, snapshot) && !native.Owns(source, snapshot) && !native.Release(source, snapshot));
            Check(properties.Removed.Count == 0);
            return Task.CompletedTask;
        }),
        ("Native mode defers legacy alpha capture while SHOW properties are still unpublished", () =>
        {
            var source = Id(2); var properties = new FakeAppearanceProperties(source);
            properties.Values.Clear();
            var native = new NativeSourceAppearance(properties, () => properties.Owner, () => true);
            Check(native.TryClaim(source, out _) == NativeSourceAppearanceClaim.Unavailable);
            Check(properties.Values.Count == 0 && properties.Removed.Count == 0);
            return Task.CompletedTask;
        }),
        ("Foreign native nonce and recycled process identities cannot be claimed", () =>
        {
            foreach (var wrongNonce in new[] { false, true })
            {
                var source = Id(2); var properties = new FakeAppearanceProperties(source);
                if (wrongNonce) properties.Values["NonceLow"]++;
                else properties.WindowCurrent = false;
                var native = new NativeSourceAppearance(properties, () => properties.Owner);
                Check(native.TryClaim(source, out _) == NativeSourceAppearanceClaim.Unavailable);
                Check(properties.Values["Claimed"] == 0 && properties.Removed.Count == 0);
            }
            return Task.CompletedTask;
        }),
        ("Malformed native DWORD flags and impossible attribute ownership remain read-only", () =>
        {
            foreach (var flags in new[] { (1UL << 40) | 1, 5UL })
            {
                var source = Id(2); var properties = new FakeAppearanceProperties(source);
                properties.Values["Flags"] = flags;
                var native = new NativeSourceAppearance(properties, () => properties.Owner);
                Check(native.TryClaim(source, out _) == NativeSourceAppearanceClaim.Unavailable);
                Check(properties.Values["Claimed"] == 0 && properties.Removed.Count == 0);
            }
            return Task.CompletedTask;
        }),
        ("A native timer or a newer property cookie invalidates an already claimed token", () =>
        {
            foreach (var timer in new[] { false, true })
            {
                var source = Id(2); var properties = new FakeAppearanceProperties(source);
                var native = new NativeSourceAppearance(properties, () => properties.Owner);
                Check(native.TryClaim(source, out var snapshot) == NativeSourceAppearanceClaim.Claimed);
                if (timer) properties.Values.Remove("Flags"); else properties.Values["WindowCookie"]++;
                Check(!native.Owns(source, snapshot) && !native.Release(source, snapshot));
                Check(properties.Removed.Count == 0);
            }
            return Task.CompletedTask;
        }),
        ("Captured native ownership can be released after the configured controller is cleared", () =>
        {
            var source = Id(2); var properties = new FakeAppearanceProperties(source);
            NativeSourceAppearanceOwner? expected = properties.Owner;
            var native = new NativeSourceAppearance(properties, () => expected);
            Check(native.TryClaim(source, out var snapshot) == NativeSourceAppearanceClaim.Claimed);
            expected = null;
            Check(native.Owns(source, snapshot) && native.Release(source, snapshot));
            return Task.CompletedTask;
        }),
        ("Property release stops when a newer capture replaces its cookie after marker removal", () =>
        {
            var source = Id(2); var properties = new FakeAppearanceProperties(source);
            var native = new NativeSourceAppearance(properties, () => properties.Owner);
            Check(native.TryClaim(source, out var snapshot) == NativeSourceAppearanceClaim.Claimed);
            properties.AfterRemove = name =>
            {
                if (name == "Flags") { properties.Values["WindowCookie"] = 2; properties.Values["Flags"] = 1; }
            };
            Check(!native.Release(source, snapshot));
            Check(properties.Removed.SequenceEqual(["Flags"]) && properties.Values["WindowCookie"] == 2 && properties.Values["OwnerPid"] == 7);
            return Task.CompletedTask;
        }),
        ("Preopen eligibility cannot wait for a blocked appearance style write", async () =>
        {
            var main = Id(1); var source = Id(2); var next = Id(3);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var native = new FakeNative(main) { AppearanceAvailable = true };
            using var presentation = new SourceWindowPresentation(native);
            native.AddWindow(source); native.AddWindow(next);
            native.BeforeStyleWrite = () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); };
            // OnShown must publish main even while it owns a blocked appearance gate.
            var appearance = Task.Factory.StartNew(() => presentation.OnShown(source, main),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Task<bool>? eligibility = null;
            try
            {
                Check(entered.Wait(TimeSpan.FromSeconds(2)));
                eligibility = Task.Factory.StartNew(() => presentation.TryGetPreopenTarget(next, created: true, out var target) && target == main,
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                Check(await Task.WhenAny(eligibility, Task.Delay(100)) == eligibility && await eligibility);
            }
            finally { release.Set(); await appearance; if (eligibility is not null) await eligibility; }
        }),
        ("An expired invisible standby CREATE remains eligible at its first real SHOW", () =>
        {
            var main = Id(1); var source = Id(2);
            var native = new FakeNative(main) { AppearanceAvailable = true };
            var clock = new FakeClock();
            using var presentation = new SourceWindowPresentation(native, clock);
            presentation.Observe(main); native.AddWindow(source, visible: false);
            presentation.OnCreated(source);
            Check(native.Attributes(source).Alpha == 0);
            clock.Advance(TimeSpan.FromSeconds(4)); presentation.ObserveCurrentMain();
            Check(native.Style(source) == 0 && presentation.NextExpiryDelay is null);
            native.SetVisible(source, true);
            Check(presentation.TryGetPreopenTarget(source, created: false, out var target) && target == main);
            presentation.OnShown(source);
            Check(native.Attributes(source).Alpha == 0 && native.Style(source) == 0x80000);
            presentation.OnTransfer(source, TransferResult.Unsafe);
            Check(!presentation.TryGetPreopenTarget(source, created: false, out _));
            return Task.CompletedTask;
        }),
        ("Preopen waits for a real show but precedes appearance writes and COM", () =>
        {
            var main = Id(1); var source = Id(2);
            var native = new FakeNative(main) { AppearanceAvailable = true };
            using var presentation = new SourceWindowPresentation(native);
            presentation.Observe(main);
            native.AddWindow(source, visible: false);
            Check(!presentation.TryGetPreopenTarget(source, created: true, out _));
            Check(!presentation.TryGetPreopenTarget(source, created: false, out _));
            native.AddWindow(source, visible: true);
            Check(presentation.TryGetPreopenTarget(source, created: true, out var target) && target == main);
            Check(native.StyleWrites == 0 && native.MinimizeAttempts.Count == 0);
            return Task.CompletedTask;
        }),
        ("Startup, main, and iconic frames never request speculative tabs", () =>
        {
            var main = Id(1); var existing = Id(2); var iconic = Id(3);
            var native = new FakeNative(main, existing);
            using var presentation = new SourceWindowPresentation(native);
            presentation.Observe(main);
            native.AddWindow(iconic, minimized: true);
            Check(!presentation.TryGetPreopenTarget(main, created: true, out _));
            Check(!presentation.TryGetPreopenTarget(existing, created: true, out _));
            Check(!presentation.TryGetPreopenTarget(iconic, created: true, out _));
            return Task.CompletedTask;
        }),
        ("Restored sources and a lost main cannot request another preopened tab", () =>
        {
            var main = Id(1); var source = Id(2); var next = Id(3);
            var native = new FakeNative(main) { AppearanceAvailable = true };
            using var presentation = new SourceWindowPresentation(native);
            presentation.Observe(main);
            native.AddWindow(source);
            presentation.OnShown(source);
            Check(presentation.TryGetPreopenTarget(source, created: false, out _));
            presentation.OnTransfer(source, TransferResult.Unsafe);
            Check(!presentation.TryGetPreopenTarget(source, created: false, out _));
            native.AddWindow(next);
            native.AddWindow(main, valid: false);
            Check(!presentation.TryGetPreopenTarget(next, created: true, out _));
            return Task.CompletedTask;
        }),
        ("CREATE suppresses a new invisible frame before SHOW without minimizing", () =>
        {
            var main = Id(1); var source = Id(2);
            var native = new FakeNative(main) { AppearanceAvailable = true };
            using var presentation = new SourceWindowPresentation(native);
            presentation.Observe(main);
            native.AddWindow(source, visible: false);
            presentation.OnCreated(source);
            Check(native.Style(source) == 0x80000 && native.Attributes(source).Alpha == 0);
            Check(!native.IsVisible(source) && native.MinimizeAttempts.Count == 0);
            native.SetVisible(source, true);
            presentation.OnShown(source);
            Check(native.IsVisible(source) && native.Attributes(source).Alpha == 0 && native.MinimizeAttempts.Count == 0);
            presentation.Dispose();
            Check(native.Style(source) == 0 && native.IsVisible(source));
            return Task.CompletedTask;
        }),
        ("Skipped CREATE stays eligible for SHOW while startup main and iconic frames stay protected", () =>
        {
            var main = Id(1); var existing = Id(3); var source = Id(2);
            var native = new FakeNative(main, existing) { AppearanceAvailable = true };
            native.SetMinimized(existing, true);
            using var presentation = new SourceWindowPresentation(native);
            native.AddWindow(source, visible: false);
            presentation.OnCreated(source); // No published main: must not mark seen.
            Check(native.Style(source) == 0);
            presentation.Observe(main);
            presentation.OnCreated(main); presentation.OnShown(existing);
            Check(native.Style(main) == 0 && native.Style(existing) == 0);
            native.SetVisible(source, true); presentation.OnShown(source);
            Check(native.Attributes(source).Alpha == 0 && native.Style(source) == 0x80000);
            return Task.CompletedTask;
        }),
        ("Prelayered attributes restore exactly while unrelated new extended-style bits survive", () =>
        {
            var main = Id(1); var source = Id(2);
            var native = new FakeNative(main) { AppearanceAvailable = true };
            using var presentation = new SourceWindowPresentation(native);
            native.AddWindow(source);
            var original = new LayeredWindowAttributes(0x123456, 73, 3);
            native.SetAppearance(source, 0x80010, original);
            presentation.OnShown(source, main);
            Check(native.Attributes(source) == original with { Alpha = 0 });
            native.SetAppearance(source, native.Style(source) | 0x80, native.Attributes(source));
            presentation.OnTransfer(source, TransferResult.Unsafe);
            Check(native.Style(source) == 0x80090 && native.Attributes(source) == original);
            Check(native.MinimizeAttempts.Count == 0 && presentation.NextExpiryDelay is null);
            return Task.CompletedTask;
        }),
        ("Unsnapshotable prelayered and partial alpha-write failures use reversible minimize fallback", () =>
        {
            foreach (var prelayered in new[] { false, true })
            {
                var main = Id(1); var source = Id(2);
                var native = new FakeNative(main) { AppearanceAvailable = true, LayeredReadSucceeds = !prelayered, AlphaWriteFailures = prelayered ? 0 : 1 };
                using var presentation = new SourceWindowPresentation(native);
                native.AddWindow(source);
                var original = new LayeredWindowAttributes(7, 123, 3);
                native.SetAppearance(source, prelayered ? 0x80000u : 0u, original);
                presentation.OnShown(source, main);
                Check(native.Style(source) == (prelayered ? 0x80000u : 0u) && native.Attributes(source) == original);
                Check(native.MinimizeAttempts.SequenceEqual([source]));
                presentation.OnTransfer(source, TransferResult.Unsafe);
                Check(native.Restored.SequenceEqual([source]));
            }
            return Task.CompletedTask;
        }),
        ("Queued alpha ownership restores on timeout, main loss, disposal and live claimed completion", () =>
        {
            foreach (var mode in new[] { 0, 1, 2, 3, 4 })
            {
                var main = Id(1); var source = Id(2);
                var native = new FakeNative(main) { AppearanceAvailable = true };
                var clock = new FakeClock();
                using var presentation = new SourceWindowPresentation(native, clock);
                native.AddWindow(source); presentation.OnShown(source, main);
                presentation.OnTransfer(source, TransferResult.Queued);
                Check(native.Attributes(source).Alpha == 0 && native.Style(source) == 0x80000);
                if (mode == 0) { clock.Advance(TimeSpan.FromSeconds(4)); presentation.ObserveCurrentMain(); }
                else if (mode == 1) presentation.Observe(null);
                else if (mode == 2) presentation.Dispose();
                else presentation.OnTransfer(source, mode == 3 ? TransferResult.Completed : TransferResult.SourceGone);
                Check(native.Style(source) == 0 && native.IsVisible(source) && presentation.NextExpiryDelay is null);
                var writes = native.StyleWrites;
                presentation.OnShown(source, main);
                Check(native.StyleWrites == writes && native.MinimizeAttempts.Count == 0);
            }
            return Task.CompletedTask;
        }),
        ("Failed exact alpha rollback makes frame visible and retries at a bounded cadence", () =>
        {
            var main = Id(1); var source = Id(2);
            var native = new FakeNative(main) { AppearanceAvailable = true };
            var clock = new FakeClock();
            using var presentation = new SourceWindowPresentation(native, clock);
            native.AddWindow(source); presentation.OnShown(source, main);
            native.SetAppearance(source, native.Style(source) | 0x80, native.Attributes(source));
            native.StyleWriteFailures = 2;
            presentation.OnTransfer(source, TransferResult.Unsafe);
            Check(native.Style(source) == 0x80 && presentation.NextExpiryDelay == TimeSpan.FromMilliseconds(100));
            var writes = native.StyleWrites;
            presentation.ObserveCurrentMain();
            Check(native.StyleWrites == writes && presentation.NextExpiryDelay == TimeSpan.FromMilliseconds(100));
            clock.Advance(TimeSpan.FromMilliseconds(100)); presentation.ObserveCurrentMain();
            Check(native.Style(source) == 0x80 && presentation.NextExpiryDelay is null);
            return Task.CompletedTask;
        }),
        ("Alpha ownership releases on user restore and never touches a recycled identity", () =>
        {
            var main = Id(1); var source = Id(2);
            var native = new FakeNative(main) { AppearanceAvailable = true };
            var clock = new FakeClock();
            using var presentation = new SourceWindowPresentation(native, clock);
            native.AddWindow(source); presentation.OnShown(source, main);
            clock.Advance(TimeSpan.FromMilliseconds(250)); native.SetMinimized(source, true); presentation.ObserveCurrentMain();
            native.SetMinimized(source, false); presentation.OnShown(source);
            Check(native.Style(source) == 0 && presentation.NextExpiryDelay is null);
            var writes = native.StyleWrites; presentation.OnShown(source); Check(native.StyleWrites == writes);
            var gone = Id(3); native.AddWindow(gone); presentation.OnShown(gone);
            native.SetValid(gone, false); var recycled = new WindowId(gone.Handle, gone.ProcessId + 1); native.AddWindow(recycled);
            writes = native.StyleWrites; presentation.OnTransfer(gone, TransferResult.SourceGone);
            Check(native.StyleWrites == writes && native.Style(recycled) == 0);
            return Task.CompletedTask;
        }),
        ("Only a new visible extra window is temporarily minimized", () =>
        {
            var main = Id(1);
            var source = Id(2);
            var native = new FakeNative(main);
            var clock = new FakeClock();
            using var presentation = new SourceWindowPresentation(native, clock);
            native.AddWindow(source);

            presentation.OnShown(source, main);

            Check(native.MinimizeAttempts.SequenceEqual([source]));
            Check(native.Activated.SequenceEqual([main]));
            Check(native.IsMinimized(source));
            Check(presentation.NextExpiryDelay == TimeSpan.FromSeconds(4));
            return Task.CompletedTask;
        }),
        ("First, main, existing, already minimized, and invalid windows are not minimized", () =>
        {
            var first = Id(10);
            var firstNative = new FakeNative();
            using (var presentation = new SourceWindowPresentation(firstNative))
            {
                firstNative.AddWindow(first);
                presentation.OnShown(first, null);
                Check(firstNative.MinimizeAttempts.Count == 0);
            }

            var main = Id(20);
            var mainNative = new FakeNative();
            using (var presentation = new SourceWindowPresentation(mainNative))
            {
                mainNative.AddWindow(main);
                presentation.OnShown(main, main);
                Check(mainNative.MinimizeAttempts.Count == 0);
            }

            var existing = Id(31);
            var existingNative = new FakeNative(main, existing);
            using (var presentation = new SourceWindowPresentation(existingNative))
            {
                presentation.OnShown(existing, main);
                Check(existingNative.MinimizeAttempts.Count == 0);
            }

            var minimized = Id(32);
            var minimizedNative = new FakeNative(main);
            using (var presentation = new SourceWindowPresentation(minimizedNative))
            {
                minimizedNative.AddWindow(minimized, minimized: true);
                presentation.OnShown(minimized, main);
                Check(minimizedNative.MinimizeAttempts.Count == 0);
                Check(minimizedNative.Restored.Count == 0);
            }

            var invalid = Id(33);
            var invalidNative = new FakeNative(main);
            using (var presentation = new SourceWindowPresentation(invalidNative))
            {
                invalidNative.AddWindow(invalid, valid: false);
                presentation.OnShown(invalid, main);
                Check(invalidNative.MinimizeAttempts.Count == 0);
            }

            return Task.CompletedTask;
        }),
        ("A failed minimize does not create a restore obligation", () =>
        {
            var main = Id(1);
            var source = Id(2);
            var native = new FakeNative(main) { MinimizeSucceeds = false };
            using var presentation = new SourceWindowPresentation(native);
            native.AddWindow(source);

            presentation.OnShown(source, main);
            presentation.RestoreAll();

            Check(native.MinimizeAttempts.SequenceEqual([source]));
            Check(native.Restored.Count == 0);
            Check(presentation.NextExpiryDelay is null);
            return Task.CompletedTask;
        }),
        ("An accepted pending minimize keeps ownership through grace and queues its inverse", () =>
        {
            var main = Id(1);
            var source = Id(2);
            var native = new FakeNative(main) { MinimizeBecomesIconic = false };
            var clock = new FakeClock();
            using var presentation = new SourceWindowPresentation(native, clock);
            native.AddWindow(source);
            presentation.OnShown(source, main);
            Check(!native.IsMinimized(source));

            clock.Advance(TimeSpan.FromMilliseconds(201));
            presentation.Observe(main);
            Check(presentation.NextExpiryDelay == TimeSpan.FromMilliseconds(3_799));
            Check(!native.IsMinimized(source));

            clock.Advance(TimeSpan.FromMilliseconds(3_799));
            presentation.Observe(main);
            Check(native.Restored.SequenceEqual([source]));
            Check(native.Commands.SequenceEqual([("minimize", source), ("restore", source)]));

            var disposeNative = new FakeNative(main) { MinimizeBecomesIconic = false };
            var disposeClock = new FakeClock();
            var disposePresentation = new SourceWindowPresentation(disposeNative, disposeClock);
            var disposeSource = Id(3);
            disposeNative.AddWindow(disposeSource);
            disposePresentation.OnShown(disposeSource, main);
            disposeClock.Advance(TimeSpan.FromMilliseconds(201));
            disposePresentation.Observe(main);
            Check(disposePresentation.NextExpiryDelay is not null);
            disposePresentation.Dispose();
            Check(disposeNative.Restored.SequenceEqual([disposeSource]));
            Check(disposeNative.Commands.SequenceEqual([("minimize", disposeSource), ("restore", disposeSource)]));
            return Task.CompletedTask;
        }),
        ("Retry and unsafe outcomes restore only the window minimized by this helper", () =>
        {
            foreach (var result in new[] { TransferResult.Retry, TransferResult.Unsafe })
            {
                var main = Id(1);
                var source = Id(2);
                var unrelated = Id(3);
                var native = new FakeNative(main, unrelated);
                native.SetMinimized(unrelated, true);
                using var presentation = new SourceWindowPresentation(native);
                native.AddWindow(source);
                presentation.OnShown(source, main);

                presentation.OnTransfer(source, result);

                Check(native.Restored.SequenceEqual([source]));
                Check(native.IsMinimized(unrelated));
                Check(presentation.NextExpiryDelay is null);
            }

            return Task.CompletedTask;
        }),
        ("Not-ready remains bounded, then restores without re-minimizing a later SHOW", () =>
        {
            var main = Id(1);
            var source = Id(2);
            var native = new FakeNative(main);
            var clock = new FakeClock();
            using var presentation = new SourceWindowPresentation(native, clock);
            native.AddWindow(source);
            presentation.OnShown(source, main);
            presentation.OnTransfer(source, TransferResult.NotReady);

            Check(presentation.NextExpiryDelay == TimeSpan.FromSeconds(4));
            clock.Advance(TimeSpan.FromMilliseconds(3_999));
            presentation.Observe(main);
            Check(native.IsMinimized(source));
            Check(presentation.NextExpiryDelay == TimeSpan.FromMilliseconds(1));

            clock.Advance(TimeSpan.FromMilliseconds(1));
            presentation.Observe(main);
            Check(native.Restored.SequenceEqual([source]));
            Check(presentation.NextExpiryDelay is null);
            presentation.OnShown(source, main);
            Check(native.MinimizeAttempts.SequenceEqual([source]));
            return Task.CompletedTask;
        }),
        ("Completed and gone sources release ownership without restoration", () =>
        {
            foreach (var result in new[] { TransferResult.Completed, TransferResult.SourceGone })
            {
                var main = Id(1);
                var source = Id(2);
                var native = new FakeNative(main);
                using var presentation = new SourceWindowPresentation(native);
                native.AddWindow(source);
                presentation.OnShown(source, main);

                native.SetValid(source, false);
                presentation.OnTransfer(source, result);
                presentation.RestoreAll();

                Check(native.Restored.Count == 0);
                Check(presentation.NextExpiryDelay is null);
            }

            return Task.CompletedTask;
        }),
        ("A user restore after the show grace relinquishes ownership", () =>
        {
            var main = Id(1);
            var source = Id(2);
            var native = new FakeNative(main);
            var clock = new FakeClock();
            using var presentation = new SourceWindowPresentation(native, clock);
            native.AddWindow(source);
            presentation.OnShown(source, main);

            native.SetMinimized(source, false);
            clock.Advance(TimeSpan.FromMilliseconds(200));
            presentation.Observe(main);
            native.SetMinimized(source, true);
            presentation.OnShown(source, main);
            presentation.RestoreAll();

            Check(native.MinimizeAttempts.SequenceEqual([source]));
            Check(native.Restored.Count == 0);
            Check(presentation.NextExpiryDelay is null);
            return Task.CompletedTask;
        }),
        ("An invalidated source is never restored through a recycled handle", () =>
        {
            var main = Id(1);
            var source = Id(2);
            var recycled = new WindowId(source.Handle, source.ProcessId + 1);
            var native = new FakeNative(main);
            using var presentation = new SourceWindowPresentation(native);
            native.AddWindow(source);
            presentation.OnShown(source, main);

            native.SetValid(source, false);
            native.AddWindow(recycled);
            presentation.OnTransfer(source, TransferResult.Retry);

            Check(native.Restored.Count == 0);
            Check(!native.IsMinimized(recycled));
            Check(presentation.NextExpiryDelay is null);
            return Task.CompletedTask;
        }),
        ("RestoreAll and Dispose restore only owned minimized windows", () =>
        {
            foreach (var disposeDirectly in new[] { false, true })
            {
                var main = Id(1);
                var source = Id(2);
                var unrelated = Id(3);
                var native = new FakeNative(main, unrelated);
                native.SetMinimized(unrelated, true);
                var presentation = new SourceWindowPresentation(native);
                native.AddWindow(source);
                presentation.OnShown(source, main);

                if (disposeDirectly)
                    presentation.Dispose();
                else
                {
                    presentation.RestoreAll();
                    presentation.Dispose();
                }

                Check(native.Restored.SequenceEqual([source]));
                Check(native.IsMinimized(unrelated));
            }

            return Task.CompletedTask;
        }),
        ("Queued close retains presentation ownership until disappearance or timeout", () =>
        {
            var main = Id(1);
            var source = Id(2);
            var native = new FakeNative(main);
            var clock = new FakeClock();
            using var presentation = new SourceWindowPresentation(native, clock);
            native.AddWindow(source);
            presentation.OnShown(source, main);
            presentation.OnTransfer(source, TransferResult.Queued);
            Check(native.Restored.Count == 0 && native.IsMinimized(source));
            clock.Advance(TimeSpan.FromSeconds(4));
            presentation.Observe(main);
            Check(native.Restored.SequenceEqual([source]));

            var gone = Id(3);
            native.AddWindow(gone);
            presentation.OnShown(gone, main);
            presentation.OnTransfer(gone, TransferResult.Queued);
            native.SetValid(gone, false);
            presentation.Observe(main);
            Check(native.Restored.SequenceEqual([source]) && presentation.NextExpiryDelay is null);
            return Task.CompletedTask;
        }),
        ("A hidden preloaded frame is treated as a new window when first shown", () =>
        {
            var main = Id(1);
            var source = Id(2);
            var native = new FakeNative(main, source);
            native.AddWindow(source, visible: false);
            using var presentation = new SourceWindowPresentation(native);
            presentation.Observe(main);
            native.AddWindow(source, visible: true);
            presentation.OnShown(source);
            Check(native.MinimizeAttempts.SequenceEqual([source]));
            Check(native.IsMinimized(source));
            return Task.CompletedTask;
        }),
        ("A new iconic frame remains eligible at its first normal show", () =>
        {
            var main = Id(1);
            var source = Id(2);
            var native = new FakeNative(main);
            using var presentation = new SourceWindowPresentation(native);
            presentation.Observe(main);
            native.AddWindow(source, minimized: true);
            presentation.OnShown(source);
            Check(native.MinimizeAttempts.Count == 0);
            native.SetMinimized(source, false);
            presentation.OnShown(source);
            Check(native.MinimizeAttempts.SequenceEqual([source]));
            presentation.OnTransfer(source, TransferResult.Unsafe);
            presentation.OnShown(source);
            Check(native.MinimizeAttempts.SequenceEqual([source]));
            return Task.CompletedTask;
        }),
        ("Native show and expiry use the last published main without coordinator access", () =>
        {
            var main = Id(1);
            var source = Id(2);
            var native = new FakeNative(main);
            var clock = new FakeClock();
            using var presentation = new SourceWindowPresentation(native, clock);
            presentation.Observe(main);
            native.AddWindow(source);
            presentation.OnShown(source);
            Check(native.MinimizeAttempts.SequenceEqual([source]) && native.Activated.SequenceEqual([main]));
            clock.Advance(TimeSpan.FromSeconds(4));
            presentation.ObserveCurrentMain();
            Check(native.Restored.SequenceEqual([source]));
            presentation.Observe(null);
            var next = Id(3);
            native.AddWindow(next);
            presentation.OnShown(next);
            Check(native.MinimizeAttempts.SequenceEqual([source]));
            presentation.Dispose();
            presentation.OnShown(Id(4));
            presentation.ObserveCurrentMain();
            Check(native.Restored.SequenceEqual([source]));
            return Task.CompletedTask;
        }),
        ("Losing the main window restores an owned extra", () =>
        {
            var main = Id(1);
            var source = Id(2);
            var native = new FakeNative(main);
            using var presentation = new SourceWindowPresentation(native);
            native.AddWindow(source);
            presentation.OnShown(source, main);

            presentation.Observe(null);

            Check(native.Restored.SequenceEqual([source]));
            Check(presentation.NextExpiryDelay is null);
            return Task.CompletedTask;
        })
    ];

    private static WindowId Id(int value) => new(value, 100);

    private static void Check(bool condition)
    {
        if (!condition)
            throw new InvalidOperationException("Presentation contract assertion failed");
    }

    private sealed class FakeClock : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;

        internal void Advance(TimeSpan value) => _timestamp += value.Ticks;
    }

    private sealed class FakeNative : ISourceWindowPresentationNative
    {
        private readonly WindowId[] _existing;
        private readonly Dictionary<WindowId, WindowState> _windows = [];

        internal List<WindowId> MinimizeAttempts { get; } = [];
        internal List<WindowId> Restored { get; } = [];
        internal List<WindowId> Activated { get; } = [];
        internal List<(string Action, WindowId Window)> Commands { get; } = [];
        internal bool MinimizeSucceeds { get; set; } = true;
        internal bool MinimizeBecomesIconic { get; set; } = true;
        internal bool AppearanceAvailable { get; set; }
        internal bool LayeredReadSucceeds { get; set; } = true;
        internal int AlphaWriteFailures { get; set; }
        internal int StyleWriteFailures { get; set; }
        internal int StyleWrites { get; private set; }
        internal int AlphaWrites { get; private set; }
        internal Action? BeforeStyleWrite { get; set; }
        private readonly Dictionary<WindowId, NativeSourceAppearanceSnapshot> nativeSnapshots = [];
        private readonly HashSet<WindowId> nativeClaims = [];
        internal List<WindowId> NativeClaims { get; } = [];
        internal List<WindowId> NativeReleased { get; } = [];
        internal List<(uint Style, LayeredWindowAttributes Attributes)> ReleaseAppearances { get; } = [];
        internal TimeSpan NativeRemaining { get; set; } = TimeSpan.FromSeconds(4);
        internal bool NativeClaimUnavailable { get; set; }
        private readonly HashSet<WindowId> nativeRestoring = [];
        internal List<WindowId> NativeRestoreBegins { get; } = [];
        internal int NativeRestoreBeginFailures { get; set; }
        internal bool NativeRestoreBeginThrows { get; set; }
        internal int NativeWritesBeforeRestore { get; private set; }
        internal bool ReassertOnStyleWrites { get; set; }
        internal int NativeReassertions { get; private set; }

        internal FakeNative(params WindowId[] existing)
        {
            _existing = existing.ToArray();
            foreach (var window in existing)
                AddWindow(window);
        }

        internal void AddWindow(WindowId window, bool valid = true, bool visible = true, bool minimized = false) =>
            _windows[window] = new WindowState(valid, visible, minimized);

        internal void SetValid(WindowId window, bool value) => _windows[window].Valid = value;
        internal void SetMinimized(WindowId window, bool value) => _windows[window].Minimized = value;
        internal void SetVisible(WindowId window, bool value) => _windows[window].Visible = value;
        internal uint Style(WindowId window) => _windows[window].Style;
        internal LayeredWindowAttributes Attributes(WindowId window) => _windows[window].Attributes;
        internal void SetAppearance(WindowId window, uint style, LayeredWindowAttributes attributes)
        { _windows[window].Style = style; _windows[window].Attributes = attributes; }
        internal void SuppressNatively(WindowId window, uint style, LayeredWindowAttributes attributes, ulong cookie = 1)
        {
            nativeSnapshots[window] = new(7, 10, 11, 13, window.ProcessId, 20, cookie, 4000,
                (style & 0x80000) != 0 ? 7u : 1u, style, attributes);
            nativeClaims.Remove(window);
            nativeRestoring.Remove(window);
            SetAppearance(window, style | 0x80000, new(attributes.ColorKey, 0, (style & 0x80000) != 0 ? attributes.Flags | 2 : 2));
        }
        internal void RestoreNatively(WindowId window)
        {
            if (!nativeSnapshots.Remove(window, out var snapshot)) return;
            nativeClaims.Remove(window);
            nativeRestoring.Remove(window);
            SetAppearance(window, snapshot.OriginalStyle, snapshot.Attributes);
        }
        public NativeSourceAppearanceClaim TryClaimNativeAppearance(WindowId window, out NativeSourceAppearanceSnapshot snapshot)
        {
            if (!nativeSnapshots.TryGetValue(window, out snapshot)) return NativeSourceAppearanceClaim.None;
            if (NativeClaimUnavailable || !nativeClaims.Add(window)) return NativeSourceAppearanceClaim.Unavailable;
            NativeClaims.Add(window); return NativeSourceAppearanceClaim.Claimed;
        }
        public bool OwnsNativeAppearance(WindowId window, NativeSourceAppearanceSnapshot snapshot) =>
            nativeClaims.Contains(window) && nativeSnapshots.TryGetValue(window, out var current) && snapshot == current;
        internal bool NativeRestoring(WindowId window) => nativeRestoring.Contains(window);
        public bool BeginNativeAppearanceRestore(WindowId window, NativeSourceAppearanceSnapshot snapshot)
        {
            if (!OwnsNativeAppearance(window, snapshot)) return false;
            if (NativeRestoreBeginFailures > 0)
            {
                NativeRestoreBeginFailures--;
                if (NativeRestoreBeginThrows) throw new InvalidOperationException("Synthetic restore handoff failure");
                return false;
            }
            NativeRestoreBegins.Add(window); nativeRestoring.Add(window); return true;
        }
        public bool ReleaseNativeAppearance(WindowId window, NativeSourceAppearanceSnapshot snapshot)
        {
            if (!OwnsNativeAppearance(window, snapshot)) return false;
            NativeReleased.Add(window); ReleaseAppearances.Add((Style(window), Attributes(window)));
            nativeSnapshots.Remove(window); nativeClaims.Remove(window); nativeRestoring.Remove(window); return true;
        }
        internal void ReassertNative(WindowId window)
        {
            if (!nativeSnapshots.TryGetValue(window, out var snapshot) || nativeRestoring.Contains(window)) return;
            NativeReassertions++;
            SetAppearance(window, Style(window) | 0x80000, new(snapshot.Attributes.ColorKey, 0,
                (snapshot.OriginalStyle & 0x80000) != 0 ? snapshot.Attributes.Flags | 2 : 2));
        }
        public TimeSpan NativeAppearanceRemaining(NativeSourceAppearanceSnapshot snapshot) => NativeRemaining;

        public IReadOnlyList<WindowId> GetExistingWindows() => _existing;
        public bool IsValid(WindowId window) => _windows.TryGetValue(window, out var state) && state.Valid;
        public bool IsVisible(WindowId window) => IsValid(window) && _windows[window].Visible;
        public bool IsMinimized(WindowId window) => IsValid(window) && _windows[window].Minimized;
        public bool TryGetExtendedStyle(WindowId window, out uint style)
        { style = IsValid(window) ? _windows[window].Style : 0; return AppearanceAvailable && IsValid(window); }
        public bool TrySetExtendedStyle(WindowId window, uint style)
        {
            if (nativeClaims.Contains(window) && !nativeRestoring.Contains(window)) NativeWritesBeforeRestore++;
            BeforeStyleWrite?.Invoke();
            StyleWrites++;
            if (!IsValid(window)) return false;
            if (StyleWriteFailures > 0) { StyleWriteFailures--; return false; }
            _windows[window].Style = style;
            if (ReassertOnStyleWrites) ReassertNative(window);
            return true;
        }
        public bool TryGetLayeredAttributes(WindowId window, out LayeredWindowAttributes attributes)
        { attributes = IsValid(window) ? _windows[window].Attributes : default; return IsValid(window) && LayeredReadSucceeds; }
        public bool TrySetLayeredAttributes(WindowId window, LayeredWindowAttributes attributes)
        {
            if (nativeClaims.Contains(window) && !nativeRestoring.Contains(window)) NativeWritesBeforeRestore++;
            AlphaWrites++;
            if (!IsValid(window)) return false;
            if (AlphaWriteFailures > 0) { AlphaWriteFailures--; return false; }
            _windows[window].Attributes = attributes; return true;
        }

        public bool Minimize(WindowId window)
        {
            MinimizeAttempts.Add(window);
            Commands.Add(("minimize", window));
            if (!IsValid(window) || !MinimizeSucceeds) return false;
            if (MinimizeBecomesIconic)
                _windows[window].Minimized = true;
            return true;
        }

        public void Restore(WindowId window)
        {
            Restored.Add(window);
            Commands.Add(("restore", window));
            if (IsValid(window)) _windows[window].Minimized = false;
        }

        public void Activate(WindowId window) => Activated.Add(window);
    }

    private sealed class FakeAppearanceProperties : INativeSourceAppearanceProperties
    {
        private readonly WindowId window;
        internal NativeSourceAppearanceOwner Owner { get; } = new(7, 100, 11, 13);
        internal Dictionary<string, ulong> Values { get; }
        internal List<string> Removed { get; } = [];
        internal bool WindowCurrent { get; set; } = true;
        internal Action<string>? AfterRemove { get; set; }
        internal Action<string, ulong>? AfterSet { get; set; }
        internal bool RejectRestore { get; set; }
        public ulong TickCount => 1000;
        internal FakeAppearanceProperties(WindowId window)
        {
            this.window = window;
            Values = new() { ["Flags"] = 1, ["OwnerPid"] = Owner.ProcessId, ["OwnerStart"] = Owner.ProcessStart,
                ["NonceLow"] = Owner.NonceLow, ["NonceHigh"] = Owner.NonceHigh, ["WindowPid"] = window.ProcessId,
                ["WindowStart"] = 200, ["WindowCookie"] = 1, ["Deadline"] = 1400, ["OriginalExStyle"] = 0,
                ["OriginalColorKey"] = 0, ["OriginalAlpha"] = 255, ["OriginalFlags"] = 2, ["Claimed"] = 0 };
        }
        private static string Name(string name) => name[NativeSourceAppearance.Prefix.Length..];
        public ulong Get(WindowId window, string name) => this.window == window ? Values.GetValueOrDefault(Name(name)) : 0;
        public bool Set(WindowId window, string name, ulong value)
        {
            if (this.window != window || RejectRestore && Name(name) == "Claimed" && value == 2) return false;
            var key = Name(name); Values[key] = value; AfterSet?.Invoke(key, value); return true;
        }
        public ulong Remove(WindowId window, string name)
        {
            if (this.window != window) return 0;
            var key = Name(name); Removed.Add(key);
            var removed = Values.Remove(key, out var value); AfterRemove?.Invoke(key);
            return removed ? value : 0;
        }
        public bool IsControllerCurrent(uint process, ulong start) => process == Owner.ProcessId && start == Owner.ProcessStart;
        public bool IsWindowCurrent(WindowId window, ulong start) => WindowCurrent && this.window == window && start == 200;
    }

    private sealed class WindowState(bool valid, bool visible, bool minimized)
    {
        internal bool Valid { get; set; } = valid;
        internal bool Visible { get; set; } = visible;
        internal bool Minimized { get; set; } = minimized;
        internal uint Style { get; set; }
        internal LayeredWindowAttributes Attributes { get; set; } = new(0, 255, 2);
    }
}
