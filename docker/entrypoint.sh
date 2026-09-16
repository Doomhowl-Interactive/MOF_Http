#!/bin/sh
set -eu

# Initialize before accepting requests, avoiding first-request initialization races.
# The console executable does not need a desktop or X server.
wineboot --init
wineserver --wait
exec "$@"
