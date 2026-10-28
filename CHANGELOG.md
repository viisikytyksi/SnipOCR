# Changelog

All notable changes to SnipOCR are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/), and versions
use [Semantic Versioning](https://semver.org/).

## [1.1.0] - 2026-10-28

Japanese OCR accuracy release. The default engine changed from
Windows.Media.Ocr / Tesseract to RapidOCR with PaddleOCR's PP-OCRv6 small models.

### Added

- **RapidOCR engine** (`src/RapidOcr.cs`): PP-OCRv6 small detector + recognizer
  on ONNX Runtime, with DB text detection, rotated-rect unclipping, perspective
  cropping and CTC greedy decoding implemented locally. No Python, no PaddleOCR
  install, no GPU.
- Tray menu **OCRエンジン** to pin auto / rapidocr / tesseract / winrt.
- Command line diagnostics: `--version` and `--selftest [image]`.
- Table mode now builds Excel TSV directly from detected boxes (gaps inside a row
  become tabs), instead of relying on Tesseract word boxes.
- `tools/fetch_deps.ps1` to populate `lib/`, `models/` and `tools/sdk/` on a
  networked PC, with SHA256 verification for the models.
- App-local Visual C++ runtime DLLs, so a PC without the VC++ redistributable
  still starts (a common reason "portable" OCR tools fail silently).
- `build.cmd` falls back to `tools/sdk` winmd metadata when the Windows SDK is
  not installed.

### Changed

- `engine=auto` now prefers RapidOCR, then Tesseract, then Windows.Media.Ocr.
  Deleting `rapidocr\` restores the previous behaviour.
- Detector resize policy changed to PaddleOCR's `max/960`; RapidOCR's default
  `min/736` upscaled small screen captures into unusable sizes.
- Recognizer input width now follows `max(320, line width)` and buckets by width.
  A fixed 320 px canvas squeezed long lines (CER 88 %), and over-padding broke
  short cells (`部署` → `部`).
- The build is x64 only (the native ONNX Runtime is x64).
- ONNX CPU memory arena disabled and threads capped at 4 to keep the resident
  set friendly to 8 GB office PCs.
- Version information is now real: the exe carries FileVersion/ProductVersion and
  the tray tooltip and About box show it.

### Fixed

- Japanese screen text at 11-20 px is no longer garbled (`プレビュー` → `プレ乙-`,
  `編隼`, `曺式` in the old engines).

### Removed

- Nothing. Tesseract and Windows.Media.Ocr remain as fallbacks.

### Measured

| | this release | previous default |
| --- | --- | --- |
| CER, 11-20 px Japanese (12 images) | **0.49 %** | 17.49 % (Windows.Media.Ocr) |
| 400x300 snippet | ~165 ms, ~130 MB peak | ~155 ms |
| 1280x720 screen | ~0.9-1.1 s, ~250 MB peak | — |
