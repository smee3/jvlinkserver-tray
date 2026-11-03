# JVLinkServer Tray

[JVLinkServer](https://github.com/nyoki-mtl/JVLinkServer) を、黒い画面（コンソール）なしで動かし続けるための、Windows のタスクトレイ常駐プログラムです。サーバーの様子をアイコンの色で見せ、止まったら起こし直します。データを見る機能はありません（意図してそうしています）。

**[ダウンロード（JVLinkServerTray.zip）](https://github.com/smee3/jvlinkserver-tray/releases/latest/download/JVLinkServerTray.zip)** · [ほかの版・EXE だけ](https://github.com/smee3/jvlinkserver-tray/releases)

JRA-VAN、JV-Link、本家 JVLinkServer とは関係のない、個人の非公式ツールです。JVLinkServer と JV-Link は入っていません。

## 必要なもの

- Windows 10 / 11 と .NET Framework 4.8（Windows 10 1903 以降には標準で入っています）
- 本家から入手した `JVLinkServer.exe`
- JV-Link と JRA-VAN の利用キー

## はじめかた

1. zip をダウンロードしたら、展開する前に右クリック →「プロパティ」→「許可する」にチェックを入れて「OK」。それから好きなフォルダ（例: `C:\Tools\JVLinkServerTray`）に展開します。
2. `JVLinkServerTray.ini.example` を `JVLinkServerTray.ini` に名前を変え、`ServerExe=` に `JVLinkServer.exe` のフルパスを書きます。ほかの項目は、そのままで動きます。
3. いまターミナルで動いている JVLinkServer を閉じます（動いたままだと、トレイがそれを引き継ぎ、黒い画面も残ります）。JVLinkServer をスタートアップなどで自動起動しているなら、それも止めます。
4. `JVLinkServerTray.exe` をダブルクリックします。タスクバーの右下（隠れているときは「^」の中）に馬の頭のアイコンが出ます。右下の点が緑になれば動いています。起動してしばらくは黄です。

使い終わったら、アイコンを右クリックして「終了…」を選びます（JVLinkServer も止まります）。

### ログオン時に自動で起動する（任意）

いつも動かしておきたいときだけ、PowerShell でログオン時に動くタスクを作ります。次のログオンから、自動で起動します。

```powershell
$dir = 'C:\Tools\JVLinkServerTray'   # 展開したフォルダ
$action   = New-ScheduledTaskAction -Execute "$dir\JVLinkServerTray.exe" -WorkingDirectory $dir
$trigger  = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit 0 -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName 'JVLinkServerTray' -Action $action -Trigger $trigger -Settings $settings
```

やめるときは `Unregister-ScheduledTask -TaskName 'JVLinkServerTray' -Confirm:$false` です。

## 色の意味

| 色 | 言葉 | トレイのふるまい |
| --- | --- | --- |
| 緑 | 動いています | 何もしません |
| 黄 | 確かめています | サーバーは答えますが、まだ正常ではありません。待ちます。10 分続いたら、一度だけ作り直します |
| 赤（白い横棒） | 止まっています | 間隔をあけて起動し直します。起動し直せないわけがあるときは、その理由を出します |
| 灰（輪） | 止めています | メニューで止めた状態です。起動し直しません |

アイコンを右クリックすると、サーバーを止める・起動する・作り直す、ログのフォルダを開く、終了、のメニューが出ます。

## 気をつけること

- **`/shutdown` には認証がありません。** JVLinkServer のポート（例: 8765）は、ファイアウォールなどで、信頼できる相手にだけ開けてください。
- **トレイだけを終わらせても（タスクマネージャー、タスクスケジューラの「終了」など）、サーバーは動き続けます。** 次に起動したトレイが引き継ぎます。サーバーも止めるときは、アイコンのメニューの「終了…」か「サーバーを止める」を使ってください。
- **EXE に署名はありません。** 手順 1 の「許可する」をしなかったときは、初回に SmartScreen の「Windows によって PC が保護されました」が出ます。「詳細情報」→「実行」で動きます。

## くわしく

- [入れかた・自動起動・設定・更新・外しかた](docs/install.md)
- [使いかた（色・メニュー・ほかの PC から作り直す・制限）](docs/usage.md)
- [ビルド・テスト・リリース](docs/development.md)

## ライセンス

[MIT](LICENSE)。本家 [nyoki-mtl/JVLinkServer](https://github.com/nyoki-mtl/JVLinkServer)（MIT）の作者に感謝します。
