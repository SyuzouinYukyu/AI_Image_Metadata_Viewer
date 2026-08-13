# AI Image Metadata Viewer

Windows向けの生成AI画像メタデータ・ビューアーです。

AUTOMATIC1111、ComfyUI、NovelAIなどの生成情報を解析し、Prompt、生成設定、Model / LoRA、Workflow、RAW Metadataなどを確認できます。

**本リポジトリは、ソフトウェアの透明性確保および安全性確認を目的として、ソースコードを公開しています。**

Current version: **v1.2.3**

---

## 主な機能

- C# / WinForms / .NET 10 / Windows 11 x64
- PNG / JPEG / WebPを中心とした画像表示
- AUTOMATIC1111 / ComfyUI / NovelAI メタデータ解析
- Positive / Negative Prompt表示
- Promptの画面幅に応じた自動折り返し表示
- 生成設定の解析・表示
- Model / Checkpoint / LoRA情報表示
- ComfyUI Prompt / Workflow JSON表示
- RAW Metadata表示
- メタデータ検索
- 各項目のクリップボードコピー
- 「主要生成情報をコピー」による画像情報・Prompt・生成設定・Model / LoRAの一括コピー
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

### AUTOMATIC1111

主に以下の情報を解析・表示します。

- Positive Prompt / Negative Prompt
- Steps / Sampler / Scheduler / CFG Scale
- Seed / Subseed / Size
- Model / Model Hash / VAE
- Clip Skip / Hires関連情報 / LoRA
- その他保存されている生成パラメーター

### ComfyUI

主に以下の情報を解析・表示します。

- Prompt JSON / Workflow JSON
- KSampler / Custom Nodeを含む各種ノード情報
- Checkpoint / UNET / VAE / CLIP / LoRA
- Seed / Steps / CFG / Sampler / Scheduler / Denoise
- Node Count / KSampler Count / Workflow Node Count / Connection Count
- その他Workflow内に保存されている情報

### NovelAI

画像に保存されているNovelAI由来の生成情報を解析して表示します。

---

## Prompt表示

Prompt表示部分にはConsolasを使用し、日本語Windows環境で `U+005C REVERSE SOLIDUS` が円記号のように見える問題を避けています。

```text
\(kancolle\)
```

v1.2.2以降ではPositive / Negative Promptを画面幅に応じて自動折り返し表示します。折り返しは**表示上のみ**で、元Metadata、内部文字列、Clipboard内容には改行を追加しません。

以下の文字コードは相互変換しません。

```text
U+005C  \
U+00A5  ¥
U+FFE5  ￥
```

---

## コピー機能

各種メタデータやPromptをクリップボードへコピーできます。

コピー成功時は対象ボタン等が一時的に `コピー済` へ変化し、約1秒後に元の表示へ戻ります。

### 主要生成情報をコピー

概要タブの「主要生成情報をコピー」では、現在画像について以下をまとめてコピーできます。

- 画像情報
- Positive Prompt
- Negative Prompt
- 生成設定
- Model / LoRA

出力例:

```text
=== 画像情報 ===
ファイル名: example.png
形式: Png
MIME: image/png
幅: 1024
高さ: 1360
生成元: ComfyUI

=== ポジティブプロンプト ===
...

=== ネガティブプロンプト ===
...

=== 生成設定 ===
Width: 1024
Height: 1360
Seed: 435003940542920
Steps: 30
CFG: 7.0
Sampler: euler
Scheduler: normal
Denoise: 1.0
Node Count: 9
KSampler Count: 1
Workflow Node Count: 9
Connection Count: 10

=== Model / LoRA ===
Checkpoint: example.safetensors
VAE: example.safetensors
```

生成設定は `項目名: 値` 形式で出力し、区分名は付加しません。v1.2.3ではComfyUI由来の `Positive Prompt` / `Negative Prompt` Fieldを生成設定セクションから除外し、独立したPromptセクションとの重複を防止しています。

主要生成情報にはフルパス、作成日時、更新日時、SHA-256、RAW Metadata、Prompt JSON、Workflow JSON等を含めません。

---

## 画像キュー

新しい「開く」操作またはドラッグ＆ドロップ操作を行うと、以前のキューへ追加せず、**新しい入力内容でキューを置き換えます。**

古いキューに対する非同期解析結果が新しいキューへ遅れて反映されないよう制御しています。

---

## メタデータ削除

PNG / JPEG / WebPでは、画像本体を再エンコードせずに対応メタデータを削除できます。

削除処理では可能な限り以下を保護します。

- Orientation
- ICC / Color Profile
- IFD1 / Thumbnail / SubIFD
- MakerNote
- private / unknown tag
- その他削除対象外の表示・画像関連情報

削除対象データについて、対応可能な領域は物理的に消去します。削除対象領域と保持対象領域を安全に分離できないEXIF構造では、画像やMakerNote等の破損を避けるため処理を拒否します。

---

## 原本保護

既定では元画像を直接変更せず、メタデータ削除後の画像を別ファイルとして保存します。

原本へ反映する場合は一時ファイルを作成・検証してから置換し、File Length / LastWriteTime / SHA-256を使用して処理中の原本変更を検出します。最終置換直前にも再検証し、変更を検出した場合は上書きを中止します。

---

## 対応画像形式

主な表示対象:

- PNG
- JPEG
- WebP

HEIF / AVIF / JPEG XL等はWindows側のCodec / Decoderによって表示可能な場合があります。

無劣化メタデータ削除の正式対応形式:

- PNG
- JPEG
- WebP

---

## 設定ファイル

設定は `settings.json` として実行EXEと同じフォルダーへ保存されます。`%LOCALAPPDATA%` 等へ自動保存する仕様ではありません。

---

## プライバシー

画像表示、Metadata解析、Prompt / Workflow解析、SHA-256計算、Metadata削除、Clipboardコピーはローカル環境内で行われます。AI Image Metadata Viewer自体は画像やメタデータを外部サーバーへ送信せず、通常使用時に外部通信を必要としません。

---

## 動作環境

### 実行環境

- Windows 11 x64

正式リリース版はSelf-containedのため、通常は.NET Runtimeを別途インストールする必要はありません。管理者権限も不要です。

### 開発・ビルド環境

- Windows 11 x64
- .NET 10 SDK
- NuGet接続環境

主なNuGet依存関係:

- SkiaSharp 3.119.0
- SkiaSharp.NativeAssets.Win32 3.119.0

---

## ビルド

```powershell
dotnet restore ".\src\AI_Image_Metadata_Viewer_v1.2.3\AI_Image_Metadata_Viewer_v1.2.3.csproj"
dotnet build ".\src\AI_Image_Metadata_Viewer_v1.2.3\AI_Image_Metadata_Viewer_v1.2.3.csproj" -c Release
```

自己完結・単一EXE:

```powershell
dotnet publish ".\src\AI_Image_Metadata_Viewer_v1.2.3\AI_Image_Metadata_Viewer_v1.2.3.csproj" `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -o ".\artifacts\publish\win-x64"
```

---

## v1.2.3

- 「主要生成情報をコピー」のComfyUI生成設定から `Positive Prompt` / `Negative Prompt` の重複Fieldを除外
- Parserや画面表示は変更せず、コピー専用フィルターのみを局所修正
- Seed / Steps / CFG / Sampler / Scheduler / DenoiseおよびNode / Workflow集計情報は維持
- v1.2.2のPrompt自動折り返し、主要生成情報コピー、既存UI・解析・Metadata削除等を維持

### v1.2.2で追加された主な機能

- 概要タブへ「主要生成情報をコピー」を追加
- Positive / Negative Promptを自動折り返し・縦スクロール表示へ変更
- 表示上の折り返しを元PromptやClipboardへ混入させない仕様を採用

### v1.2.1で追加された主な機能

- Prompt表示部分にConsolasを使用し、U+005Cを明確なバックスラッシュとして表示
- コピー成功時の「コピー済」フィードバックを統一

---

## Formal EXE v1.2.3

```text
AI_Image_Metadata_Viewer_v1.2.3.exe
```

File size:

```text
128,277,700 bytes
```

SHA-256:

```text
A024FD463E3A4C0B3F31431EB57406C2E0391F6510CAD2E58321076145CABC7D
```

正式な実行バイナリはGitHub Releasesから配布します。

---

## ソースコード公開について

**本リポジトリは、ソフトウェアの透明性確保および安全性確認を目的として、ソースコードを公開しています。**

公開リポジトリにはアプリケーション本体のソースコードおよびビルドに必要な設定ファイル等を収録します。ローカル生成物や実行環境固有データはGit管理対象外です。

主な除外対象:

- `bin`
- `obj`
- `artifacts`
- `settings.json`
- ビルド済みEXE / DLL / PDB
- ログ / 一時ファイル / ZIP等の生成物
- テスト・検証用ローカル成果物

---

## 注意事項

Metadataは画像作成ソフトウェアや画像形式によって構造が大きく異なります。すべての画像について、存在するあらゆるMetadataを完全に解析・削除できることを保証するものではありません。

未知のEXIF構造や、削除対象と保持対象のデータ領域が共有されている場合は、安全性を優先して削除処理を拒否することがあります。重要な画像を処理する場合は原本のバックアップを保持してください。

---

## License

現在、本リポジトリにはオープンソースライセンスを設定していません。

本リポジトリのソースコードは、**ソフトウェアの透明性確保および安全性確認を主な目的として公開しています。**

ライセンスが明示されていない限り、本リポジトリのソースコードについて、複製、改変、再配布、商用利用等の権利を明示的に許諾するものではありません。

Copyright © 2026 Syuzouin Yukyu. All rights reserved.
