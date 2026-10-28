// Single source of truth for the product version.
// build.cmd compiles this file; AppInfo reads the value back at runtime so the
// UI, --version and the Explorer file properties can never drift apart.
using System.Reflection;

[assembly: AssemblyTitle("SnipOCR")]
[assembly: AssemblyDescription("Portable screen OCR for Windows (RapidOCR / PP-OCRv6)")]
[assembly: AssemblyProduct("SnipOCR")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyCopyright("MIT License")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]
[assembly: AssemblyInformationalVersion("1.1.0")]
