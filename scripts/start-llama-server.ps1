<#
  Starts llama-server with Qwen3 4B for the MRA Reporting Assistant.

  One-time setup:
    1. Download a Windows CPU build of llama.cpp from
       https://github.com/ggml-org/llama.cpp/releases  (file name contains "bin-win-cpu-x64")
       and unzip it to C:\dev\llama.cpp
    2. Download Qwen3-4B-Q4_K_M.gguf from https://huggingface.co/Qwen/Qwen3-4B-GGUF
       into C:\dev\llama.cpp\models

  Run:
    powershell -ExecutionPolicy Bypass -File .\scripts\start-llama-server.ps1
  Leave the window open while you use the app.
#>
param(
    [string]$LlamaDir = "C:\dev\llama.cpp",
    [string]$Model    = "Qwen3-4B-Q4_K_M.gguf",
    [int]$Port        = 8080,
    [int]$Threads     = 0,
    [int]$Context     = 8192
)

$exe = Join-Path $LlamaDir "llama-server.exe"
$modelPath = Join-Path (Join-Path $LlamaDir "models") $Model

if (-not (Test-Path $exe)) {
    Write-Error "llama-server.exe not found in $LlamaDir. See the setup notes at the top of this script."
    exit 1
}
if (-not (Test-Path $modelPath)) {
    Write-Error "Model not found: $modelPath. See the setup notes at the top of this script."
    exit 1
}

if ($Threads -le 0) {
    # Physical cores, not logical ones: llama.cpp runs best that way on CPU.
    $Threads = (Get-CimInstance Win32_Processor | Measure-Object -Property NumberOfCores -Sum).Sum
}

Write-Host "Starting llama-server on http://127.0.0.1:$Port with $Threads threads and a $Context-token context."
Write-Host "Check it is up: open http://127.0.0.1:$Port/v1/models in a browser."

# --jinja     : use Qwen3's own chat template, so tool calls come back as structured tool_calls
# --parallel 1: one request at a time gets the whole context (more slots would split it)
# --host      : only this computer can reach the model
& $exe -m $modelPath --jinja -c $Context --parallel 1 --host 127.0.0.1 --port $Port -t $Threads
