# IDMPush 本地接收端（v3）

「推送至 IDM」用的本地中转程序。单文件、免安装、自带 .NET 运行时。

- 源码：`Program.cs`（约 700 行，C# / .NET 8）
- 构建：`dotnet publish -c Release -r win-x64 --self-contained true`（产物约 11 MB，GUI 子系统无控制台窗口）
- 行为：首次运行注册 `linkswift://`（HKCU，免管理员）+ 自动探测 `IDMan.exe` + 写配置；空闲 120 秒自动退出；不常驻、不开机自启；只监听 `127.0.0.1:16888`，只接受带 `X-LS-Client: linkswift-userscript/2` 的请求。

接口（与油猴脚本的 `LOCAL_BRIDGE` 对齐）：

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/v1/ping` | 返回 `ok/version/idm/idmPath` |
| POST | `/v1/push` | `{v,url,filename,filesize,referer,ua,cookie,confirm}`，通过 IDM COM 接口推送 |
| GET | `/v1/123pan/tasks` | 读 123 客户端 SQLite，返回任务进度与速度 |

命令行：`--setup` 重新配置、`--idm "路径"` 指定 IDM、`--port`、`--idle-exit`、`--silent`。
