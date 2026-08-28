# Remote Desktop LAN (WinUI 3)

同一 Wi-Fi 上の PC と画面共有・リモート操作を行う WinUI 3 アプリです。

## 機能

- プライマリモニタの画面共有 (JPEG / 約 15 FPS)
- マウス・キーボード操作
- UDP による近傍ホスト自動発見
- PIN 認証 + ホスト側の接続許可
- **GitHub Releases からのオンライン更新**（ダウンロード → インストーラー起動）

## ダウンロード

最新版は GitHub Releases から入手できます。

https://github.com/honi0907/remote_app/releases

`RemoteDesktopLAN-Setup-x.x.x-x64.exe` を実行してインストールしてください。

## 開発環境

- Windows 10 1809 以降 (Windows 11 推奨)
- .NET 8 SDK
- Windows App SDK 1.7
- ビルドには Visual Studio 2022 の **Windows アプリ開発** ワークロード、または Windows SDK + Inno Setup を推奨

## ビルド

```powershell
cd c:\Users\mksgr\Desktop\remote_app
dotnet restore RemoteDesktop.sln
dotnet build RemoteDesktop.sln -c Release -p:Platform=x64
```

### インストーラー付きリリースビルド

Inno Setup 6 をインストール後:

```powershell
.\scripts\build-release.ps1 -Version 1.0.0
```

成果物: `dist/RemoteDesktopLAN-Setup-1.0.0-x64.exe`

## オンライン更新

アプリ起動時およびホーム画面の「更新を確認」から GitHub Releases を参照します。

1. 新しいバージョンを検出
2. インストーラーをダウンロード
3. ユーザー確認後、インストーラーを起動してアプリ終了

設定: `src/RemoteDesktop.App/Assets/update-config.json`

## バージョン管理

- バージョンは [Directory.Build.props](Directory.Build.props) の `<Version>` で管理
- `v*` タグを push すると GitHub Actions がビルドして Release を作成

```powershell
git tag v1.0.0
git push origin v1.0.0
```

## 同一 PC テスト

1. アプリを 2 インスタンス起動
2. 一方で「ホスト」→ PIN を確認
3. もう一方で「接続」→ `127.0.0.1` → PIN 入力
4. ホスト側で「許可」

## ポート

| 用途 | ポート |
|------|--------|
| UDP 発見 | 9847 |
| TCP セッション | 9848 |

## プロジェクト構成

```
src/RemoteDesktop.App/     WinUI 3 メインアプリ
tests/ProtocolTests/       プロトコル単体テスト
installer/                 Inno Setup スクリプト
scripts/build-release.ps1  リリースビルド
.github/workflows/         CI / Release
```
