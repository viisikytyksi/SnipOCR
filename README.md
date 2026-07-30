# SnipOCR

Portable offline screen OCR tool for Windows.

- WinForms / .NET Framework 4.8
- No admin rights required
- Uses Windows.Media.Ocr or bundled Tesseract when `dist/tesseract/` is present locally
- Snip-style region selection with clipboard copy
- OCR modes including table mode for Excel TSV / Markdown table copy

## Build

Run `build.cmd` on Windows with the Windows SDK available.

## Distribution

Copy `dist/` to the target PC. The local `dist/tesseract/` runtime is intentionally not tracked in GitHub because some bundled DLLs exceed GitHub's normal file size limit.

## License

MIT License. See [LICENSE](LICENSE).

### Third-party components

This repository does not contain Tesseract. When you build a distribution
folder, you place `tesseract.exe`, its DLLs, and `*.traineddata` under
`dist/tesseract/` yourself. Those files keep their own licenses:

- Tesseract OCR — Apache License 2.0
- tessdata_best language data — Apache License 2.0
- Bundled DLLs from the UB-Mannheim build — see the licenses shipped with them

Redistribute them together with their license files.
