# GitHub に workflow ファイルを push するためのセットアップ
# 使い方: PowerShell で .\scripts\setup-github-push.ps1

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot\..

Write-Host ""
Write-Host "=== GitHub 自動ビルド設定 ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "普段は AI 側で git push できますが、"
Write-Host "「自動ビルドの設定ファイル(.github/workflows)」だけは"
Write-Host "GitHub がセキュリティ上、あなたのブラウザで1回許可を求めます。"
Write-Host "（AI や Cursor が代わりにクリックすることはできません）"
Write-Host ""

# Step 1: workflow 権限をリクエスト
Write-Host "[1/4] ブラウザ認証を開始します..." -ForegroundColor Yellow
Write-Host "      → Enter を押す → ブラウザが開く → コード入力 → Authorize" -ForegroundColor Yellow
Write-Host ""
gh auth refresh -h github.com -s workflow

# Step 2: 権限付与を確認（最大2分待つ）
Write-Host ""
Write-Host "[2/4] 権限を確認中..." -ForegroundColor Yellow
$deadline = (Get-Date).AddMinutes(2)
$hasWorkflow = $false
while ((Get-Date) -lt $deadline) {
    $status = gh auth status 2>&1 | Out-String
    if ($status -match "workflow") {
        $hasWorkflow = $true
        break
    }
    Write-Host "  workflow 権限がまだ付いていません。ブラウザで Authorize しましたか？ 10秒後に再確認..."
    Start-Sleep -Seconds 10
}

if (-not $hasWorkflow) {
    Write-Host ""
    Write-Host "ERROR: workflow 権限が付きませんでした。" -ForegroundColor Red
    Write-Host "もう一度このスクリプトを実行するか、次を手動で確認してください:"
    Write-Host "  gh auth status"
    Write-Host "  → Token scopes に 'workflow' があること"
    exit 1
}

Write-Host "OK: workflow 権限を確認しました。" -ForegroundColor Green

# Step 3: git が gh のトークンを使うように設定
Write-Host ""
Write-Host "[3/4] git の認証を gh に合わせます..." -ForegroundColor Yellow
gh auth setup-git

# Step 4: push
Write-Host ""
Write-Host "[4/4] GitHub に push します..." -ForegroundColor Yellow
git push origin main
git tag -f v1.0.0
git push origin v1.0.0 --force

Write-Host ""
Write-Host "=== 完了 ===" -ForegroundColor Green
Write-Host "数分後、ここでビルド状況を確認:"
Write-Host "  https://github.com/honi0907/remote_app/actions"
Write-Host ""
Write-Host "インストーラーができたら:"
Write-Host "  https://github.com/honi0907/remote_app/releases/tag/v1.0.0"
