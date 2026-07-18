#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CONFIGURATION="${1:-Release}"
RID="${2:-osx-arm64}"
OUT_DIR="$ROOT/artifacts/macos"
APP_NAME="Sefirah"
APP_DIR="$OUT_DIR/$APP_NAME.app"
PUBLISH_DIR="$OUT_DIR/publish"
VERSION="${SEFIRAH_VERSION:-3.0.0}"
DMG_PATH="$OUT_DIR/${APP_NAME}-${VERSION}-${RID}.dmg"
VOL_NAME="Sefirah"

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

echo "==> Building native helper"
mkdir -p "$ROOT/native/macos"
clang -dynamiclib -fobjc-arc -O2 \
  -framework Foundation -framework UserNotifications -framework AppKit -framework CoreAudio \
  -install_name @rpath/libsefirah_macos.dylib \
  -o "$ROOT/native/macos/libsefirah_macos.dylib" \
  "$ROOT/native/macos/sefirah_macos.m"

echo "==> Publishing $RID ($CONFIGURATION)"
rm -rf "$PUBLISH_DIR" "$APP_DIR" "$DMG_PATH" "$OUT_DIR/dmg-root"
dotnet publish "$ROOT/src/Sefirah/Sefirah.csproj" \
  -c "$CONFIGURATION" \
  -f net10.0-desktop \
  -r "$RID" \
  --self-contained true \
  -o "$PUBLISH_DIR" \
  -p:PublishSingleFile=false

echo "==> Assembling $APP_NAME.app"
mkdir -p "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources"
cp -R "$PUBLISH_DIR/"* "$APP_DIR/Contents/MacOS/"
cp "$ROOT/native/macos/libsefirah_macos.dylib" "$APP_DIR/Contents/MacOS/"

ICON_SRC="$ROOT/dist/icons/512x512/com.castle.sefirah.png"
if [[ -f "$ICON_SRC" ]]; then
  mkdir -p "$OUT_DIR/Sefirah.iconset"
  sips -z 16 16     "$ICON_SRC" --out "$OUT_DIR/Sefirah.iconset/icon_16x16.png" >/dev/null
  sips -z 32 32     "$ICON_SRC" --out "$OUT_DIR/Sefirah.iconset/icon_16x16@2x.png" >/dev/null
  sips -z 32 32     "$ICON_SRC" --out "$OUT_DIR/Sefirah.iconset/icon_32x32.png" >/dev/null
  sips -z 64 64     "$ICON_SRC" --out "$OUT_DIR/Sefirah.iconset/icon_32x32@2x.png" >/dev/null
  sips -z 128 128   "$ICON_SRC" --out "$OUT_DIR/Sefirah.iconset/icon_128x128.png" >/dev/null
  sips -z 256 256   "$ICON_SRC" --out "$OUT_DIR/Sefirah.iconset/icon_128x128@2x.png" >/dev/null
  sips -z 256 256   "$ICON_SRC" --out "$OUT_DIR/Sefirah.iconset/icon_256x256.png" >/dev/null
  sips -z 512 512   "$ICON_SRC" --out "$OUT_DIR/Sefirah.iconset/icon_256x256@2x.png" >/dev/null
  sips -z 512 512   "$ICON_SRC" --out "$OUT_DIR/Sefirah.iconset/icon_512x512.png" >/dev/null
  sips -z 1024 1024 "$ICON_SRC" --out "$OUT_DIR/Sefirah.iconset/icon_512x512@2x.png" >/dev/null
  iconutil -c icns "$OUT_DIR/Sefirah.iconset" -o "$APP_DIR/Contents/Resources/Sefirah.icns" || true
  rm -rf "$OUT_DIR/Sefirah.iconset"
fi

cat > "$APP_DIR/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key>
  <string>en</string>
  <key>CFBundleDisplayName</key>
  <string>Sefirah</string>
  <key>CFBundleExecutable</key>
  <string>Sefirah</string>
  <key>CFBundleIdentifier</key>
  <string>com.castle.sefirah</string>
  <key>CFBundleInfoDictionaryVersion</key>
  <string>6.0</string>
  <key>CFBundleName</key>
  <string>Sefirah</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>${VERSION}</string>
  <key>CFBundleVersion</key>
  <string>${VERSION}</string>
  <key>CFBundleIconFile</key>
  <string>Sefirah</string>
  <key>LSMinimumSystemVersion</key>
  <string>13.0</string>
  <key>NSHighResolutionCapable</key>
  <true/>
  <key>NSPrincipalClass</key>
  <string>NSApplication</string>
  <key>NSUserNotificationAlertStyle</key>
  <string>alert</string>
  <key>NSLocalNetworkUsageDescription</key>
  <string>Sefirah discovers and connects to your Android device on the local network.</string>
  <key>NSAppleEventsUsageDescription</key>
  <string>Sefirah uses automation for lock, sleep, and login item preferences.</string>
  <key>NSBonjourServices</key>
  <array>
    <string>_sefirah._tcp</string>
  </array>
</dict>
</plist>
PLIST

chmod +x "$APP_DIR/Contents/MacOS/Sefirah" || true

# Optional Developer ID signing when SEFIRAH_SIGN_IDENTITY is set.
if [[ -n "${SEFIRAH_SIGN_IDENTITY:-}" ]]; then
  echo "==> Codesigning with $SEFIRAH_SIGN_IDENTITY"
  codesign --force --deep --options runtime --sign "$SEFIRAH_SIGN_IDENTITY" "$APP_DIR"
  codesign --verify --verbose=2 "$APP_DIR"
fi

echo "==> Creating DMG"
DMG_ROOT="$OUT_DIR/dmg-root"
mkdir -p "$DMG_ROOT"
cp -R "$APP_DIR" "$DMG_ROOT/"
ln -sf /Applications "$DMG_ROOT/Applications"

# UDZO compressed read-only image suitable for GitHub Releases
hdiutil create \
  -volname "$VOL_NAME" \
  -srcfolder "$DMG_ROOT" \
  -ov \
  -format UDZO \
  "$DMG_PATH"

rm -rf "$DMG_ROOT"

# Optional notarization when credentials are configured.
if [[ -n "${SEFIRAH_NOTARY_PROFILE:-}" ]]; then
  echo "==> Submitting for notarization (profile: $SEFIRAH_NOTARY_PROFILE)"
  xcrun notarytool submit "$DMG_PATH" --keychain-profile "$SEFIRAH_NOTARY_PROFILE" --wait
  xcrun stapler staple "$DMG_PATH" || true
  xcrun stapler staple "$APP_DIR" || true
fi

echo ""
echo "==> Artifacts"
echo "  App: $APP_DIR"
echo "  DMG: $DMG_PATH"
echo ""
echo "Upload the DMG to a GitHub Release for users to download."
echo "Open locally with: open \"$DMG_PATH\""
