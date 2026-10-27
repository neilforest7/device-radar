# Identification sources

The bundled snapshots are loaded locally, including when running a single-file build. A user's explicit update downloads complete files from the fixed HTTPS URLs below. The request does not use device IDs, local names, instance paths, addresses, serial numbers, computer IDs or history. Redirects and cookies are disabled; configured system proxies can be used. Updates use a 20-second timeout and an 8 MiB limit per file; an incomplete response does not replace the existing cache. Cache files are validated for minimum size, termination and usable entries; invalid files fall back to bundled data. These checks detect common truncation and format failures, not deliberate tampering. The cache is under `cache/identification` beside the executable.

| Source | Bundled version | Bytes | SHA-256 |
| --- | --- | ---: | --- |
| [USB ID Repository](https://usb-ids.gowdy.us/), [snapshot](https://usb-ids.gowdy.us/usb.ids) | 2026.06.26 | 730605 | `f5a48b0cc8dae1607c2f0bae6b8dc13f2ecef69dbaeaf34b4be8e280d34dba4` |
| [PCI ID Repository](https://pci-ids.ucw.cz/), [snapshot](https://pci-ids.ucw.cz/v2.2/pci.ids) | 2026.10.27 | 1675566 | `d74137d070767260e19a0cd4d80ae9ac39d30cbd2872306490d392bfde4d323a` |

Both official repositories offer GPL-2-or-later or BSD-3-Clause. We select BSD-3-Clause. License texts are [USB](../third-party/USB-LICENSE.txt) and [PCI](../third-party/PCI-LICENSE.txt); the PCI snapshot names its copyright holders in its header. The USB notice follows [Chromium's USB ID license](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/third_party/usb_ids/LICENSE), which distributes the database under BSD-3-Clause. Chromium also uses the database to provide human-readable device descriptions. The upstream USB snapshot contains some non-UTF-8 bytes; these are replaced during decoding. The original bytes remain intact in the snapshot and cache.

Windows names and database descriptions retain separate source labels. A VID/PID or VEN/DEV entry can describe a controller or a generic function, and does not establish a retail model or the identity of a particular physical device. No online identifier lookup is performed. Network retrieval remains a user-initiated operation.
