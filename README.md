# X-RAY

Local-first Windows 11 diagnostics and storage analyzer (WPF, .NET 10). Works fully offline, needs no administrator
rights, sends no telemetry and never deletes anything.

## Features

- **Overview** – live CPU, memory and GPU (utilisation + temperature when the driver reports it), drive capacity,
  most recent scan and last snapshot, quick actions.
- **Disk Analyzer** – parallel, cancellable scan of any folder or drive; interactive squarified treemap
  (nested up to 4 levels, single-visual rendering, small items merged), breadcrumbs, sortable list
  (size / name / files / modified), largest-files view, details pane, Explorer integration.
  Access-denied folders, vanished items and links (junctions, symlinks, mount points – never followed) are
  reported, never silently treated as empty. Logical size or estimated size on disk.
- **Performance** – session history charts for CPU, memory, GPU and disk throughput; per-process CPU/memory;
  per-adapter GPU details (DXGI + GPU performance counters + D3DKMT, no vendor tools).
- **Large Files** – minimum size, extension, age and review-category filters; review hints for installers,
  archives, disk images, build output, temp files, large logs, crash dumps and backups. Hints are suggestions only;
  Windows-managed stores (WinSxS, Windows\Installer, …) are never flagged.
- **Duplicates** – finds byte-identical files (size → first/last 16 KiB → full SHA-256), ignores hard links and
  never downloads cloud-only files. Optional cleanup moves selected copies to the Recycle Bin only: it needs
  confirmation listing every path, always keeps at least one copy per group (re-checked on disk), skips files changed
  since the scan, and refuses Windows/Program Files/ProgramData and drives without a Recycle Bin.
- **History** – snapshots of folder sizes (gzip JSON), comparison that separates measured changes from
  changes that cannot be compared (different scope, unreadable folders, truncated snapshots), delete with confirmation.
- **Settings** – dark/light theme, size units, size metric, default scan folder, treemap appearance,
  polling interval, snapshot depth, history retention, opt-in start with Windows.

Data lives in `%LOCALAPPDATA%\X-RAY` (settings, state, snapshots, rotating logs kept 14 days).

## Structure

```
XRay.slnx
src/XRay.Core/         Platform-neutral logic: scanning, aggregation, treemap layout, large-file search,
                       review heuristics, snapshots + comparison, settings, JSON persistence, file logger
src/XRay.App/          WPF app (MVVM, CommunityToolkit.Mvvm, Microsoft.Extensions.DI/Logging)
  Controls/            TreemapControl, SparklineChart, category palette
  Services/            Theme, dialogs, shell, navigation, session, startup registration
  Services/Metrics/    CPU, memory, GPU, disk and process samplers + polling service
  ViewModels/, Views/, Themes/, Converters/
tests/XRay.Core.Tests/ xUnit tests (synthetic data and temporary folders only)
tools/                 Icon generator and a UI-automation helper used during development
```

## Build, test, run

Requires the .NET 10 SDK on Windows.

```bash
dotnet test tests/XRay.Core.Tests -c Release
```

```bash
dotnet build XRay.slnx -c Release
```

```bash
dotnet run --project src/XRay.App -c Release
```

## Publish a self-contained build

```bash
dotnet publish src/XRay.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/win-x64
```

The result is `publish/win-x64/XRay.exe`, which runs on Windows 10/11 x64 without an installed .NET runtime.

## Known limitations

- Size on disk is an estimate (cluster rounding); compressed, sparse and cloud files query their real allocation.
  NTFS-resident tiny files, hard links and deduplication are not modelled.
- Folders Windows denies to a normal user (e.g. parts of `C:\Windows`, other users' profiles) are not read;
  they are listed and totals are marked as lower bounds. Run as administrator only if you need them.
- GPU temperature appears only when the graphics driver exposes it to Windows; dedicated memory size comes from DXGI.
- CPU time of protected system processes is not readable without administrator rights.
- Network drives are not listed on the overview (they can block); they can still be scanned by path.
