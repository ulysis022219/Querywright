# Bundled components

Microsoft.SqlServer.TransactSql.ScriptDom 180.117.0: MIT, Microsoft Corporation.
Source: https://github.com/microsoft/SqlScriptDOM
License retrieved September 28, 2026 from the upstream main branch LICENSE;
the NuGet package independently declares MIT. The package's repository commit
was not available through the public raw URL when checked.

The VSIX bundles ScriptDOM and its localized resources. Visual Studio SDK reference
assemblies are excluded from runtime assets; SSMS provides those host services.
Inspect the final archive whenever dependencies change; new bundled components need
their own license notices. Build-only packages retain their upstream licenses.
