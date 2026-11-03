# 入れかた・自動起動・設定・更新・外しかた

[README に戻る](../README.md)

## 必要なもの

- Windows 10 / 11
- .NET Framework 4.8。Windows 10 1903 以降と Windows 11 には標準で入っています。それより前の Windows 10 では、Microsoft から入れてください。
- 本家から入手した `JVLinkServer.exe`
- JV-Link と JRA-VAN の利用キー

## 入れかた

1. [リリース](https://github.com/smee3/jvlinkserver-tray/releases) から `JVLinkServerTray.zip` をダウンロードします。中身は `JVLinkServerTray.exe`、`JVLinkServerTray.ini.example`、`LICENSE` です。リリースには、照合用の `SHA256SUMS.txt` も付いています。
2. 展開する前に、zip を右クリック →「プロパティ」→「許可する」にチェックを入れて「OK」。展開したあとなら、PowerShell で `Get-ChildItem <展開したフォルダ> | Unblock-File` でも同じです。
   - EXE に署名がないためです。これをしないと、初回に SmartScreen の「Windows によって PC が保護されました」が出ます。そのときは「詳細情報」→「実行」で動きます。
3. 好きなフォルダに展開します。
4. `JVLinkServerTray.ini.example` を `JVLinkServerTray.ini` に名前を変え、`ServerExe`（JVLinkServer.exe のフルパス）と `ServerArgs`（JVLinkServer に渡す起動オプション）を自分の環境に合わせます。`ServerExe` は、短い名前（8.3 形式）や subst のドライブを使わない、長いフルパスで書いてください。
5. いまターミナルで動いている JVLinkServer を閉じます（動いたままだと、トレイがそれを引き継ぎ、黒い画面も残ります）。JVLinkServer をほかの方法（スタートアップなど）で自動起動しているなら、それも止めます。
6. `JVLinkServerTray.exe` をダブルクリックして動かします。2回起動しても、2つ目はすぐに終わります。

トレイが起動したとき、JVLinkServer がすでに動いていて答えるなら、新しく起動せず、そのサーバーを引き継いで見張ります（2つ目は起動しません）。

## ログオン時に自動で起動する（任意）

いつも動かしておきたいときだけ、ログオン時に動くタスクを作ります。JV-Link の画面が見えるよう、SYSTEM ではなくログオンしているユーザーで実行します。PowerShell の例です。

```powershell
$dir = 'C:\Tools\JVLinkServerTray'   # EXE を置いた場所
$action   = New-ScheduledTaskAction -Execute "$dir\JVLinkServerTray.exe" -WorkingDirectory $dir
$trigger  = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit 0 -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName 'JVLinkServerTray' -Action $action -Trigger $trigger -Settings $settings
```

- `-ExecutionTimeLimit 0` は「時間で止めない」、`-AllowStartIfOnBatteries -DontStopIfGoingOnBatteries` は「バッテリーで動いていても起動し、止めない」です。
- 次のログオンから自動で起動します。すぐに動かすなら `Start-ScheduledTask -TaskName 'JVLinkServerTray'` です（トレイがもう動いていれば要りません）。
- やめるときは `Unregister-ScheduledTask -TaskName 'JVLinkServerTray' -Confirm:$false` です。

## ini の項目

| キー | 既定値 | 意味 |
| --- | --- | --- |
| `ServerExe` | `C:\JVLinkServer\JVLinkServer.exe` | JVLinkServer.exe のフルパス |
| `ServerArgs` | `--port 8765 --log-dir C:\ProgramData\JVLinkServer\logs --startup-delay-sec 30 --recovery-retry-interval-sec 15` | JVLinkServer に渡す起動オプション。`--port` と `--log-dir` はトレイも読みます |
| `PollSeconds` | 5 | 様子を確かめる間隔（秒） |
| `UnreachableLimit` | 6 | 何回続けて応答がなければ落ちたと判断するか |
| `StartupGraceSeconds` | 60 | 起動直後に応答がなくても待つ時間（秒） |
| `ShutdownTimeoutSeconds` | 20 | 終了時にサーバーが止まるのを待つ時間（秒） |

- ini は UTF-8 で保存してください（いまのメモ帳の既定です）。
- 別の ini で動かしたいときは `JVLinkServerTray.exe --config <ini のパス>` とします。`--config` で渡した ini が見つからないときは、既定値で動かさず、赤で止まります。

## 更新のしかた

ダブルクリックで動かしているときは、メニューの「終了…」で終わらせ（サーバーも止まります）、フォルダの `JVLinkServerTray.exe` を新しいものに置き換えて（ini はそのまま）、もう一度ダブルクリックします。

ログオン時のタスクで動かしているときは、サーバーを止めずに、トレイだけを入れ替えられます。

1. `Stop-ScheduledTask -TaskName 'JVLinkServerTray'` でトレイを終わらせます。サーバーは動き続けます。
2. フォルダの `JVLinkServerTray.exe` を新しいものに置き換えます（ini はそのまま）。ダウンロードした EXE は、上と同じように「許可する」をしておきます。
3. `Start-ScheduledTask -TaskName 'JVLinkServerTray'` で動かします。新しいトレイが、動いているサーバーを引き継ぎます。

## 外しかた

1. メニューの「終了…」で終わらせます（サーバーも止まります）。
2. ログオン時のタスクを作っていたなら、消します（または、元の JVLinkServer の自動起動に戻します）。

   ```powershell
   Unregister-ScheduledTask -TaskName 'JVLinkServerTray' -Confirm:$false
   ```
3. トレイのフォルダを消します。

ログのフォルダにある `JVLinkServerTray_*.log` は残ります。要らなければ消してください。JVLinkServer 本体、JV-Link、JVLinkServer のログには触れません。
