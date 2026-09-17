#!/bin/sh
set -eu

# Fail fast if the Wine major version drifts from the verified release.
# The console executable does not need a desktop or X server.
wine_version="$(wine --version 2>/dev/null || true)"
case "$wine_version" in
  wine-9.0*) ;;
  *) echo "unexpected wine version: '${wine_version}' (expected wine-9.0.x)" >&2; exit 1;;
esac

# Initialize before accepting requests, avoiding first-request initialization races.
wineboot --init
wineserver --wait
exec "$@"
