# AI Image Metadata Viewer

Windows向けの生成AI画像メタデータ・ビューアーです。AUTOMATIC1111、ComfyUI、NovelAIなどの生成情報を解析・表示し、PNG / JPEG / WebPでは画像本体を再エンコードせずに対応メタデータを削除できます。

Current version: **v1.2.1**

## 主な機能

- C# / WinForms / .NET 10 / Windows 11 x64
- PNG / JPEG / WebPを中心とした画像表示
- AUTOMATIC1111 / ComfyUI / NovelAIメタデータ解析
- Positive / Negative Prompt、生成設定、Model / LoRA、Workflow、RAW Metadata表示
- 各項目のクリップボードコピーと「コピー済」フィードバック
- U+005C (`\`) をPrompt欄で明確に表示し、元データやClipboard内容は変更しない
- 複数ファイル・フォルダーのドラッグ＆ドロップ
- Zoom / Pan / Fit / 100%
- SHA-256表示
- PNG / JPEG / WebPの無劣化メタデータ削除
- Orientation / ICC / IFD1 / Thumbnail / MakerNote等の保護
- 削除対象の物理消去とTOCTOU対策
- 設定ファイル `settings.json` はEXEと同じフォルダーに保存
- 外部通信なし、管理者権限不要、ログファイル常時出力なし

## 対応上の注意

- 無劣化メタデータ削除の正式対応形式は **PNG / JPEG / WebP** です。
- HEIF / AVIF / JPEG XL等の表示可否はWindows側の利用可能なデコーダーに依存します。
- 削除対象領域が保持すべきEXIF領域と安全に分離できない場合、破損防止のため削除を拒否することがあります。
- アニメーション画像は用途・デコーダーにより表示上の制約があります。

## 必要環境

開発・ビルド:

- Windows 11 x64
- .NET 10 SDK
- NuGet接続環境

NuGet依存関係:

- SkiaSharp 3.119.0
- SkiaSharp.NativeAssets.Win32 3.119.0

## ビルド

PowerShellでリポジトリルートから実行します。

```powershell
dotnet restore ".\src\AI_Image_Metadata_Viewer_v1.2.1\AI_Image_Metadata_Viewer_v1.2.1.csproj"
dotnet build ".\src\AI_Image_Metadata_Viewer_v1.2.1\AI_Image_Metadata_Viewer_v1.2.1.csproj" -c Release
```

自己完結・単一EXEを発行する場合:

```powershell
dotnet publish ".\src\AI_Image_Metadata_Viewer_v1.2.1\AI_Image_Metadata_Viewer_v1.2.1.csproj" `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -o ".\artifacts\publish\win-x64"
```

## 公開リポジトリについて

公開範囲はアプリ本体ソースとビルドに必要な設定・アイコンに限定しています。リリース検証用の内部テスト資材、`bin`、`obj`、`artifacts`、実行時の `settings.json`、EXE、ログ、アーカイブ等は公開対象外です。

公開用アイコンは、ビルドに不要な付加PNGチャンクを除外したクリーンなICOを使用しています。バイナリ配布を行う場合はGitHub Releases等へ別途配置する運用を推奨します。
