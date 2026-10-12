# Developer notes

Notes for working on the Franchise Collections plugin. For what it does and how to install it, see [README.md](README.md).

## Layout

| File | Role |
|---|---|
| `Plugin.cs` | Plugin entry point: name, GUID, settings page registration. |
| `PluginServiceRegistrator.cs` | Registers `FranchiseCollectionService` (singleton) and `LibraryChangeListener` (hosted service). |
| `Services/FranchiseCollectionService.cs` | The sync logic. Everything that reads or changes collections lives here. |
| `Services/LibraryChangeListener.cs` | Listens for movie add/update events and queues the scheduled task after a quiet period. |
| `Tasks/SyncFranchiseCollectionsTask.cs` | Scheduled task (daily, 4 AM). Jellyfin finds `IScheduledTask` implementations in plugin assemblies on its own, so it isn't registered anywhere. |
| `Tasks/OverrideFranchiseCollectionsTask.cs` | On-demand override sync with no triggers, started by the "Sync and override" button. |
| `Configuration/PluginConfiguration.cs` | Settings, saved as XML by Jellyfin. |
| `Configuration/configPage.html` | Settings page, embedded in the DLL. |
| `package.ps1` | Builds both targets and produces drop-in folders and zips in `artifacts/`. |
| `images/logo.svg` | Logo source. `images/logo.png` (800×450) is rendered from it and used by the catalog (`imageUrl`) and the zips (`imagePath`). |

## How a sync works

1. Load every non-virtual `Movie`, keep those in the selected libraries (`LibraryIds`, empty means all, checked with `ILibraryManager.GetCollectionFolders`), and group them by the `TmdbCollection` provider ID. Jellyfin's TMDb provider sets that ID and `Movie.CollectionName` from TMDb's `belongs_to_collection`.
2. Load every `BoxSet` and index it by its TMDb collection ID and by its name.
3. For each franchise:
   - **Find the collection:** match by TMDb ID first. Failing that, match by exact name, but only if that collection has no TMDb ID of its own. A name-matched collection gets tagged with the ID so later syncs match it by ID.
   - **Skip it** if the TMDb name or the existing collection's name is in the excluded list.
   - **No collection yet, and at least `MinimumMovies` movies:** create one through `ICollectionManager.CreateCollectionAsync` with the TMDb ID set, so Jellyfin downloads the poster and overview.
   - **Collection exists:** add movies that are missing from it. If `RemoveStaleMovies` is on, remove members whose TMDb ID now points to a *different* franchise. Members with no TMDb ID are treated as hand-added and left alone.
   - **Override mode** (`OverrideFranchiseCollectionsTask`, the "Sync and override" button): a name match also takes over a collection whose TMDb ID belongs to no franchise in the (selected) library, and re-tags it. Every member that isn't one of the franchise's movies is removed. Other collections of the same franchise (same TMDb ID, or untagged with the same name) are deleted through `ILibraryManager.DeleteItem`; the movies stay. After the franchise loop, every remaining collection without a `TmdbCollection` ID (hand-made, or from Jellyfin's auto-collections) is deleted too, except names in `ExcludedFranchises`. This cleanup is skipped when no franchises were found, so a run with missing metadata or no matching library can't wipe every collection.
   - **TMDb IDs on collections:** the plugin sets both `TmdbCollection` (what it matches on) and `Tmdb`. Jellyfin's box set metadata provider reads only `Tmdb`, and without it searches TMDb by name, which can land on a different franchise ("RED Collection" → "Red Ryder Collection"). A collection whose `Tmdb` ID points to the wrong franchise is re-tagged and gets a full metadata and image refresh; one with no `Tmdb` ID is only tagged, so custom artwork is kept.
4. Errors are logged and the sync moves on to the next franchise. A `SemaphoreSlim` stops two syncs from running at once.

**Change-triggered syncs:** `LibraryChangeListener` waits until no movie has changed for 2 minutes, but never more than 15 minutes after the first change, then calls `ITaskManager.QueueScheduledTask`. Going through the task manager makes the run show up in the dashboard with progress. It reacts only to `Movie` events, which is why its own collection edits can't re-trigger it.

## Jellyfin server behavior this relies on

Checked against the server source (tags `v10.11.0`, `v10.11.11`, `v12.0`, `v12.2`):

- **Collections are folders on disk.** `CreateCollectionAsync` makes a `<name> [boxset]` folder under the collections folder. Two collections with the same name would share a folder, which is why a name clash gets ` (<tmdbId>)` added to the name.
- **The first name sticks.** Once a collection has a name, a later TMDb metadata refresh doesn't replace it. The fallback name above is permanent unless the user renames the collection.
- **Adding is idempotent.** `AddToCollectionAsync` skips items that are already members, but throws `ArgumentException` for an item ID that no longer exists.
- **Members come back with queries.** In 12.x, collection members live in their own `LinkedChildren` table, and `GetItemList` includes them for `BoxSet` queries. `GetLinkedChildrenInfos()` returns the member items, and the item half of each pair is null when an item can't be resolved.
- **12.x only loads what the query asks for.** Provider IDs, locked fields and user data are only loaded if `InternalItemsQuery.DtoOptions` requests them. The default `DtoOptions` requests everything, which the plugin relies on.
  - **Don't narrow `DtoOptions` on any query whose items you save afterwards.** Saving an item that was loaded without its provider IDs or locked fields can wipe them. The collection query is saved from (the name-match tagging), so leave it alone. The movie query is read-only, so it could safely be narrowed to `ItemFields.ProviderIds` with images and user data turned off for speed.
- **Collection refreshes don't touch movies.** Refreshing a collection only reads its members (for genres, rating, studios and dates), so syncing doesn't fire movie `ItemUpdated` events.
- **`QueueScheduledTask<T>` logs instead of throwing** if the task can't be found. The timer callback still catches everything, because an exception escaping a `System.Threading.Timer` callback kills the server process.
- **`meta.json` must be UTF-8 without a BOM.** For manually installed plugins, Jellyfin reads it with `File.ReadAllBytes` and `JsonSerializer.Deserialize`, which throws on a BOM. Windows PowerShell's `Out-File -Encoding utf8` always writes one, so `package.ps1` uses `[IO.File]::WriteAllText` with `UTF8Encoding($false)`.
- **Which plugins load:** a plugin loads only when `targetAbi` in `meta.json` is no higher than the server version.

## Supporting two Jellyfin versions

One DLL can't serve both server lines, because 10.11 runs .NET 9 and 12.x runs .NET 10. The project targets both frameworks, and each one references the first release of its line:

| Target framework | Jellyfin packages | `targetAbi` |
|---|---|---|
| `net9.0` | 10.11.0 | 10.11.0.0 |
| `net10.0` | 12.0.0 | 12.0.0.0 |

Building against the `.0` release means the plugin runs on every later patch release, as long as the APIs it uses keep the same signatures. They are identical up to 10.11.11 and 12.2.

Every API the plugin uses is the same in 10.11 and 12.x, so there's no `#if` in the code. If a future change needs one, use `#if NET10_0_OR_GREATER` for 12.x-only code.

**To add a new server line** (for example 13.x on `net11.0`):
1. Add the framework to `<TargetFrameworks>` and add a `JellyfinVersion` `PropertyGroup` for it in the `.csproj`.
2. Add an entry to `$targets` in `package.ps1`.
3. Diff the APIs listed above between the old and new tags before trusting it.

The Jellyfin NuGet packages switched from `10.x` to `12.x` version numbers. There is no 11.

## Releasing

The version number is stored in **two places**, and they must match:
- `AssemblyVersion` and `FileVersion` in the `.csproj`
- `$version` in `package.ps1`. This also names the drop-in folder (`Franchise Collections_<version>`) and the zips.

Also update `$changelog` in `package.ps1`.

1. Run `.\package.ps1`. It needs the .NET 10 SDK (which can also build `net9.0`). It deletes `artifacts\`, rebuilds it from scratch, and adds the new version to `manifest.json` with the zips' MD5 checksums.
2. Publish the zips as a GitHub release whose tag is `v<version>`, because the manifest's `sourceUrl`s point there:
   ```powershell
   gh release create v1.0.0.1 artifacts\*.zip --title "1.0.0.1" --notes "..."
   ```
3. Commit and push `manifest.json` to `main`. Servers that use the repository see the update on their next catalog refresh.

Never rebuild an already published version: the new zips get new checksums that no longer match the release, and installs fail.

The manifest lists the 12.x build before the 10.11 build. Both have the same version number, and Jellyfin keeps the first compatible entry, so a 12.x server must see its own build first.

The plugin GUID (`7c1f3c2e-5b8a-4d3e-9f61-2a4b8e0d9c17`) appears in `Plugin.cs`, `configPage.html` and `package.ps1`. Never change it: it's how Jellyfin connects saved settings and upgrades to the plugin.

## Testing

There are no automated tests yet, and **the plugin hasn't been run against a real server**. Smoke test on a throwaway server:

1. Install the plugin and restart. Check that it's listed as Active and that the log has no `Error deserializing` lines.
2. Click **Sync now**. Expect `Found N franchises` and then `Franchise collection sync finished: X created, Y updated` in the log.
3. Add a second movie from a franchise you only had one movie from. About 2 minutes after the scan, its collection should appear.
4. Hand-add an unrelated movie to a franchise collection and sync again. It should stay.
5. Add a franchise to the excluded list and sync. Its collection shouldn't change.
6. Run **Sync now** twice quickly. The second run should log `already running, skipping`.
7. Tick one library only and sync. Franchises only in other libraries shouldn't get collections.
8. Hand-add a movie to a franchise collection, and make a hand-built collection named like a franchise but tagged with an unrelated TMDb ID. Click **Sync and override**: the extra movie is removed and the hand-built collection is reused instead of a "Name (id)" duplicate.

The plugin's log lines come from `Jellyfin.Plugin.FranchiseCollections.Services.*`.

## Known limitations and ideas

- **Movies only.** TMDb collections exist only for movies.
- **It never deletes collections.** That applies when a franchise falls below the minimum, when all its movies are gone, or when it's added to the excluded list.
- **The excluded list matches whole names exactly**, ignoring case, and only one name is entered per line. Commas also split entries, so a franchise name containing a comma can't be excluded.
- **Matching by name can adopt a hand-made collection** that happens to have the TMDb name. After that, stale-movie removal applies to it too.
- **Each sync loads the whole movie library with all fields.** That's fine for typical libraries. For very large ones, narrow `DtoOptions` on the *movie* query (see the warning above).
- **The settings page uses the old dashboard globals** (`Dashboard`, `ApiClient`). They still work in 10.11 and 12.x, but they could break if Jellyfin removes the old plugin page system.
- **Ideas:**
  - optionally delete collections the plugin created that have become empty
  - a "dry run" setting that only logs what it would do
  - unit tests with mocked `ILibraryManager` and `ICollectionManager`
