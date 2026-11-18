SnipOCR 1.2.0 (portable) - 画面OCRツール
====================================

■ これは何か
  画面の任意の範囲を Snipping Tool の要領で矩形選択し、その部分を OCR して
  テキストをクリップボードにコピー／結果ウィンドウに表示する常駐ツールです。
  - 完全オフライン動作（インターネット不要）
  - 管理者権限不要・インストール不要（このフォルダを置くだけ／USBでも可）
  - OCR エンジンは RapidOCR（同梱 rapidocr フォルダ）を使用します:
      * RapidOCR（PP-OCRv6）… 小さい日本語に最も強い（既定）
      * Windows 内蔵（Windows.Media.Ocr）… rapidocr フォルダが無い場合のみ。
        最速・追加ファイル不要だが、小さい字は苦手
    トレイメニュー →「OCRエンジン」で固定することもできます。

■ 使い方
  1. SnipOCR.exe をダブルクリックで起動（タスクトレイに常駐します）。
  2. Ctrl + Shift + S を押す（またはトレイアイコンをダブルクリック）。
  3. 画面が暗くなったら、OCR したい範囲をドラッグで囲む。
     - Esc または右クリックでキャンセル。
  4. 離すと OCR 実行 → クリップボードにコピー＋通知。
     結果ウィンドウでは、元画像プレビュー、テキスト、Excel TSV、Markdown表、1行版を確認できます。
  5. 表をExcelに貼りたい場合は、トレイメニュー → OCRモード →「表 / Excel」に切替えてから範囲選択。
     クリップボードにはExcelにそのまま貼れるタブ区切りテキストが入ります。
  6. 終了はトレイアイコン右クリック →「終了」。

■ 動作確認（コマンドプロンプトから）
  SnipOCR.exe --selftest               … エンジン/モデルの状態を表示
  SnipOCR.exe --selftest shot.png      … その画像を実際に読み取って結果を表示
  職場PCで「動かない」ときは、まずこれを実行して原因を切り分けてください。

■ ネット経由でコピーした場合の注意（ブロック解除）
  他PCやメールで受け取った exe は「ブロック」属性が付くことがあります。
  実行できない／警告が出る場合：
  SnipOCR.exe を右クリック → プロパティ → 下部の「許可する」にチェック → OK
  （管理者権限は不要です）

■ フォルダ構成
    SnipOCR.exe                    … 本体
    README.txt                     … このファイル
    使い方.html                    … 画面付きの簡易マニュアル
    settings.sample.txt            … 設定サンプル（settings.txt にリネームで有効）
    rapidocr\                      … RapidOCR のモデル（PP-OCRv6 det/rec）
                                     ※このフォルダごと職場PCへコピーしてください
    onnxruntime.dll                … 推論エンジン本体（必須）
    onnxruntime_providers_shared.dll
    Microsoft.ML.OnnxRuntime.dll   … .NET 用ラッパー
    System.Memory.dll              … .NET Framework 用の補助 DLL
    System.Buffers.dll
    System.Runtime.CompilerServices.Unsafe.dll
    msvcp140.dll / msvcp140_1.dll  … Visual C++ ランタイム（同梱。無い職場PC対策）
    vcruntime140.dll / vcruntime140_1.dll

■ 設定（任意）
  exe と同じフォルダに settings.txt を置くと動作を変えられます（無くても既定で動作）。
  例（settings.sample.txt を settings.txt にリネームして編集）:
    hotkey=Ctrl+Shift+S     … 起動ホットキー
    engine=auto             … auto / rapidocr / winrt
    language=auto           … WinRT用言語 auto / ja / en など
    mode=normal             … normal / line / table / sparse / small
    copyformat=auto         … auto / plain / excel / markdown / oneline
    binarize=false          … WinRTで二値化前処理するか（RapidOCR では未使用）
    showwindow=true         … 結果ウィンドウを表示するか
    copytoclipboard=true    … 自動でクリップボードにコピーするか
  ※ フォルダが書込不可の場合、設定は %LOCALAPPDATA%\SnipOCR\settings.txt に保存されます。

■ 自動起動したい場合（任意・管理者権限不要）
  Win+R →「shell:startup」→ 開いたフォルダに SnipOCR.exe のショートカットを置く。

■ うまく動かないとき
  - 「rapidocr フォルダが見つかりません」通知が出る
      → rapidocr フォルダを exe と同じ場所に置いてください。
        無い場合は Windows 標準 OCR で動作します（自動フォールバック）。
  - 「OCR 言語リソースが見つかりません」通知が出る
      → rapidocr フォルダがある場合は出ません。無い場合は 設定 → 時刻と言語 →
        言語 → 日本語 →「オプション」→「文字認識」機能を追加してください。
  - 管理者権限で動いている別アプリのウィンドウ上ではキャプチャできないことがあります。
  - UAC 画面・ロック画面・セキュアデスクトップはキャプチャできません（OS 仕様）。
  - 迷ったら SnipOCR.exe --selftest で状態を確認してください。

■ ビルドし直すには（開発者向け）
  build.cmd をダブルクリック。Windows 標準の C# コンパイラのみ使用。
  Visual Studio・NuGet・インターネットは不要です。
  依存 DLL（lib\）とモデル（models\）が無い場合は、ネット接続のある PC で
    powershell -ExecutionPolicy Bypass -File tools\fetch_deps.ps1
  を一度実行してから build.cmd を実行してください。
