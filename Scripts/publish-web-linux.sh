#!/usr/bin/env bash
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
output=$(realpath -m -- "${1:-$root/artifacts/publish/Amuse.Web-linux-x64}")
if [[ -e "$output" ]]; then
  echo "Output already exists. Choose a new output directory: $output" >&2
  exit 1
fi
stage=$(mktemp -d -t amuse-publish-XXXXXXXX)
trap 'rm -rf -- "$stage"' EXIT
for name in Web Host.OnnxRuntime Host.StableDiffusionCpp; do
  dotnet publish "$root/Amuse.$name/Amuse.$name.csproj" -c Release \
    -p:AmusePortable=true -p:BaseIntermediateOutputPath=obj/linux/ -p:BaseOutputPath=bin/linux/ \
    -r linux-x64 --self-contained true -o "$stage/$name" --nologo
done
mkdir -p -- "$output"
for name in Web Host.OnnxRuntime Host.StableDiffusionCpp; do cp -a "$stage/$name/." "$output/"; done
cp "$root/Amuse.Web/README.md" "$output/README.md"
mkdir -p "$output/Scripts"
cp "$root/Scripts/install-sd-cuda-linux.sh" "$output/Scripts/install-sd-cuda-linux.sh"
chmod +x "$output/Amuse.Web" "$output/AmuseHost.OnnxRuntime" "$output/AmuseHost.StableDiffusionCpp"
echo "Published: $output"
echo "Start: $output/Amuse.Web --port 8080"
