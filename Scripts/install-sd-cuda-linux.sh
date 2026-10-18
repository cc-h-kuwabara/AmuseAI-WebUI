#!/usr/bin/env bash
set -euo pipefail
data=$(realpath -m -- "${1:-${XDG_DATA_HOME:-$HOME/.local/share}/Amuse/Web}")
target="$data/RuntimeStableDiffusionCpp/linux-cuda"
if [[ -e "$target" ]]; then echo "Target exists. Stop Amuse and choose a new data directory or move the old runtime first: $target" >&2; exit 1; fi
for tool in git cmake nvcc nvidia-smi; do command -v "$tool" >/dev/null || { echo "Required tool missing: $tool (install NVIDIA driver, CUDA toolkit and build tools)." >&2; exit 1; }; done
nvidia-smi --query-gpu=name --format=csv,noheader
stage=$(mktemp -d -t amuse-cuda-XXXXXXXX)
trap 'rm -rf -- "$stage"' EXIT
git clone --depth 1 --branch master-968-8cd731e --recurse-submodules --shallow-submodules https://github.com/leejet/stable-diffusion.cpp.git "$stage/source"
cmake -S "$stage/source" -B "$stage/build" -DCMAKE_BUILD_TYPE=Release \
  -DSD_CUDA=ON -DSD_BUILD_SHARED_LIBS=ON -DSD_BUILD_EXAMPLES=OFF \
  -DSD_WEBP=OFF -DSD_WEBM=OFF -DCMAKE_CUDA_ARCHITECTURES=native
cmake --build "$stage/build" --parallel "${AMUSE_BUILD_JOBS:-2}"
library=$(find "$stage/build" -name libstable-diffusion.so -type f -print -quit)
[[ -n "$library" ]] || { echo "Built shared library not found." >&2; exit 1; }
mkdir -p -- "$target"
cp "$library" "$target/libstable-diffusion.so"
echo "Installed: $target"
echo "Start Amuse with --data-dir '$data', select the NVIDIA device and Linux NVIDIA CUDA environment."
