#!/usr/bin/env bash
# Builds GZJam for this Mac into a disk image: the steps the release workflow takes (a
# self-contained publish, then package.sh: GZJam.app, signed ad hoc, in a .dmg), for a build kept to
# yourself. Run it on a Mac.
#
#   packaging/macos/build-local.sh [version]
#       Publishes here (needs the .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0),
#       then packages. The same as CI does.
#
#   packaging/macos/build-local.sh --from <folder> [version]
#       Packages a folder published somewhere else for this Mac (dotnet publish -r osx-arm64
#       --self-contained, say on Windows): no .NET SDK needed here. It first does what
#       lib/bass/Bass.targets does on a Mac build: bass_fx and bassmix load libbass through
#       @rpath, which doesn't include their own folder, so they are pointed at it.
#
# The version defaults to 1.2.0-navidrome.<today>. The .dmg lands in dist/ beside this repo.
# Like the release builds it is signed ad hoc only, so macOS asks for confirmation the first time:
# see "Windows protected your PC / macOS may say" in the README (System Settings > Privacy &
# Security > Open Anyway, or: xattr -dr com.apple.quarantine /Applications/GZJam.app).
set -euo pipefail

if [[ "$(uname -s)" != Darwin ]]; then
  echo "This makes the Mac app, so it has to run on a Mac." >&2
  exit 1
fi

here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
arch=arm64
[[ "$(uname -m)" == x86_64 ]] && arch=x64   # follows the Mac it runs on

from=""
if [[ "${1:-}" == "--from" ]]; then
  from="${2:?--from needs the published folder}"
  shift 2
fi
version="${1:-1.2.0-navidrome.$(date +%Y%m%d)}"
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$ ]]; then
  echo "'$version' isn't a version like 1.2.0 or 1.2.0-navidrome.1" >&2
  exit 1
fi

if [[ -z "$from" ]]; then
  if ! command -v dotnet > /dev/null; then
    echo "The .NET 10 SDK isn't installed: https://dotnet.microsoft.com/download/dotnet/10.0" >&2
    echo "(Or publish on another computer and use --from <folder>.)" >&2
    exit 1
  fi
  prefix="${version%%-*}"
  suffix=""
  [[ "$version" == *-* ]] && suffix="${version#*-}"
  publish="$repo/publish/osx-$arch"
  rm -rf "$publish"
  dotnet publish "$repo/src/GZJam.App" -c Release --self-contained \
    -p:VersionPrefix="$prefix" -p:VersionSuffix="$suffix" -r "osx-$arch" -o "$publish"
else
  publish="$(cd "$from" && pwd)"
  if [[ ! -f "$publish/GZJam" || ! -f "$publish/libbass.dylib" ]]; then
    echo "$publish doesn't look like GZJam published for macOS (no GZJam or libbass.dylib in it)." >&2
    exit 1
  fi
  for lib in "$publish"/libbass*.dylib; do
    [[ "$(basename "$lib")" == libbass.dylib ]] && continue
    if otool -L "$lib" | grep -q '@rpath/libbass.dylib'; then
      install_name_tool -change @rpath/libbass.dylib @loader_path/libbass.dylib "$lib"
    fi
  done
fi

bash "$here/package.sh" "$publish" "$version" "$arch" "$repo/dist"
echo
echo "Done: $repo/dist/GZJam-$version-macos-$arch.dmg"
echo "Open it, drag GZJam to Applications, and the first time right-click it > Open."
