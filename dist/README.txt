SnipOCR (portable) - 画面OCRツール
====================================

■ これは何か
  画面の任意の範囲を Snipping Tool の要領で矩形選択し、その部分を OCR して
  テキストをクリップボードにコピー／結果ウィンドウに表示する常駐ツールです。
  - 完全オフライン動作（インターネット不要）
  - 管理者権限不要・インストール不要（このフォルダを置くだけ／USBでも可）
  - OCR エンジンは2種類を切替可能（既定=auto）:
      * Tesseract（同梱 tesseract フォルダ）… 高精度。小さい字・日本語に強い。
      * Windows 内蔵（Windows.Media.Ocr）    … 高速・軽量。追加ファイル不要。
    tesseract フォルダごとコピーすれば Tesseract が自動で使われます（推奨）。
    軽量にしたい場合は tesseract フォルダを削除すれば Windows 内蔵に切替わります。

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

■ ネット経由でコピーした場合の注意（ブロック解除）
  他PCやメールで受け取った exe は「ブロック」属性が付くことがあります。
  実行できない／警告が出る場合：
  SnipOCR.exe を右クリック → プロパティ → 下部の「許可する」にチェック → OK
  （管理者権限は不要です）

■ フォルダ構成
    SnipOCR.exe            … 本体
    README.txt             … このファイル
    settings.sample.txt    … 設定サンプル（settings.txt にリネームで有効）
    tesseract\             … 同梱 Tesseract（tesseract.exe + tessdata\jpn/eng）
                             ※このフォルダごと職場PCへコピーしてください

■ 設定（任意）
  exe と同じフォルダに settings.txt を置くと動作を変えられます（無くても既定で動作）。
  例（settings.sample.txt を settings.txt にリネームして編集）:
    hotkey=Ctrl+Shift+S     … 起動ホットキー
    engine=auto             … auto / winrt / tesseract
    language=auto           … WinRT用言語 auto / ja / en など
    mode=normal             … normal / line / table / sparse / small
    copyformat=auto         … auto / plain / excel / markdown / oneline
    tesseractlangs=jpn+eng  … Tesseract用言語
    tesseractpsm=6          … Tesseract解析モード(6=ブロック,7=1行,11=疎。modeで上書きあり)
    binarize=false          … WinRTで二値化前処理するか
    showwindow=true         … 結果ウィンドウを表示するか
    copytoclipboard=true    … 自動でクリップボードにコピーするか
  ※ フォルダが書込不可の場合、設定は %LOCALAPPDATA%\SnipOCR\settings.txt に保存されます。

■ 自動起動したい場合（任意・管理者権限不要）
  Win+R →「shell:startup」→ 開いたフォルダに SnipOCR.exe のショートカットを置く。

■ うまく動かないとき
  - 「OCR 言語リソースが見つかりません」通知が出る
      → 設定 → 時刻と言語 → 言語 → 日本語 →「オプション」→「文字認識」機能を追加。
        （この追加は環境により管理者権限が必要な場合があります）
  - 管理者権限で動いている別アプリのウィンドウ上ではキャプチャできないことがあります。
  - UAC 画面・ロック画面・セキュアデスクトップはキャプチャできません（OS 仕様）。

■ ビルドし直すには（開発者向け）
  build.cmd をダブルクリック。Windows 標準の C# コンパイラと Windows SDK のみ使用。
  Visual Studio・NuGet・インターネットは不要です。
