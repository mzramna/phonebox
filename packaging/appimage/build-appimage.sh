#!/usr/bin/env bash
# Builds a self-contained PhoneBox agent AppImage (Linux): the .NET runtime is included, so nothing else needs to be
# installed on the computer except adb for USB.
#
#   packaging/appimage/build-appimage.sh [linux-x64|linux-arm64] [output dir]
#
# Needs the .NET 10 SDK and mksquashfs (squashfs-tools). The AppImage runtime (AppImage/type2-runtime, release
# RUNTIME_VERSION below) is downloaded and checked against its pinned SHA-256, unless RUNTIME_FILE points to one
# (which is checked the same way). Settings and logs stay in ~/.config/PhoneBox and ~/.local/share/PhoneBox, as with
# the archive; the agent does not update itself when it runs from an AppImage (replace the file instead).
set -euo pipefail

rid="${1:-linux-x64}"
root="$(cd "$(dirname "$0")/../.." && pwd)"
out="$(realpath -m "${2:-$root/dist}")"
# Pinned AppImage runtime: https://github.com/AppImage/type2-runtime/releases/tag/20251108
RUNTIME_VERSION=20251108
case "$rid" in
  linux-x64) arch=x86_64; runtime_sha256=2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d ;;
  linux-arm64) arch=aarch64; runtime_sha256=00cbdfcf917cc6c0ff6d3347d59e0ca1f7f45a6df1a428a0d6d8a78664d87444 ;;
  *) echo "unsupported RID: $rid" >&2; exit 2 ;;
esac
version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/agent/PhoneBox.Agent/PhoneBox.Agent.csproj" | head -n1)"
command -v mksquashfs >/dev/null || { echo "mksquashfs not found: install squashfs-tools" >&2; exit 1; }

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
app="$work/PhoneBox.AppDir"
mkdir -p "$app/usr/lib/phonebox" "$out"

dotnet publish "$root/agent/PhoneBox.Agent" -c Release -f net10.0 -r "$rid" --self-contained true \
  -o "$app/usr/lib/phonebox" --nologo -v q

cat > "$app/AppRun" <<'EOF'
#!/bin/sh
# PhoneBox agent: settings at http://127.0.0.1:8765/ once it runs.
here="$(dirname "$(readlink -f "$0")")"
exec "$here/usr/lib/phonebox/PhoneBoxAgent" "$@"
EOF
chmod 755 "$app/AppRun"

cat > "$app/phonebox.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=PhoneBox
Comment=Desk panel agent for an old Android phone, a tablet or a browser
Exec=PhoneBoxAgent
Icon=phonebox
Categories=Utility;
Terminal=false
X-AppImage-Version=$version
EOF
cp "$root/packaging/appimage/phonebox.png" "$app/phonebox.png"
ln -s phonebox.png "$app/.DirIcon"

runtime="${RUNTIME_FILE:-$work/runtime}"
if [ -z "${RUNTIME_FILE:-}" ]; then
  curl -fsSL -o "$runtime" "https://github.com/AppImage/type2-runtime/releases/download/$RUNTIME_VERSION/runtime-$arch"
fi
echo "$runtime_sha256  $runtime" | sha256sum -c --quiet - \
  || { echo "AppImage runtime checksum mismatch (expected type2-runtime $RUNTIME_VERSION, runtime-$arch)" >&2; exit 1; }

image="$out/PhoneBox-$version-$arch.AppImage"
mksquashfs "$app" "$work/app.squashfs" -root-owned -noappend -comp zstd -quiet >/dev/null
cat "$runtime" "$work/app.squashfs" > "$image"
chmod 755 "$image"
echo "$image"
