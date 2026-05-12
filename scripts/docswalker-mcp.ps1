$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
Set-Location -LiteralPath $repoRoot

$mcpExe = Join-Path $repoRoot 'src\DocsWalker.Mcp\bin\Release\net10.0\win-x64\publish\DocsWalker.Mcp.exe'
if (-not (Test-Path -LiteralPath $mcpExe)) {
    [Console]::Error.WriteLine("DocsWalker.Mcp.exe not found: $mcpExe")
    [Console]::Error.WriteLine("Publish it first: dotnet publish src/DocsWalker.Mcp/DocsWalker.Mcp.csproj -c Release -r win-x64")
    exit 1
}

& $mcpExe --quiet=true
exit $LASTEXITCODE
