#!/usr/bin/env bash
# Construit les versions exportées de Planet (Linux et Windows) dans build/.
#
# Il faut Godot 4.7.2 .NET et ses modèles d'export (Éditeur > Gérer les modèles d'export >
# Télécharger), ainsi que le SDK .NET 8 (Godot compile lui-même le code C# à l'export). Usage :
#   tools/export.sh                      # Godot trouvé dans le PATH sous le nom « godot »
#   GODOT=/chemin/vers/Godot tools/export.sh
#   tools/export.sh Linux                # une seule plateforme (Linux ou Windows)
set -euo pipefail

cd "$(dirname "$0")/.."
GODOT="${GODOT:-godot}"
VERSION="$(sed -n 's/^config\/version="\(.*\)"/\1/p' project.godot)"
PRESETS=("${@:-Linux Windows}")
read -r -a PRESETS <<< "${PRESETS[*]}"

echo "Planet ${VERSION} : vérification des tests"
dotnet run -c Release --project tests/Planet.Tests -- --quick

for preset in "${PRESETS[@]}"; do
    case "$preset" in
        Linux)   out="build/linux/Planet.x86_64" ;;
        Windows) out="build/windows/Planet.exe" ;;
        *) echo "Préréglage inconnu : $preset (Linux ou Windows)" >&2; exit 1 ;;
    esac
    mkdir -p "$(dirname "$out")"
    echo "Export ${preset} → ${out}"
    "$GODOT" --headless --export-release "$preset" "$out"
    cp LICENSE README.md "$(dirname "$out")/"
    archive="build/Planet-${VERSION}-$(echo "$preset" | tr '[:upper:]' '[:lower:]').zip"
    (cd "$(dirname "$out")" && zip -qr "../$(basename "$archive")" .)
    echo "Archive : ${archive}"
done
