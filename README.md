# JVLinkServer Tray

[JVLinkServer](https://github.com/nyoki-mtl/JVLinkServer) を、黒い画面（コンソール）なしで動かし続けるための、Windows のタスクトレイ常駐プログラムです。サーバーの様子をアイコンの色で見せ、止まったら起こし直します。データを見る機能はありません（意図してそうしています）。

## これは何

- JVLinkServer をトレイから起動し、`/health` を見張ります。
- 状態をアイコンの色（緑・黄・赤・灰）とツールチップで知らせます。
- 止まっていたら、間隔をあけて自動で起動し直します。
- ほかの PC から `POST /shutdown` を送ると、トレイがサーバーを起動し直します。

## 非公式です

JRA-VAN、JV-Link、本家 JVLinkServer とは関係のない、個人の非公式ツールです。JVLinkServer と JV-Link は同梱していません。本家 [nyoki-mtl/JVLinkServer](https://github.com/nyoki-mtl/JVLinkServer)（MIT）の作者に感謝します。

## 必要なもの

- Windows 10 / 11
- .NET Framework 4.8（Windows に標準で入っています）
- 本家から入手した `JVLinkServer.exe`
- JV-Link と JRA-VAN の利用キー

## 入れかた

1. 好きなフォルダに `JVLinkServerTray.exe` と、`JVLinkServerTray.ini.example` を `JVLinkServerTray.ini` に名前を変えたものを置きます。
2. `JVLinkServerTray.ini` の `ServerExe`（JVLinkServer.exe のフルパス）と `ServerArgs`（JVLinkServer に渡す起動オプション）を自分の環境に合わせます。`ServerExe` は、短い名前（8.3 形式）や subst のドライブを使わない、長いフルパスで書いてください。
3. ログオン時に動くタスクを作ります。JV-Link の画面が見えるよう、SYSTEM ではなくログオンしているユーザーで実行します。PowerShell の例です。

   ```powershell
   $dir = 'C:\Tools\JVLinkServerTray'   # EXE を置いた場所
   $action   = New-ScheduledTaskAction -Execute "$dir\JVLinkServerTray.exe" -WorkingDirectory $dir
   $trigger  = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
   $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit 0
   Register-ScheduledTask -TaskName 'JVLinkServerTray' -Action $action -Trigger $trigger -Settings $settings
   ```
4. JVLinkServer をほかの方法（スタートアップなど）で自動起動しているなら、止めます。
5. 一度ログオンし直すか、タスクを実行します。

EXE に署名がないので、初回は Windows の SmartScreen が警告を出すことがあります。

### ini の項目

| キー | 既定値 | 意味 |
| --- | --- | --- |
| `ServerExe` | `C:\JVLinkServer\JVLinkServer.exe` | JVLinkServer.exe のフルパス |
| `ServerArgs` | `--port 8765 --log-dir C:\ProgramData\JVLinkServer\logs --startup-delay-sec 30 --recovery-retry-interval-sec 15` | JVLinkServer に渡す起動オプション。`--port` と `--log-dir` はトレイも読みます |
| `PollSeconds` | 5 | 様子を確かめる間隔（秒） |
| `UnreachableLimit` | 6 | 何回続けて応答がなければ落ちたと判断するか |
| `StartupGraceSeconds` | 60 | 起動直後に応答がなくても待つ時間（秒） |
| `ShutdownTimeoutSeconds` | 20 | 終了時にサーバーが止まるのを待つ時間（秒） |

別の ini で動かしたいときは `JVLinkServerTray.exe --config <ini のパス>` とします。`--config` で渡した ini が見つからないときは、既定値で動かさず、赤で止まります。

## 色の意味

アイコンは、青い角丸の四角に白い「JV」、右下に状態の点がつきます。

| 色 | 言葉 | トレイのふるまい |
| --- | --- | --- |
| 緑 | 動いています | 何もしません。続いた時間をメニューに出します |
| 黄 | 確かめています | 何もしません（JVLinkServer は約1秒で自分で立ち直ります）。10 分続いたら、一度だけ作り直します |
| 赤（白い横棒） | 止まっています | 10 → 30 → 60 → 120 → 300 秒の間隔で起動し直します |
| 灰（輪） | 止めています | メニューで止めた状態です。起動し直しません |

## メニュー

アイコンを右クリックします。

- 状態の行: 状態と、その続いた時間
- 「JV-Link の自動再起動 N 回 · 最後（…）」の行: JVLinkServer 本体が JV-Link を自動で再起動した回数と、最後の理由
- ログのフォルダを開く
- サーバーを作り直す（止まっているとき、起動できないときは選べません）
- サーバーを止める / サーバーを起動する
- 終了…（「サーバーも止まります。JVLinkServer につないでいるプログラムは、データを取れなくなります。終了しますか？」と確かめます）

「サーバーを起動する」は、まず同じ EXE のパスで残っている JVLinkServer のプロセスをすべて止めてから起動します。手で起動した JVLinkServer も止まります。

同じファイル名でも、パスを読めないプロセス（ほかのユーザーのもの、管理者権限のもの）は止めません。その場合は赤で「片付けられないプロセスがあります」と出し、2つ目のサーバーは起動しません。

タスクスケジューラの「終了」や `Stop-ScheduledTask` で終わらせるのはトレイだけで、サーバーは動き続けます。次に起動したトレイが引き継ぎます。サーバーを止めるときはメニューを使ってください。

## ほかの PC から作り直す

ポートに届く相手なら、`POST /shutdown` を送れます。トレイはサーバーが落ちたことを見て、約10秒後に起動し直します。

```powershell
Invoke-RestMethod -Method Post http://<Windows の PC>:8765/shutdown
```

JVLinkServer の `/shutdown` に認証はありません。ポートはファイアウォールなどで、信頼できる相手にだけ開けてください。

## 外しかた

1. メニューの「終了…」で終わらせます（サーバーも止まります）。
2. ログオン時のタスクを消します（または、元の JVLinkServer の自動起動に戻します）。

   ```powershell
   Unregister-ScheduledTask -TaskName 'JVLinkServerTray' -Confirm:$false
   ```
3. トレイのフォルダを消します。

ログのフォルダにある `JVLinkServerTray_*.log` は残ります。要らなければ消してください。JVLinkServer 本体、JV-Link、JVLinkServer のログには触れません。

## 決まりと制限

- 同じ EXE を2つ動かす使いかたは扱いません。
- EXE に署名がないので、初回は SmartScreen の警告が出ます。
- `ServerExe` が見つからないときは、赤で止まります。
- 同じファイル名でパスを読めないプロセスがあるときは、赤で止まります（上の「メニュー」を参照）。

## ビルドとテスト

Windows で `build.cmd` を実行します。Windows に標準で入っている `csc`（C# 5）だけを使い、追加の道具は要りません。`bin\` に `JVLinkServerTray.exe` ができます。

テストは `bin\JVLinkServerTray.Tests.exe` を実行します。

## ライセンス

[MIT](LICENSE)
