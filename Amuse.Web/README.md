# Amuse Web（ローカル版）

Windows / Linux x64で動くASP.NET CoreのWebアプリです。ブラウザーからAIホストを利用します。Linux版はWPFに依存しません。Windowsデスクトップ版は引き続き利用できます。

## Linuxで起動する

Ubuntu 24.04 x64を検証対象にしています。Linux版はCPUによるStableDiffusion.cppとONNX推論、およびNVIDIA CUDAによるStableDiffusion.cppに対応する構成です。Windows用の実行環境・DLLは流用せず、Linux用の環境を作成してください。HuggingFaceの同梱PythonホストとONNXのGPU実行はLinux版では未対応です。NVIDIA実機でのCUDAビルド・推論は未検証です。

```bash
# Ubuntuの実行時依存関係
sudo apt-get update
sudo apt-get install -y ffmpeg libgomp1 libglib2.0-0 libgl1

# ソースから配布版を作成（.NET 10 SDKが必要）
bash Scripts/publish-web-linux.sh

# 配布版自体は.NETランタイムを同梱
./artifacts/publish/Amuse.Web-linux-x64/Amuse.Web --port 8080 --data-dir "$HOME/.local/share/Amuse/Web"
```

`http://localhost:8080` を開きます。ポート指定・localhost限定・停止方法はWindows版と同じです。「実行環境」で **StableDiffusion.cpp Linux CPU** を作成してから、対応モデルを導入してください。モデルファイル自体は共有できますが、設定のモデル保存先はLinux側の実在パスに変更します。Windowsの `C:\...` はLinuxのパスとして使えません。

ビルドだけなら `dotnet build Amuse.Web/Amuse.Web.csproj -c Release` で実行できます。Linuxで `Amuse.sln` 全体をビルドすると、Windowsデスクトップ用プロジェクトも対象になるため、Webプロジェクトまたは上記スクリプトを使用してください。別の配布先はスクリプトの第1引数で指定でき、既存フォルダーは上書きしません。

Linuxでの再検証は、実行環境を作成後に次のコマンドで行えます。サーバーの統合テスト、PNG処理、CPUによる実ONNX推論、別プロセス間の共有テンソル転送、プロセス終了処理、StableDiffusion.cppのネイティブABIを検証します。生成モデルはダウンロードしません。

```bash
bash Scripts/test-linux.sh artifacts/publish/Amuse.Web-linux-x64 "$HOME/.local/share/Amuse/Web/RuntimeStableDiffusionCpp/linux-cpu"
```

### NVIDIA GPUを使う

Linux用のNVIDIAドライバー・CUDA Toolkit（`nvidia-smi` と `nvcc` が動く状態）、CMake、C++コンパイラー、Gitを用意します。Webサーバーを停止して、固定バージョンのCUDAライブラリーを構築してください。既存のCUDA環境は上書きしません。

```bash
sudo apt-get install -y build-essential cmake git
# NVIDIAドライバーとCUDA ToolkitはGPUに対応するものを別途導入
nvidia-smi
nvcc --version
bash Scripts/install-sd-cuda-linux.sh "$HOME/.local/share/Amuse/Web"
./artifacts/publish/Amuse.Web-linux-x64/Amuse.Web --port 8080 --data-dir "$HOME/.local/share/Amuse/Web"
```

「実行環境」の **StableDiffusion.cpp Linux NVIDIA CUDA** を作成し、生成画面の「実行デバイス」でNVIDIA GPUを選びます。CPU用実行環境は別に保持します。GPU検出には `nvidia-smi` を使い、`CUDA_VISIBLE_DEVICES` の番号・UUID指定と並び順を反映します。CUDAライブラリーを更新する場合も構築スクリプトを使います。Web画面からの自動再構築は、手動ビルドした環境を削除しないよう拒否します。

CUDA構築には [固定バージョンの公式CMake設定](https://github.com/leejet/stable-diffusion.cpp/blob/master-968-8cd731e/CMakeLists.txt) の `SD_CUDA` と `SD_BUILD_SHARED_LIBS` を使用します。

## 起動

ソースから実行する場合は.NET 10 SDKを使用します。既存リポジトリーと同様に、通常はNuGet版TensorStackを利用するRelease構成でビルドしてください。

```powershell
dotnet build Amuse.sln -c Release
& .\Amuse.Web\bin\Release\net10.0-windows10.0.17763.0\Amuse.Web.exe `
    --port 8080 `
    --host-dir .\Amuse.App\bin\Release\net10.0-windows10.0.17763.0
```

`http://localhost:8080` をブラウザーで開きます。サーバーの停止はコンソールで `Ctrl+C`。サーバーを停止するまでは、タブを閉じても処理は続きます。

| オプション | 既定値・用途 |
| --- | --- |
| `--port <1–65535>` | `8080`。使用中の場合は起動に失敗し、別ポートに自動変更しません |
| `--data-dir <path>` | `%LOCALAPPDATA%\Amuse\Web`。設定、ファイル、ジョブ、実行環境の保存先 |
| `--host-dir <path>` | Web実行ファイルのフォルダー。3種類の `AmuseHost.*.exe` を配置する場所 |
| `--import-desktop <path>` | 既存の `Settings.json` があるフォルダー。新しいデータ保存先への初回取り込み専用 |
| `--help` | 起動方法を表示 |

待ち受けはIPv4/IPv6のループバック限定です。LAN公開用のオプションはありません。`ASPNETCORE_URLS` や `--urls` で公開範囲を拡張する仕様にはしていません。

既存AIホストは固定の名前付きパイプを使うため、デスクトップ版とWeb版の**同時起動は排他**にしています。両方のアプリは維持されますが、利用時には一方を終了して切り替えてください。

## 最初の設定

1. 「設定 → 一般」で、既存のモデル保存先を指定するか、新しいモデル保存先を使用します。
2. 必要に応じて「設定 → アクセストークン」でトークンを登録します。登録済みの値をAPIで読み戻すことはできません。
3. 「モデル」で使うモデルと依存コンポーネントをダウンロードします。
4. 「実行環境」でGPUとバックエンドに対応する環境を作成します。作成・更新・再構築は明示的な操作で実行します。
5. 「生成スタジオ」でワークフロー、モデル、GPUを選択して生成します。

モデル・環境のダウンロードにはインターネット接続が必要な場合があります。生成のためにブラウザーへAPIキーを渡す必要はありません。

### デスクトップ版のデータを取り込む

```powershell
& .\Amuse.Web.exe --port 8080 `
    --data-dir C:\AmuseData\Web `
    --import-desktop C:\AmuseDesktop
```

`Settings.json` をバックアップ付きでコピーし、既存のモデル保存先を再利用します。既存の履歴内にある対応メディアとJSONもWebギャラリーにコピーします。元の設定・履歴ファイルは変更しません。Webの履歴・一時ファイル・実行環境は別の保存先になります。コピー元のモデル保存先がある状態で実行してください。

`--import-desktop` は初回のみ指定します。以後は同じ `--data-dir` で起動してください。既存のWeb設定がある場所への再取り込みは拒否します。

実行環境のバイナリーは自動コピーしません。Web側で環境を作成するか、既存の `RuntimeStableDiffusionCpp` などをWebのデータ保存先へコピーして再利用できます。モデルファイルの再ダウンロードは不要です。

## Webで操作できる機能

- 画像：テキストからの生成、画像変換、指示による編集、Inpaint、ペイントからの生成。
- 動画：テキスト／画像／動画からの生成、フレームごとの画像変換。
- 音声・音楽：テキストからの生成、音声認識。
- テキスト：指示、会話履歴の自動追記とブラウザーへの保存、画像入力、選択したAIツールの利用。
- 画像／動画のアップスケール、特徴抽出・背景除去、動画フレーム補間。
- PNGキャンバス：画像の重ね合わせ、描画、消しゴム、テキスト、サイズ変更、Undo、マスク作成、生成入力への受け渡し。
- 動画編集：画像・動画クリップの並べ替え、開始位置と長さの指定、音声付き書き出し。クリップの音声を引き継ぎ、BGMを指定した場合はBGMを使用します。
- モデル、LoRA、ControlNet、依存コンポーネント、環境、テンプレートの設定。
- ギャラリー、結果と生成設定の保存、ごみ箱への移動と復元。
- バッチ生成（最大100回）、ジョブ進捗、対応バックエンドの途中画像・テキストの表示、待機／実行中のキャンセル、サーバー再起動後の履歴復元。

特殊な生成パラメーターは「詳細パラメーター」で指定できます。動画・音声・テキストの固有値はモデルの初期値をベースにします。LoRAの強度を変更する場合は `loraOptions` に `key`、`name`、`strength` を指定します。

Inpaintでは「入力画像」で原画像を選び、「Inpaint用マスク」でマスクを選びます。マスクは黒が保持、白が編集領域です。

AIツールの外部アクセスを使用する場合は「一般」で外部アクセスと確認済みの項目を有効化し、生成画面で使用するツールを選択します。外部ツールには検索、ローカルファイル検索、アプリ起動などが含まれます。

### 移行・検証の境界

Web側には各生成／加工カテゴリーの経路を実装していますが、WPFの全画面・全操作を同じUIで再現したものではありません。モデル・環境の高度な設定は既存形式のJSONで編集します。画像エディターはラスタキャンバス方式、動画エディターはクリップ接続方式であり、デスクトップ版の選択・レイヤー・タイムライン編集の完全な操作互換は今後の移行対象です。

フォルダー入力を使う高度な自動化は追加移行対象です。現在は選択した入力メディアとSeedを使うバッチ生成に対応します。途中画像はバックエンドからプレビュー用データが通知される場合に表示します。

実際のAI推論を確認したのは、配布用のサーバーと同梱AIホストによるStableDiffusion.cpp/VulkanのFLUX.2 Klein 4Bで、AMD Radeon 780M上の256×256、4ステップの画像生成と途中プレビューの更新です。各モデル、HuggingFace/ONNXでの生成、動画・音声・テキスト生成、AIによるアップスケール／抽出／補間は個別の実モデル検証が必要です。

Chrome DevTools MCPを使ったブラウザー操作で、全9画面の遷移、15ワークフローのモデル選択、モデル検索、設定保存、アップロード、描画・Undo・PNG保存、ごみ箱・復元、動画編集と実再生、実画像生成、再読み込み後の復元、390px幅の全画面表示を確認しました。通知がボタンのクリックを妨げる問題と、更新・再読み込み時に生成条件が失われる問題を修正しています。デスクトップ幅とスマートフォン幅のスクリーンショットも目視確認済みです。

## 配布用フォルダーを作る

```powershell
.\scripts\publish-web.ps1
# .NETランタイムも同梱する場合
.\scripts\publish-web.ps1 -SelfContained -OutputDirectory C:\Build\AmuseWeb
```

Webサーバー、静的画面、3種類のAIホストと依存ファイルを同じフォルダーに発行します。既定の出力先は `artifacts\publish\Amuse.Web`。誤上書きを避けるため、既に存在する出力先は拒否します。ユーザーの設定・モデル・トークンは配布物へコピーしません。

通常の発行はフレームワーク依存です。配布先には.NET 10のASP.NET Coreランタイムと、Windowsメディア処理用の.NET Desktopランタイムが必要です。`-SelfContained` では.NETランタイムを同梱します。GPUドライバーやモデル固有の実行環境は別途必要です。

## 開発と検証

```powershell
dotnet build Amuse.sln -c Release
python .\scripts\test-web.py
node --check .\Amuse.Web\wwwroot\app.js
```

Chrome DevTools MCPがCodexの `chrome-devtools` として設定されていれば、ブラウザーテストも実行できます。別のデータ保存先でWebサーバーを起動し、そのURLを指定してください。設定変更やアップロードを含むため、検証用のデータ保存先を使用します。

```powershell
python .\Scripts\test-web-browser.py --url http://localhost:18080
# 実モデルによる画像生成を省略する場合
python .\Scripts\test-web-browser.py --url http://localhost:18080 --skip-inference
# 実モデルのキャンセルと、その後の再生成
python .\Scripts\test-web-browser-cancel.py --url http://localhost:18080
```

実画像生成のテストには、FLUX.2 Klein 4B（モデルID 550）のファイルと対応する実行環境が必要です。ブラウザー検証結果・スクリーンショットは `artifacts\browser-qa` に保存します。

Pythonの統合テストは一時フォルダーと専用プロセスを使用し、モデルをダウンロードしません。19項目で起動オプション、ローカル接続制限、設定の取り消し、トークン非公開、ファイル保存・復元、実際のH.264 MP4エンコード・音声追加・トリム、ジョブキャンセル、正常終了、再起動後の復元、ポート競合、不正な設定の保護を検証します。

ログは `<data-dir>\Logs\web-YYYYMMDD.log` に保存します。起動失敗はコンソールにも表示されます。AI環境・ドライバーに関する詳細ログは各ホストのログも確認してください。

## 構成

`Amuse.Core` にモデル、設定、バックエンドクライアント、サービスを集約し、`Amuse.App` と `Amuse.Web` が参照します。既存ファイルのパスと名前空間は維持し、Coreプロジェクトだけがそれらをコンパイルします。パス・ダイアログ確認・UIディスパッチは `AppRuntime` を介してホストが提供します。

Windows版Coreは既存のTensorStack.WPFを利用します。Linux版は `ThirdParty/TensorStack` のソースアダプターを使い、画像型をSkiaSharpへ、ONNXをCPUランタイムへ切り替えます。Windows専用のプロセス管理・共有メモリーもLinux用の実装に切り替えます。Web画面はASP.NET Coreから配信するHTML/CSS/JavaScriptです。Node.jsやフロントエンドのビルド工程は起動要件ではありません。

生成・加工・設定変更・環境作成・ダウンロードは共通の単一ワーカーで順に処理し、GPUと設定の競合を避けます。途中でサーバーが終了したジョブは再起動後に `interrupted` と表示し、自動再実行しません。
