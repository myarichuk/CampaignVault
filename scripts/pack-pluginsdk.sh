#!/usr/bin/env bash
# Packs CampaignVault.PluginSdk into a .nupkg (with license + readme embedded).
# Publishing to nuget.org is a separate, deliberate step — see the printed command at the end.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
csproj="$repo_root/src/CampaignVault.PluginSdk/CampaignVault.PluginSdk.csproj"
out_dir="$repo_root/nupkgs"

if [[ ! -f "$csproj" ]]; then
  echo "error: csproj not found at $csproj" >&2
  exit 1
fi

version="$(grep -oE '<Version>[^<]+</Version>' "$csproj" | sed -E 's/<\/?Version>//g')"
if [[ -z "$version" ]]; then
  echo "error: could not read <Version> from $csproj" >&2
  exit 1
fi

echo "Packing CampaignVault.PluginSdk v$version ..."
rm -rf "$out_dir"
dotnet pack "$csproj" -c Release -o "$out_dir"

pkg="$out_dir/CampaignVault.PluginSdk.$version.nupkg"
if [[ ! -f "$pkg" ]]; then
  echo "error: expected package not found at $pkg" >&2
  exit 1
fi

echo
echo "Built: $pkg"
echo
echo "Contents (license/readme should be listed):"
unzip -l "$pkg" | grep -Ei 'license|readme|\.nuspec' || true

echo
echo "To publish, run:"
echo "  dotnet nuget push \"$pkg\" --api-key \$NUGET_API_KEY --source https://api.nuget.org/v3/index.json --skip-duplicate"
