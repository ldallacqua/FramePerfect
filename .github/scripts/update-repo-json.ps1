# Updates repo.json, the Dalamud custom plugin repository, from the manifest that
# DalamudPackager writes next to FramePerfect.dll in a Release build.
#
# Everything players see in /xlplugins (name, description, tags, version, API level)
# comes from that manifest, so repo.json can't drift from the plugin itself.
# The download links and the other repository-only fields are kept as they are.
#
#   ./.github/scripts/update-repo-json.ps1 -Manifest bin/Release/FramePerfect.json -RepoJson repo.json

param(
    [Parameter(Mandatory)] [string] $Manifest,
    [Parameter(Mandatory)] [string] $RepoJson
)

$ErrorActionPreference = 'Stop'

$m = Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json
if (-not $m.AssemblyVersion -or -not $m.DalamudApiLevel) {
    throw "$Manifest has no AssemblyVersion or DalamudApiLevel: use the manifest from a Release build, not the source FramePerfect.json."
}

$repo = Get-Content -LiteralPath $RepoJson -Raw | ConvertFrom-Json
$entry = $repo | Where-Object InternalName -eq $m.InternalName
if (-not $entry) { throw "$RepoJson has no entry with InternalName '$($m.InternalName)'." }

$fromManifest = 'Author', 'Name', 'InternalName', 'AssemblyVersion', 'Punchline', 'Description',
    'ApplicableVersion', 'Tags', 'CategoryTags', 'DalamudApiLevel', 'LoadPriority', 'AcceptsFeedback'

foreach ($field in $fromManifest) {
    if ($null -eq $m.$field) { continue }
    if ($entry.PSObject.Properties[$field]) { $entry.$field = $m.$field }
    else { $entry | Add-Member -NotePropertyName $field -NotePropertyValue $m.$field }
}

$entry.LastUpdate = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()

$json = (ConvertTo-Json -InputObject @($repo) -Depth 10) -replace "`r`n", "`n"
[IO.File]::WriteAllText((Resolve-Path -LiteralPath $RepoJson), "$json`n")

Write-Host "repo.json: $($m.Name) $($m.AssemblyVersion), API $($m.DalamudApiLevel)"
