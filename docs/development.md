# ビルド・テスト・リリース

[README に戻る](../README.md)

## ビルド

Windows で `build.cmd` を実行します。Windows に標準で入っている `csc`（C# 5、.NET Framework 4.8）だけを使い、追加の道具は要りません。`bin\` に次のものができます。

- `JVLinkServerTray.exe`: トレイ本体
- `JVLinkServerTray.Tests.exe`: テスト
- `FakeJVLinkServer.exe`: テスト用の偽のサーバー（`/health` と `/shutdown` に答える）
- `MakeIcon.exe`、`JVLinkServerTray.ico`: EXE のアイコンを、トレイと同じ描きかたで作るためのもの（リポジトリに画像は置きません）

C# 5 で書ける範囲にとどめます（`$""` や `?.` は使えません）。

## テスト

`bin\JVLinkServerTray.Tests.exe` を実行します。名前の一部を渡すと、そのテストだけを動かします（例: `bin\JVLinkServerTray.Tests.exe Watchdog`）。失敗があれば、終了コードが 0 以外になります。

テストは `bin\FakeJVLinkServer.exe` を 18765〜18779 のポートで起動します。本物の JVLinkServer と、そのポート（8765 など）には触れません。

## ほかの PC から Windows でテストする

コードを Mac などで書き、SSH で届く Windows でビルドとテストを走らせるためのスクリプトです。

- `scripts/remote-test.sh [名前の一部]`: ソースを送り、`build.cmd` とテストを走らせます。

  ```sh
  JVTRAY_SSH_HOST=<SSH の相手> JVTRAY_REMOTE_DIR=<Windows 側のフォルダ（/ 区切り）> scripts/remote-test.sh
  ```
- `scripts/remote-smoke.ps1`: ログオンしているユーザーのセッションで、トレイを試験用の ini（偽のサーバー、ポート 18775）だけで動かして確かめます。`remote-test.sh` のあとに、Windows 側で実行します。中断したときの片付けは `-CleanupOnly` です。

  ```powershell
  powershell -NoProfile -ExecutionPolicy Bypass -File <リポジトリ>\scripts\remote-smoke.ps1
  ```

`bin\JVLinkServerTray.exe` は、試験用の ini を `--config` で渡して動かします。EXE の隣に ini がなければ赤で止まりますが（0.2.0 から。0.1.0 は既定値のポート 8765 で動き、同じ PC で動いている本物のサーバーを見張り始めました）、`bin\` に ini を置いたまま `--config` なしで動かすと、その ini で動きます。

- `remote-test.sh` は、`JVTRAY_REMOTE_DIR` がドライブの直下（`C:/` など）や `.`・`..` を含むときは、何もせずに止まります。そのフォルダの `src`・`tests`・`scripts`・`tools` を消してから送るためです。
- `remote-smoke.ps1` は、前の回の残り（試験用のタスク、`bin\` のトレイや偽のサーバー、`bin\JVLinkServerTray.ini`）があると、何も変えずに止まります。`-CleanupOnly` で片付けてから、もう一度実行します。`bin\JVLinkServerTray.ini` は、スモークテストが書いたもの（1行目が `; smoke test only`）だけを消します。

## リリース

GitHub Actions（`.github/workflows/build.yml`）が、push と pull request のたびに Windows でビルドとテストを走らせ、配るものを成果物（artifact）として 14 日残します。

- `JVLinkServerTray.zip`: `JVLinkServerTray.exe`、`JVLinkServerTray.ini.example`、`LICENSE`
- `JVLinkServerTray.exe`: 更新用の EXE だけ
- `SHA256SUMS.txt`: 上の2つの SHA-256

リリースの出しかた:

1. `src/Core/TrayVersion.cs` の `Value` を新しい版にして、コミットして push します。
2. 同じ版のタグを付けて push します。

   ```sh
   git tag v0.2.0
   git push origin v0.2.0
   ```
3. Actions が、ビルドとテストのあと、上の3つを付けた GitHub のリリースを作ります。タグ（`v` + 版）と `TrayVersion.Value` が違うときは、リリースを作らずに止まります。

README のダウンロードのリンクは、最新のリリースの `JVLinkServerTray.zip` を指します。ファイルの名前は版ごとに変えないでください。
