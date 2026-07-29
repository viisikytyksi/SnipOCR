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
