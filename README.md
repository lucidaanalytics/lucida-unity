# Lucida for Unity

UPM package `dev.lucida.sdk` (Unity 2021.3+). Add by git URL pointing at `sdks/unity`, or copy the folder into `Packages/`.

```csharp
LucidaSdk.Init(new LucidaOptions { ApiKey = "lc_pk_...", Host = "https://app.lucida.dev",
                                   ConsentMode = ConsentMode.OptIn });
LucidaSdk.OptIn();                       // consent first: nothing is written to PlayerPrefs before it
LucidaSdk.Capture("level_complete", new Dictionary<string, object> { { "level", 3 } });
LucidaSdk.Identify("user-42");
if (LucidaSdk.IsEnabled("new-shop")) { /* ... */ }
```

- `Runtime/Core/*.cs` is engine-free (no `UnityEngine`): consent (`opt_in` / `opt_out`, optional queue-before-consent held in
  memory), anonymous UUIDv7 id, `Capture` / `Identify` / `Alias` / `Group` / `Screen`, a persistent batching queue
  (`POST /v1/batch`, flush at 20 events or 5 s, exponential backoff with jitter, at-least-once with a uuid per event),
  and flags (`POST /v1/flags` plus a cached last response; optional bootstrap definitions evaluate locally and pass the
  shared `testdata/flags` vectors, except multi-kind contexts).
- `Runtime/Unity/*.cs` (guarded by `UNITY_5_3_OR_NEWER`) holds the adapters: `UnityWebRequestHttp` (the `IHttp` port),
  `PlayerPrefsStore` (the `IKeyValueStore` port) and the `LucidaSdk` facade with a hidden driver that ticks the queue each
  frame and flushes on pause and quit.
- Session replay (wireframe, off by default): `new LucidaOptions { SessionReplay = true }`. `Runtime/Core/Wireframe.cs`
  turns a UI tree into the same rrweb mobile wireframe the iOS/Android/Flutter SDKs send (Meta, `navigation`,
  FullSnapshot, taps) and `ReplayRecorder` sends changed snapshots to `POST /v1/replay` (gzip, `seq` per window). It
  needs analytics and replay consent before the UI is read. Text and input values are starred and images are marked
  masked by default (`ReplayOptions.Mask` turns that off); passwords are always starred. `UnityWireframeSource` walks
  every root uGUI Canvas (Text, InputField, Button, Image, plus TextMeshPro by reflection); add `LucidaReplayUnmask`
  or `LucidaReplayBlock` to a GameObject to unmask it or blank it out. 3D scenes are not captured, and taps are read
  from the legacy Input Manager.

Tests: `Tests/Runtime` (core, async/lifecycle, replay) runs in the Unity Test Runner (NUnit). Without Unity: `cd Tests.Core && dotnet test` compiles
`Runtime/Core` and the same tests plus the shared flag vectors from the repo's `testdata/flags`.
Unity generates `.meta` files on first import; they are not committed here.
The `Runtime/Unity` glue has not been compiled here (no Unity Editor on the build host).

---

This repository is a read-only mirror of the Lucida monorepo (synced by a script, never edited by hand); releases and provenance attestations are built here by GitHub Actions. Issues are welcome, changes land upstream.
