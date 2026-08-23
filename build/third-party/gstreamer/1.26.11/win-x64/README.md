# Pinned GStreamer runtime

Add these two files from the official GStreamer Windows MSVC package when a
release should include the optional runtime:

- `gstreamer-1.0-msvc-x86_64-1.26.11.msi`
- `gstreamer-1.0-msvc-x86_64-1.26.11.msi.sha256sum`

The checksum file must contain the SHA-256 published by GStreamer. The package
script verifies the MSI before extracting it and does not use the network when
both files are present and valid. A release may omit GStreamer; the application
remains installable and reports the unavailable media runtime through its normal
settings and diagnostics.

Source: <https://gstreamer.freedesktop.org/data/pkg/windows/1.26.11/msvc/>

The MSI is a third-party redistributable dependency, not Robot Command source.
Its license and notices remain governed by GStreamer and the notices shipped
with the application package.
