## 推送至 IDM 需要本地接收端（可选）

「推送至 IDM」要经过本机一个小程序中转（脚本 → `127.0.0.1:16888` → IDM），这样才能带上 Referer / Cookie，
夸克、123 这类必须有请求头的直链才推得动。其它功能（获取直链、复制链接、保存到网盘）都不需要它。

### 三步（只需一次，不用装 Node / .NET）

1. **下载**：[`IDMPush.exe`](https://github.com/dx-y/linkswift-enhanced/releases/latest/download/IDMPush.exe)（约 11 MB 单文件）
2. **双击运行一次**：自动注册 `linkswift://`、自动找到 `IDMan.exe`、写好配置；弹出「配置完成」后点确定关闭即可（不用保持运行）
3. 回到网盘页面点 **「推送至 IDM」**

- 空闲 120 秒自动退出；不开机自启；只监听 `127.0.0.1`，只接受本脚本的请求；不需要管理员权限
- 提示找不到 IDM 时：`IDMPush.exe --idm "IDMan.exe 的完整路径"`
- 不装也可以：接收端不可用时会自动降级为「复制 IDM 命令」到剪贴板，`Win+R` 粘贴回车即可导入
