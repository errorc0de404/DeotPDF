#!/bin/sh
# Adds DeotPDF to "Open with" for PDF files for the current user (no root): a .desktop file in
# ~/.local/share/applications and the icon. It does not make DeotPDF the default PDF app; the desktop asks the user
# for that (or: xdg-mime default deotpdf.desktop application/pdf).
# Usage: ./register-open-with.sh [path/to/executable]   |   ./register-open-with.sh --unregister
set -eu
here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
data=${XDG_DATA_HOME:-$HOME/.local/share}
case "$data" in /*) ;; *) data=$HOME/.local/share ;; esac
desktop=$data/applications/deotpdf.desktop
icon=$data/icons/hicolor/scalable/apps/deotpdf.svg

refresh() {
    command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$data/applications" || true
    command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q "$data/icons/hicolor" || true
}

if [ "${1:-}" = "--unregister" ]; then
    rm -f "$desktop" "$icon"
    refresh
    echo "DeotPDF removed from Open with."
    exit 0
fi

exe=${1:-}
if [ -z "$exe" ]; then
    if [ -x "$here/DeotPDF" ]; then exe=$here/DeotPDF; fi
fi
if [ -z "$exe" ] || [ ! -x "$exe" ]; then echo "Not found: the DeotPDF executable (give its path)" >&2; exit 1; fi
exe=$(CDPATH= cd -- "$(dirname -- "$exe")" && pwd)/$(basename -- "$exe")

mkdir -p "$data/applications" "$(dirname -- "$icon")"
# A path with a quote, backslash, dollar, backtick, | or & would need escaping (Exec, sed); those are refused instead.
case "$exe" in *'"'* | *'\'* | *'`'* | *'$'* | *'|'* | *'&'*) echo "The path has a character Exec cannot hold: $exe" >&2; exit 1 ;; esac
sed "s|{EXEC}|$exe|" "$here/deotpdf.desktop" > "$desktop.tmp"
mv "$desktop.tmp" "$desktop"
if [ -f "$here/deotpdf.svg" ]; then cp "$here/deotpdf.svg" "$icon"; fi
refresh
echo "DeotPDF added to Open with for PDF: $exe"
