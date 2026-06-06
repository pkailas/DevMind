# Archived: DevMind Visual Studio extension (VSIX)

This folder holds the **abandoned net48 Visual Studio extension** (`DevMind.csproj` and its
sources), archived 2026-06-05 when the VSIX was removed from `DevMind.slnx`. Nothing in the
active solution referenced it — it was a terminal deliverable. It only *consumed*
`DevMind.Core` (via ProjectReference).

## Contents
- `DevMind.csproj` (+ `.user`) — the net48 VSIX project (was rooted at the repo root)
- `DevMind*.cs`, `DevMindToolWindowControl.*`, `DiffBatchBar.*`, `DiffPreviewCard.*`,
  `PackageIds.cs`, `ProfileManager.cs`, `TrainingLogger.cs` — VSIX source (formerly repo root)
- `DevMindCommandTable.vsct`, `extension.vsixmanifest`, `source.extension.vsixmanifest`,
  `build.counter` — VSIX packaging/manifests
- `Properties/AssemblyInfo.cs`, `Properties/PublishProfiles/FolderProfile.pubxml` — VSIX
  assembly info + the net48 publish profile

## To restore
1. Move every file/folder here back to the repo root (the `.cs`/`.xaml`/`.vsct` files and
   `Properties/` lived at the root; the VSIX project globbed root files and excluded the
   sibling project folders).
2. Re-add to `DevMind.slnx`:
   ```xml
   <Project Path="DevMind.csproj">
     <Platform Solution="*|arm64" Project="arm64" />
     <Platform Solution="*|x86" Project="x86" />
   </Project>
   ```
   and restore the `arm64` / `x86` solution `<Platform>` entries.
