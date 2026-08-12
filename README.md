# AI Image Metadata Viewer

Windows向けの生成AI画像メタデータ・ビューアーです。

AUTOMATIC1111、ComfyUI、NovelAIなどの生成情報を解析し、Prompt、生成設定、Model / LoRA、Workflow、RAW Metadataなどを確認できます。

**本リポジトリは、ソフトウェアの透明性確保および安全性確認を目的として、ソースコードを公開しています。**

Current version: **v1.2.1**

---

## 主な機能

- C# / WinForms / .NET 10 / Windows 11 x64
- PNG / JPEG / WebPを中心とした画像表示
- AUTOMATIC1111 / ComfyUI / NovelAI メタデータ解析
- Positive / Negative Prompt表示
- 生成設定の解析・表示
- Model / Checkpoint / LoRA情報表示
- ComfyUI Prompt / Workflow JSON表示
- RAW Metadata表示
- メタデータ検索
- 各項目のクリップボードコピー
- コピー成功時に「コピー済」を約1秒表示
- Prompt内のU+005C（`\`）を明確なバックスラッシュとして表示
- 複数ファイル・フォルダーのドラッグ＆ドロップ
- 入力単位で置き換える画像キュー
- 「前へ」「次へ」による画像移動
- Zoom / Pan / Fit / 100%
- SHA-256表示
- PNG / JPEG / WebPの無劣化メタデータ削除
- Orientation / ICC / IFD1 / Thumbnail / MakerNote等の保護
- 削除対象メタデータの物理消去
- 原本上書き時のTOCTOU対策
- EXEと同じフォルダーへの `settings.json` 保存
- 外部通信なし
- 管理者権限不要
- 常時ログファイル出力なし

---

## 対応する生成AIメタデータ

主に以下の生成環境で作成された画像のメタデータ解析に対応しています。

### AUTOMATIC1111

主に以下の情報を解析・表示します。

- Positive Prompt
- Negative Prompt
- Steps
- Sampler
- Scheduler
- CFG Scale
- Seed
- Subseed
- Size
- Model
- Model Hash
- VAE
- Clip Skip
- Hires関連情報
- LoRA
- その他保存されている生成パラメーター

### ComfyUI

主に以下の情報を解析・表示します。

- Prompt JSON
- Workflow JSON
- KSampler
- Custom Nodeを含む各種ノード情報
- Checkpoint
- UNET
- VAE
- CLIP
- LoRA
- Seed
- Steps
- CFG
- Sampler
- Scheduler
- Denoise
- その他Workflow内に保存されている情報

### NovelAI

画像に保存されているNovelAI由来の生成情報を解析して表示します。

---

## Prompt表示

日本語Windows環境では、Unicodeの

`U+005C REVERSE SOLIDUS`

が使用フォントによって円記号のように表示される場合があります。

AI Image Metadata Viewer v1.2.1では、Prompt表示部分にConsolasを使用し、

```text
\(kancolle\)
```

のようなバックスラッシュを画面上でも明確に `\` として表示します。

これは**表示上のみの処理**です。

以下の文字コードは相互変換しません。

```text
U+005C  \
U+00A5  ¥
U+FFE5  ￥
```

元のMetadata、内部Prompt文字列、Clipboardへコピーされる文字列についても変更しません。

---

## コピー機能

各種メタデータやPromptなどをクリップボードへコピーできます。

コピーに成功すると、対象のコピー操作が一時的に以下のように変化します。

```text
コピー
  ↓
コピー済
  ↓ 約1秒
コピー
```

コピー成功時に確認ダイアログは表示されません。

連続コピーや複数のコピー操作についても、それぞれ独立して状態管理されます。

主なコピー対象は以下です。

- 各メタデータ項目
- 概要
- Positive Prompt
- Negative Prompt
- 生成設定
- Prompt JSON
- Workflow JSON
- RAW Metadata
- その他対応する表示項目

---

## 画像キュー

単一ファイル、複数ファイル、フォルダーなどを読み込めます。

新しい「開く」操作またはドラッグ＆ドロップ操作を行うと、以前のキューへ追加するのではなく、**新しい入力内容でキューを置き換えます。**

例:

```text
A.pngを開く
↓
Queue = A.png

その後、100枚の画像を含むBフォルダーを開く
↓
A.pngはキューから削除
↓
Queue = Bフォルダー内の100枚
```

古いキューに対する非同期解析結果が、新しいキューへ遅れて反映されないよう制御しています。

---

## メタデータ削除

PNG / JPEG / WebPでは、画像本体を再エンコードせずに対応メタデータを削除できます。

用途に応じた複数の削除モードを備えています。

削除処理では可能な限り以下の情報を保護します。

- Orientation
- ICC / Color Profile
- IFD1
- Thumbnail
- SubIFD
- MakerNote
- private tag
- unknown tag
- その他削除対象外の表示・画像関連情報

削除対象の文字列については、単にEXIFの参照エントリから見えなくするだけではなく、削除対象データがファイル内部に復元可能な状態で残らないよう、対応可能な領域について物理的な消去処理を行います。

ただし、削除対象領域と保持対象領域が共有されており、安全な物理消去が保証できないEXIF構造については、画像、MakerNote、Thumbnail等の破損を防ぐため処理を拒否します。

安全性を確認できない状態で強制的に削除することはありません。

---

## 原本保護

既定では元画像を直接変更せず、メタデータ削除後の画像を別ファイルとして保存します。

原本へ反映する場合は、一時ファイルを作成・検証してから置換します。

処理中に別アプリケーション等によって元ファイルが変更されていないか、以下の情報を使用して確認します。

- File Length
- LastWriteTime
- SHA-256

さらに、最終的な原本置換直前にも再検証します。

元ファイルの変更を検出した場合は置換を中止し、古い解析結果を基に作成された一時ファイルで原本を上書きしません。

---

## 対応画像形式

### 主な表示形式

主に以下の形式を対象としています。

- PNG
- JPEG
- WebP

その他の形式についても、Windows環境や利用可能なデコーダーによって表示可能な場合があります。

### 無劣化メタデータ削除

正式対応形式は以下です。

- PNG
- JPEG
- WebP

これらの形式では、画像本体を再エンコード・再圧縮せずに対応メタデータを削除します。

---

## HEIF / AVIF / JPEG XL

HEIF、AVIF、JPEG XL等の表示可否は、Windows側で利用可能なCodec / Decoderに依存します。

対応コーデックが導入されていない環境では表示できない場合があります。

また、これらの形式はPNG / JPEG / WebPと同等の無劣化メタデータ削除の正式対応対象ではありません。

---

## 画像表示

以下の基本操作に対応しています。

- Fit表示
- 100%表示
- Zoom
- Pan
- 前の画像
- 次の画像
- ウィンドウサイズ変更
- 最大化 / 復元
- ドラッグ＆ドロップ

画面は画像表示用の左ペインと、メタデータ表示用の右ペインで構成され、両者の境界はドラッグして調整できます。

---

## メタデータ表示

主な表示タブから生成情報や画像Metadataを確認できます。

長い文字列については、画面幅に応じた表示と完全値の確認を行えるよう設計しています。

概要画面では主要な画像情報をまとめて確認できます。

例:

- ファイル名
- フルパス
- 画像形式
- MIME
- ファイルサイズ
- 幅
- 高さ
- アスペクト比
- Pixel Format
- Color Space
- Alpha
- DPI
- Orientation
- SHA-256
- 生成元

---

## RAW Metadata

画像コンテナに保存されているMetadataを可能な範囲で解析・表示します。

対象となる情報には、形式に応じて以下が含まれます。

- PNG chunks
- EXIF
- XMP
- ICC
- GPS
- JPEG Metadata
- WebP RIFF chunks
- 生成AI固有Metadata
- その他検出可能なMetadata

---

## SHA-256

現在表示しているファイルのSHA-256を確認できます。

ファイル識別や改変確認などに利用できます。

---

## 設定ファイル

設定は

```text
settings.json
```

として、実行EXEと同じフォルダーへ保存されます。

`%LOCALAPPDATA%` 等へ自動保存する仕様ではありません。

設定ファイルへ書き込めない環境では、アプリケーション本体を異常終了させず、安全側で処理します。

---

## プライバシー

AI Image Metadata Viewer自体は、画像やメタデータを外部サーバーへ送信しません。

通常使用時に外部通信を必要としません。

以下の処理はローカル環境内で行われます。

- 画像表示
- Metadata解析
- Prompt解析
- Workflow解析
- SHA-256計算
- Metadata削除
- Clipboardコピー

---

## 管理者権限

通常の使用に管理者権限は必要ありません。

UACによる管理者昇格を前提としたアプリケーションではありません。

---

## ログ

通常使用時に常時ログファイルを生成する仕様ではありません。

予期しないエラーが発生した場合は、エラー内容を確認できるダイアログを表示します。

---

## 動作環境

### 実行環境

- Windows 11 x64

正式リリース版はSelf-containedで発行されているため、通常は.NET Runtimeを別途インストールする必要はありません。

### 開発・ビルド環境

- Windows 11 x64
- .NET 10 SDK
- NuGet接続環境

---

## 使用ライブラリ

主なNuGet依存関係:

- SkiaSharp 3.119.0
- SkiaSharp.NativeAssets.Win32 3.119.0

---

## ビルド

PowerShellでリポジトリルートから実行します。

```powershell
dotnet restore ".\src\AI_Image_Metadata_Viewer_v1.2.1\AI_Image_Metadata_Viewer_v1.2.1.csproj"

dotnet build ".\src\AI_Image_Metadata_Viewer_v1.2.1\AI_Image_Metadata_Viewer_v1.2.1.csproj" -c Release
```

自己完結・単一EXEとして発行する場合:

```powershell
dotnet publish ".\src\AI_Image_Metadata_Viewer_v1.2.1\AI_Image_Metadata_Viewer_v1.2.1.csproj" `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -o ".\artifacts\publish\win-x64"
```

---

## v1.2.1

v1.2.1では主に以下を改善しました。

- Prompt内のU+005C（`\`）が日本語Windows環境で円記号のように表示される問題を改善
- Prompt表示部分にConsolasを使用
- 元MetadataやClipboard内容を変更しない表示方式を採用
- コピー成功時に「コピー済」を約1秒表示
- 各種コピー操作のフィードバックを統一
- 連続コピー・複数コピー時の状態管理を改善
- 既存の画像表示、Metadata解析、Metadata削除等の機能を維持

---

## Release

正式版:

```text
AI_Image_Metadata_Viewer_v1.2.1.exe
```

File size:

```text
128,277,700 bytes
```

SHA-256:

```text
F998B23D15063293E13737D3CDC9CE1BD5392A39EC65BA28208894C2A4F799F8
```

正式な実行バイナリはGitHub Releasesから配布します。

---

## ソースコード公開について

**本リポジトリは、ソフトウェアの透明性確保および安全性確認を目的として、ソースコードを公開しています。**

公開リポジトリには、アプリケーション本体のソースコードおよびビルドに必要な設定ファイル等を収録しています。

利用者が以下のような点を確認できることを目的としています。

- 外部通信の有無
- 画像やMetadataの取扱い
- Metadata解析処理
- Metadata削除処理
- ファイル操作
- 原本保護処理
- Clipboard処理
- その他アプリケーション内部の主要な動作

ローカル生成物や実行環境固有データについてはGit管理対象外です。

主な除外対象:

- `bin`
- `obj`
- `artifacts`
- `settings.json`
- ビルド済みEXE
- DLL
- PDB
- ログ
- 一時ファイル
- ZIP等の生成物

正式な実行バイナリについてはGitHub Releasesから配布します。

---

## 注意事項

Metadataは画像作成ソフトウェアや画像形式によって構造が大きく異なります。

すべての画像について、存在するあらゆるMetadataを完全に解析・削除できることを保証するものではありません。

特に未知のEXIF構造や、削除対象と保持対象のデータ領域が共有されている場合は、安全性を優先して削除処理を拒否することがあります。

重要な画像を処理する場合は、原本のバックアップを保持してください。

---

## License

現在、本リポジトリにはオープンソースライセンスを設定していません。

本リポジトリのソースコードは、**ソフトウェアの透明性確保および安全性確認を主な目的として公開しています。**

ライセンスが明示されていない限り、本リポジトリのソースコードについて、複製、改変、再配布、商用利用等の権利を明示的に許諾するものではありません。

Copyright © 2026 Syuzouin Yukyu. All rights reserved.
