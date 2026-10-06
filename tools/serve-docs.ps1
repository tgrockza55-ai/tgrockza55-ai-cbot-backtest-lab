# Serve the repo over http://localhost:<port>/ so that pages using ES modules can be previewed without publishing.
# Usage: powershell -ExecutionPolicy Bypass -File tools\serve-docs.ps1 [-Port 8123]
# Then open http://localhost:8123/tools/preview/ha-examples.html (test pages) or /docs/summary.html (needs the Google login).

param([int]$Port = 8123)

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$types = @{ ".html" = "text/html; charset=utf-8"; ".js" = "text/javascript; charset=utf-8"; ".css" = "text/css; charset=utf-8"
            ".json" = "application/json; charset=utf-8"; ".svg" = "image/svg+xml"; ".png" = "image/png"; ".ico" = "image/x-icon" }
$listener = New-Object Net.HttpListener
$listener.Prefixes.Add("http://localhost:$Port/")
$listener.Start()
Write-Host "Serving $root at http://localhost:$Port/  (Ctrl+C to stop)"
try {
    while ($listener.IsListening) {
        $ctx = $listener.GetContext()
        try {
            $path = [Uri]::UnescapeDataString($ctx.Request.Url.AbsolutePath).TrimStart('/')
            if ($path -eq "") { $path = "docs/index.html" }
            $file = [IO.Path]::GetFullPath((Join-Path $root $path))
            if ($file.StartsWith($root) -and (Test-Path $file -PathType Leaf)) {
                $bytes = [IO.File]::ReadAllBytes($file)
                $ext = [IO.Path]::GetExtension($file).ToLowerInvariant()
                $ctx.Response.ContentType = $(if ($types.ContainsKey($ext)) { $types[$ext] } else { "application/octet-stream" })
                $ctx.Response.Headers["Cache-Control"] = "no-store"
                $ctx.Response.ContentLength64 = $bytes.Length
                if ($ctx.Request.HttpMethod -ne "HEAD") { $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length) }   # a HEAD answer has no body
            } else { $ctx.Response.StatusCode = 404 }
        } catch { Write-Host "request failed: $($_.Exception.Message)" }
        try { $ctx.Response.Close() } catch { }
    }
} finally { $listener.Stop() }
