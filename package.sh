#!/usr/bin/env bash
set -euo pipefail
proxy=${1:-http://127.0.0.1:1445}
root=vgmstream-gui
work="$root/.work"
licenses="$work/portable/licenses"
mkdir -p "$licenses/vgmstream-codecs" "$root/dist"
base=https://raw.githubusercontent.com/vgmstream/vgmstream/r2168/ext_libs/licenses
for name in celt-0.11.0.COPYING celt-0.6.1.COPYING ffmpeg-9.0.2.COPYING.LGPLv2.1 ffmpeg-9.0.2.COPYING.LGPLv3 libogg-1.3.5.COPYING libvorbis-1.3.7.COPYING mpg123-1.33.7-COPYING opus-1.6.1-COPYING speex-1.2.1.COPYING; do
    curl -fsS --retry 2 --max-time 25 --proxy "$proxy" "$base/$name" -o "$licenses/vgmstream-codecs/$name"
done
runtime="$HOME/.nuget/packages/microsoft.netcore.app.runtime.win-x64/10.0.12"
cp "$runtime/LICENSE.TXT" "$licenses/dotnet-LICENSE.txt"
cp "$runtime/THIRD-PARTY-NOTICES.TXT" "$licenses/dotnet-THIRD-PARTY-NOTICES.txt"
cp "$HOME/.nuget/packages/microsoft.windowsdesktop.app.runtime.win-x64/10.0.12/LICENSE" "$licenses/wpf-LICENSE.txt"
cp "$work/upstream-build-lib.md" "$licenses/vgmstream-BUILD-LIB.md"
cp "$root/README.md" "$work/portable/README.md"
cp "$work/ui/preview.png" "$root/dist/VgmDesk-preview.png"
python "$root/tests/package.py"
