# MCP stdio wrapper для V2: запускает DocsWalker.Mcp.exe из publish/.
# MCP-клиент (Claude Code и т.п.) подключается так:
#   mcpServers."docswalker" = {
#       "command": "powershell.exe",
#       "args": [
#           "-NoProfile", "-ExecutionPolicy", "Bypass",
#           "-File", "scripts\\docswalker-mcp.ps1"
#       ]
#   }
# Перед использованием опубликуйте Mcp в Release/win-x64:
#   dotnet publish src/DocsWalker.Mcp -c Release -r win-x64
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
Set-Location -LiteralPath $repoRoot

$mcpExe = Join-Path $repoRoot 'src\DocsWalker.Mcp\bin\Release\net10.0\win-x64\publish\DocsWalker.Mcp.exe'
if (-not (Test-Path -LiteralPath $mcpExe)) {
    [Console]::Error.WriteLine("DocsWalker.Mcp.exe not found: $mcpExe")
    [Console]::Error.WriteLine("Publish it first: dotnet publish src/DocsWalker.Mcp -c Release -r win-x64")
    exit 1
}
& $mcpExe --quiet=true
exit $LASTEXITCODE
