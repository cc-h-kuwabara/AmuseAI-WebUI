# Google ColabでAmuseを動かす

1. `Amuse_Web_Colab.ipynb` をColabの「ファイル → ノートブックをアップロード」で開きます。
2. 「ランタイムのタイプを変更」でNVIDIA GPUを選びます。
3. 上から実行し、ソース選択画面で同梱の `Amuse_Colab_Source.zip` をアップロードします。
4. ビルド後、Web画面がノートブック内に表示されます。ポートは最初の設定セルの `PORT` で指定します。
5. 終了時は停止セルを実行します。

CUDAと.NETの初回ビルドには時間がかかります。モデルは別途ダウンロードされます。標準の保存先は `/content/amuse` なので、ランタイムの終了で失われます。モデルのDrive保存と結果ZIPのダウンロードは任意です。

## Web UIとノートブックAPI

[Colab公式FAQ](https://research.google.com/colaboratory/faq.html)では、無料枠・コンピュートユニット残高なしでWeb UIを主に使う生成処理を制限しています。その場合は `USE_WEB_UI=False` とし、任意の生成セルを `RUN_SAMPLE=True` にして実行してください。Web UIの表示には公式の `serve_kernel_port_as_iframe` と `proxyPort` を使用します。公開トンネルは作りません。

サーバーは引き続きlocalhostにバインドします。ノートブックから取得したプロキシのHTTPS originと現在の出力フレームだけを明示的に許可し、その他のHost/Originと、トークンを持たない変更要求は拒否します。通常のローカル起動時にはColab用の許可はありません。

ソースをGitHubから取得する場合は、`SOURCE_MODE='git'` に変更し、Colab対応ファイルを含む `GIT_URL` / `GIT_REF` を指定します。まだ公開されていないローカル変更を使う場合は、既定のZIPアップロードを使ってください。

## 配布ファイルの再作成

```powershell
python Scripts/create-colab-notebook.py
python Scripts/package-colab-source.py
```

ノートブックは `Notebooks/Amuse_Web_Colab.ipynb`、ソースZIPは `artifacts/Amuse_Colab_Source.zip` に作成します。設定・認証情報・生成モデル・実行結果・ビルド成果物は収録しません。

## 開発時の確認

```powershell
dotnet build Amuse.Web/Amuse.Web.csproj -c Release
python Scripts/test-colab.py
```

テストはノートブックのコードセル構文、ソース展開、ポート占有時の保護、実サーバーでのプロキシ・iframe・CSRF動作を確認します。実際のColabのフロントエンドやGPU推論の検証とは区別してください。
