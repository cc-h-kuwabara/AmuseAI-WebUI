# Google ColabでAmuseを動かす

1. `Amuse_Web_Colab.ipynb` をColabの「ファイル → ノートブックをアップロード」で開きます。
2. 「ランタイムのタイプを変更」でNVIDIA GPUを選びます。
3. 上から実行します。`https://github.com/cc-h-kuwabara/AmuseAI-WebUI.git` の `webui` ブランチを自動的にcloneしてビルドします。ソースZIPのアップロードは不要です。
4. 「3. 起動・表示」でCloudflare Tunnelが起動し、表示された「Open Amuse Web UI」リンクをブラウザーで開きます。既定ポートは8082です。
5. 終了時は停止セルを実行します。

CUDAと.NETの初回ビルドには時間がかかります。モデルは別途ダウンロードされます。標準の保存先は `/content/amuse` なので、ランタイムの終了で失われます。モデルのDrive保存と結果ZIPのダウンロードは任意です。

## ビルド時間の短縮

ビルド中の標準出力・標準エラーは `WORK/logs/build.log`（既定: `/content/amuse/logs/build.log`）へ追記します。セルには開始・終了状態とログの保存先だけを表示します。失敗時もログは残ります。Colabのファイル一覧から参照・ダウンロードできます。必要な場合だけ別セルで `!tail -n 100 /content/amuse/logs/build.log` を実行すると末尾を確認できます。ランタイム終了時にログは失われるため、必要なら事前に保存してください。

設定セルで `MOUNT_DRIVE=True` にすると、`MyDrive/Amuse/BuildCache` にビルド済みアプリとCUDAライブラリーを保存します。次のランタイムでは互換性のあるキャッシュを復元し、コンパイルを省きます。初回はビルドが必要です。保存先は `CACHE_DIRECTORY` で変更できます。モデル・生成結果・認証設定はこのキャッシュに含めません。

`BUILD_JOBS=0` はCPU数と空きメモリーからCUDAビルドの並列数を自動設定します（最大8）。メモリー不足の場合は `BUILD_JOBS=2` に下げてください。GPU・CUDA・OS、アプリのソースやCUDAインストーラーが変わると、対応するキャッシュは再作成されます。依存パッケージが揃っていればAPT更新も省略します。各コマンドの所要秒数をログに表示します。

## Web UIとノートブックAPI

[Colab公式FAQ](https://research.google.com/colaboratory/faq.html)では、無料枠・コンピュートユニット残高なしでWeb UIを主に使う生成処理を制限しています。その場合は `USE_WEB_UI=False` とし、任意の生成セルを `RUN_SAMPLE=True` にして実行してください。このモードではトンネルを起動しません。

Web UIは [Cloudflare Quick Tunnel](https://developers.cloudflare.com/tunnel/get-started/quick-tunnels/) を使用します。公式GitHubリリースからLinux x64版cloudflaredを自動取得し、localhost:8082へ転送します。Colabプロキシ・iframeは使いません。発行されたHTTPS originだけをAmuseに許可します。内部オプション名の `--colab-origin` は互換性のため維持しています。

公開URLを知る人はアプリを操作できます（ログイン認証なし）。URLを共有しないでください。再実行時はこのセルが起動したサーバーとトンネルを停止し、新しいURLを取得します。「6. 停止」で両方を停止します。トンネルログは `WORK/logs/cloudflared.log` に保存します。起動に失敗したらこのログを確認してください。Quick Tunnelは一時利用向けで、サービスの可用性は保証されません。

取得するブランチは設定セルの `GIT_REF` で指定します。既定値はColab対応を含む `webui` です。再実行時は同じcloneを再利用し、実行コミットを表示します。別ブランチや最新のソースを新規取得する場合は、`WORK` を別の保存先に変更してください。

## 配布ファイルの再作成

```powershell
python Scripts/create-colab-notebook.py
```

ノートブックは `Notebooks/Amuse_Web_Colab.ipynb` に作成します。設定・認証情報・実行出力は収録しません。`package-colab-source.py` は手動でソースZIPを作るための補助スクリプトで、ノートブックの実行には使いません。

## 開発時の確認

```powershell
dotnet build Amuse.Web/Amuse.Web.csproj -c Release
python Scripts/test-colab.py
```

テストはノートブックのコードセル構文、ソース展開、ポート占有時の保護、実サーバーでのプロキシ・iframe・CSRF動作を確認します。実際のColabのフロントエンドやGPU推論の検証とは区別してください。
