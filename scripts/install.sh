#!/bin/bash
# macOS counterpart of install.ps1: publishes bobar-cli and bobar and installs them for the current user.
#  1. dotnet publish of both (self-contained, single-file, osx-arm64 or osx-x64) into dist/
#  2. wraps them in dist/BobarCode.app and copies it to ~/Applications (closing a running GUI first)
#  3. links ~/.local/bin/bobar-cli to the CLI inside the bundle (and puts ~/.local/bin on PATH in ~/.zprofile)
#  4. copies the breview, bplan, bexplore-cleanup and bdiscuss skills to ~/.claude/skills/
# Options: --skip-publish, --no-skill, --install-dir <dir> (default ~/Applications)
set -euo pipefail

install_dir="$HOME/Applications"
skip_publish=0
no_skill=0
while [ $# -gt 0 ]; do
    case "$1" in
        --skip-publish) skip_publish=1 ;;
        --no-skill) no_skill=1 ;;
        --install-dir) install_dir="$2"; shift ;;
        *) echo "unknown option: $1" >&2; exit 2 ;;
    esac
    shift
done

root="$(cd "$(dirname "$0")/.." && pwd)"
dist="$root/dist"
app="$dist/BobarCode.app"
case "$(uname -m)" in arm64) rid=osx-arm64 ;; *) rid=osx-x64 ;; esac
# dotnet-install.sh puts the SDK in ~/.dotnet without touching PATH.
command -v dotnet >/dev/null || export PATH="$HOME/.dotnet:$PATH"

if [ "$skip_publish" = 0 ]; then
    rm -rf "$dist"
    for proj in src/Bobar.Cli/Bobar.Cli.csproj src/Bobar.App/Bobar.App.csproj; do
        echo "Publishing $proj..."
        dotnet publish "$root/$proj" -c Release -r "$rid" --self-contained true -p:PublishSingleFile=true \
            -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o "$dist/bin" -nologo -v quiet
    done

    mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
    cp "$dist/bin/bobar" "$dist/bin/bobar-cli" "$app/Contents/MacOS/"

    iconset="$dist/icon.iconset"
    mkdir -p "$iconset"
    for s in 16 32 128 256 512; do
        sips -z $s $s "$root/src/Bobar.App/Assets/icon.png" --out "$iconset/icon_${s}x${s}.png" >/dev/null
        sips -z $((s * 2)) $((s * 2)) "$root/src/Bobar.App/Assets/icon.png" --out "$iconset/icon_${s}x${s}@2x.png" >/dev/null
    done
    iconutil -c icns "$iconset" -o "$app/Contents/Resources/icon.icns"

    version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/Directory.Build.props")"
    cat > "$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>BobarCode</string>
    <key>CFBundleDisplayName</key><string>BobarCode</string>
    <key>CFBundleIdentifier</key><string>local.bobarcode.app</string>
    <key>CFBundleExecutable</key><string>bobar</string>
    <key>CFBundleIconFile</key><string>icon</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleVersion</key><string>$version</string>
    <key>CFBundleShortVersionString</key><string>$version</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>LSMinimumSystemVersion</key><string>12.0</string>
</dict>
</plist>
EOF
    codesign --force --deep --sign - "$app" >/dev/null 2>&1
fi

target="$install_dir/BobarCode.app"
# Close a running GUI from this install (not the CLI, which may be mid-review).
for pid in $(pgrep -x bobar || true); do
    if [ "$(ps -o comm= -p "$pid")" = "$target/Contents/MacOS/bobar" ]; then
        echo "Closing the running BobarCode..."
        kill "$pid" 2>/dev/null || true
        for _ in 1 2 3 4 5 6 7 8 9 10; do kill -0 "$pid" 2>/dev/null || break; sleep 0.5; done
        kill -9 "$pid" 2>/dev/null || true
    fi
done

mkdir -p "$install_dir"
rm -rf "$target"
cp -R "$app" "$target"
echo "Installed to $target"

bin="$HOME/.local/bin"
mkdir -p "$bin"
ln -sf "$target/Contents/MacOS/bobar-cli" "$bin/bobar-cli"
echo "Linked $bin/bobar-cli"
case ":$PATH:" in
    *":$bin:"*) ;;
    *)
        if ! grep -qs '.local/bin' "$HOME/.zprofile"; then
            echo 'export PATH="$HOME/.local/bin:$PATH"' >> "$HOME/.zprofile"
            echo "Added $bin to PATH in ~/.zprofile (open a new terminal to pick it up)."
        fi
        ;;
esac

if [ "$no_skill" = 0 ]; then
    for name in breview bplan bexplore-cleanup bdiscuss; do
        mkdir -p "$HOME/.claude/skills/$name"
        cp "$root/skill/$name/SKILL.md" "$HOME/.claude/skills/$name/"
        echo "Skill installed to $HOME/.claude/skills/$name"
    done
    rm -rf "$HOME/.claude/skills/lx-review" # the breview skill used to be called lx-review
fi
echo "Done. Put your provider keys in ~/Library/Application Support/BobarCode/keys.env (NAME=value lines)."
