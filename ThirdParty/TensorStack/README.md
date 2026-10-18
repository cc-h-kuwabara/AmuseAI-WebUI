# TensorStack Linux source adapter

These Apache-2.0 sources are derived from [saddam213/TensorStack](https://github.com/saddam213/TensorStack), commit `197529a4a7083803f515af03849acdbdea0083b1` (version 0.14.1). See LICENSE. Windows builds continue to use the original NuGet packages. The source adapter is selected only with `AmusePortable=true`, which is the default on Linux.

The upstream NuGet packages target Windows, including WPF and DirectML. This copy builds the non-UI components for net10.0, uses the CPU ONNX Runtime package, Linux OpenCV/Skia native assets and system FFmpeg, and loads the pinned StableDiffusion.cpp Linux shared library. The Headless adapter retains the existing namespaces for shared model/source compatibility, but contains no WPF runtime or window types. UI-only font, visual-tree, statistics and desktop history components are excluded from the portable build.

StableDiffusion.cpp native ABI is pinned to `master-968-8cd731e`. Update its native declarations and the Linux environment download together. HuggingFace's bundled Python host remains Windows-only and is explicitly rejected on Linux.
