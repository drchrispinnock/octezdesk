#!/bin/sh
# Builds OctezDesk.app and OctezDesk-<version>.dmg into macos/dist.
#   scripts/build-app.sh 0.3.0           # version for Info.plist and the DMG name (default: 0.3.0-dev)
#   SIGN_IDENTITY="Developer ID Application: …" scripts/build-app.sh 0.3.0   # sign properly (default: ad hoc)
# Universal (arm64 + x86_64) when both architectures build; set ARCHS=arm64 to build one.
set -eu

version="${1:-0.3.0-dev}"
here=$(cd "$(dirname "$0")/.." && pwd)
repo=$(cd "$here/.." && pwd)
dist="$here/dist"
app="$dist/OctezDesk.app"
archs="${ARCHS:-arm64 x86_64}"
sign="${SIGN_IDENTITY:--}"

cd "$here"
echo "== Testing"
(cd "$repo/common" && swift test -q)
swift test -q

echo "== Building release ($archs)"
arch_flags=""
for a in $archs; do arch_flags="$arch_flags --arch $a"; done
# shellcheck disable=SC2086
swift build -c release $arch_flags
bin_dir=$(swift build -c release $arch_flags --show-bin-path)

rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp "$bin_dir/OctezDesk" "$app/Contents/MacOS/OctezDesk"

echo "== Icon"
iconset="$dist/AppIcon.iconset"
rm -rf "$iconset"; mkdir -p "$iconset"
for size in 16 32 128 256 512; do
  sips -z $size $size "$repo/assets/icon.png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z $double $double "$repo/assets/icon.png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/AppIcon.icns"
rm -rf "$iconset"

cat > "$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>OctezDesk</string>
  <key>CFBundleDisplayName</key><string>OctezDesk</string>
  <key>CFBundleIdentifier</key><string>org.octezdesk.mac</string>
  <key>CFBundleExecutable</key><string>OctezDesk</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.finance</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSHumanReadableCopyright</key><string>OctezDesk</string>
</dict>
</plist>
EOF

echo "== Signing ($sign)"
codesign --force --options runtime --timestamp=none --sign "$sign" "$app"

echo "== DMG"
dmg="$dist/OctezDesk-$version.dmg"
staging="$dist/dmg"
rm -rf "$staging" "$dmg"; mkdir -p "$staging"
cp -R "$app" "$staging/"
ln -s /Applications "$staging/Applications"
hdiutil create -volname "OctezDesk $version" -srcfolder "$staging" -ov -format UDZO "$dmg" >/dev/null
rm -rf "$staging"
echo "Built $app and $dmg"
