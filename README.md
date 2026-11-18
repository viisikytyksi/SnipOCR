# SnipOCR

Portable offline screen OCR tool for Windows. Current release: **1.2.0**
([changelog](CHANGELOG.md)).

- WinForms / .NET Framework 4.8, no admin rights, no installer
- Snap a region, get text (and Excel/Markdown tables) on the clipboard
- OCR engine: **RapidOCR (PP-OCRv6 small)** on ONNX Runtime, with
  Windows.Media.Ocr as the only fallback

## Why RapidOCR

The engines earlier versions shipped both struggle with Japanese UI text
around 11-16 px:

| engine | 11-20 px Japanese (CER) | notes |
| --- | --- | --- |
| PP-OCRv6 small (RapidOCR) | **0.5 %** | 10 MB det + 21 MB rec, CPU only |
| PP-OCRv5 mobile + PP-OCRv4 japan | 9.6 % | no Japanese model exists in v5 |
| Tesseract (jpn) | — | removed in 1.2.0: slower than RapidOCR and less accurate |
| Windows.Media.Ocr | — | garbles "プレビュー" into "プレ乙-" |

CER is character error rate over a synthetic 12-image corpus of 11-20 px Japanese
screen text (menus, dialogs, tables, status bars). The engine is implemented in
`src/RapidOcr.cs`: DB text detection, rotated-rect unclipping, perspective crops
and CTC decoding run locally on ONNX Runtime, so there is no Python, no
PaddleOCR install and no GPU requirement.

Measured on an i5-8400T (6 cores, no AVX-512): ~150-250 ms for a typical
400x300 snippet, ~0.9 s for a full 1280x720 screen, peak working set ~200 MB.
CPU inference is capped at 4 threads and the ONNX CPU arena is disabled so the
resident set stays friendly to 8 GB office PCs.

## Layout

```
src/SnipOCR.cs      app, tray, overlay, settings, engine selection
src/RapidOcr.cs     ONNX Runtime OCR engine (det + rec, DB + CTC)
lib/                onnxruntime.dll + managed wrapper + .NET Framework shims
                    + app-local VC++ runtime (works on PCs without the redist)
models/             PP-OCRv6_det_small.onnx, PP-OCRv6_rec_small.onnx
tools/sdk/          winmd metadata (only needed when the Windows SDK is absent)
build.cmd           compiles with the in-box csc.exe; no VS, no NuGet
```

`lib/`, `models/` and `tools/sdk/` are not tracked (size). Populate them once on
a networked PC:

```
powershell -ExecutionPolicy Bypass -File tools\fetch_deps.ps1
build.cmd
```

`build.cmd` produces a self-contained `build\`, and mirrors the runtime pieces
into `dist\` so that folder can be copied straight to a USB stick.

## Verify on the target PC

```
SnipOCR.exe --selftest              # engine + model availability
SnipOCR.exe --selftest shot.png     # OCR one image to stdout
```

This is the fastest way to tell whether a locked-down PC is missing the models,
blocked by policy, or simply out of language packs.

## Settings

`settings.sample.txt` documents every key. The ones that matter most:

```
engine=auto        # auto | rapidocr | winrt
mode=normal        # normal | line | table | sparse | small
copyformat=auto    # auto | plain | excel | markdown | oneline
hotkey=Ctrl+Shift+S
```

`engine=auto` uses RapidOCR when `rapidocr\` exists and falls back to
Windows.Media.Ocr when it does not. `engine=winrt` pins the OS engine. A stale
`engine=tesseract` from an older settings file is read as `auto`.

## License

MIT License. See [LICENSE](LICENSE).

### Third-party components

- PP-OCRv6 small detector/recognizer (PaddleOCR) — Apache License 2.0
- ONNX Runtime — MIT License
- RapidOCR's converted models are mirrored from ModelScope; SHA256 values are
  pinned in `tools/fetch_deps.ps1`.

Redistribute these together with their license files.
