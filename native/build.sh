#!/usr/bin/env sh
# Builds the native library. Run from anywhere:  sh native/build.sh
# Linux x64 (tested, gcc 13.3.0):   native/lib/linux-x64/libscholarship_native.so
# Windows x64 (cross-compiled with MinGW-w64 when installed): native/lib/win-x64/scholarship_native.dll
# macOS (not tested):  cc -shared -fPIC -O2 -o native/lib/osx/libscholarship_native.dylib native/scholarship_native.c
set -eu
here="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$here/lib/linux-x64" "$here/lib/win-x64"
gcc -std=c11 -Wall -Wextra -Werror -O2 -shared -fPIC \
    -o "$here/lib/linux-x64/libscholarship_native.so" "$here/scholarship_native.c"
echo "built $here/lib/linux-x64/libscholarship_native.so"
if command -v x86_64-w64-mingw32-gcc >/dev/null 2>&1; then
  x86_64-w64-mingw32-gcc -std=c11 -Wall -Wextra -Werror -O2 -shared -static -static-libgcc \
      -o "$here/lib/win-x64/scholarship_native.dll" "$here/scholarship_native.c"
  echo "built $here/lib/win-x64/scholarship_native.dll"
fi
