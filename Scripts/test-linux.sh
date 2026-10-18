#!/usr/bin/env bash
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
distribution=$(realpath -- "${1:-$root/artifacts/publish/Amuse.Web-linux-x64}")
native=$(realpath -- "${2:?Specify the installed linux-cpu native runtime directory as argument 2}")
[[ -f "$native/libstable-diffusion.so" ]] || { echo "Native runtime missing: $native/libstable-diffusion.so" >&2; exit 1; }
python3 "$root/Scripts/test-web.py" --exe "$distribution/Amuse.Web"
stage=$(mktemp -d -t amuse-smoke-XXXXXXXX)
trap 'rm -rf -- "$stage"' EXIT
python3 "$root/Tests/PortableSmoke/make-fixture.py" "$stage/identity.onnx"
dotnet publish "$root/Tests/PortableSmoke/PortableSmoke.csproj" -c Release -p:AmusePortable=true \
  -p:BaseIntermediateOutputPath=obj/linux/ -p:BaseOutputPath=bin/linux/ \
  -r linux-x64 --self-contained true -o "$stage/app" --nologo
"$stage/app/PortableSmoke" "$root/Amuse.App/Images/Icon.png" "$stage/identity.onnx" "$native"
