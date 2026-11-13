# 当前实现协议与配置

## PowerShell 与快捷脚本

终端独立要求 `terminal.control`，默认不授予，电脑在「手机与权限」配置。USB、LAN 和 Bluetooth 使用相同认证及权限检查。蓝牙支持 task.templates/run/status.get/cancel、request.status 及 terminal.*，可推送 task.updated 和 control.paused。0.19 起，蓝牙也支持桌面应用列表、图标、启动、音量、静音与媒体指令。服务端在 session.hello 返回 apps、audio 和 media 能力，并可推送 app.catalog.changed 和 audio.state。

| action | payload / result |
|---|---|
| terminal.open | columns 20～300、rows 5～120；返回 id、shell、running、inputSequence。当前设备重复打开且进程仍运行时复用会话。旧请求中非空 profileId 会收到 UNSUPPORTED_ACTION。 |
| terminal.read | id、offset 非负字节偏移；返回 offset、data（Base64 UTF-8 VT 字节）、reset、running。每块最多 12 KiB。 |
| terminal.write | id、sequence 正整数、text 最多 8192 UTF-16 单元。会话内连续序号去重，重复序号不再次输入，跳号拒绝。 |
| terminal.resize | id、columns、rows，范围同 open。 |
| terminal.close | 结束该设备的终端进程。 |

Windows 通过 ConPTY 启动 PowerShell 7（标准安装目录存在时）或系统 Windows PowerShell 5.1，参数 -NoLogo -NoProfile，用户目录作为工作目录。手机本地离线打包 xterm.js 5.5.0 / addon-fit 0.10.0，以 Windows Terminal 默认配色呈现 VT。WebView 禁止网络、文件和内容访问及外部导航，桥接仅提供终端输入和尺寸。依赖许可证随 APK 打包，来源及摘要见 assets/terminal/DEPENDENCIES.json。

读偏移对应终端字节流，响应 offset 是下一次读偏移。手机按顺序读取并交给 xterm 的流式 UTF-8 解析器。电脑保留最近 256 KiB；偏移已被淘汰时 reset=true，从保留尾部开始并清屏。输入不写持久化去重表，输入结果不明时停止发送并提示重开，避免自动重复命令。关闭终端、撤权、解除配对、暂停控制、最后一条认证链路断开及服务退出均结束终端。后台暂停读输出，连接仍在时保留进程。

快捷脚本复用 TaskTemplate / TaskRunner 和 tasks.read/run 权限。电脑添加 .ps1 文件后生成固定 PowerShell 命令及工作目录，通过固定 -Command 设置 UTF-8 输出并调用经单引号转义的本机脚本路径。默认无手机参数，不修改执行策略。高级参数模板保持已有规则；任务日志按现有有界结果保存，不写诊断。

本文件描述当前代码实际使用的协议。早期设计中不同的字段名以本文件和 `Models.cs` / `AgentServer.cs` 为准，protocolVersion 为 1。

## 连接与认证

- USB：`wss://127.0.0.1:8765/ws`，通过 ADB reverse 到电脑回环端点。
- LAN：`wss://本机地址:8766/ws`，启用局域网后自动监听可用 WLAN 或以太网的私有 IPv4 地址。电脑将地址加入证书和配对码。
- `/health` 只返回服务名和协议版本，不接受控制。
- 发现类型 `_mcompanion._tcp.`，TXT 含 `hostId` 和 `v=1`。各接口独立发布服务。发现不建立信任。
- QR 的 `type` 为 `magic-companion-pair`，包含 `hostId`、`hostName`、`fingerprint`、`pairSessionId`、32 字节随机 `secret`（Base64）、`expiresAt` 和 `candidates:[{transport,url}]`。
- 电脑证书 SPKI 的 SHA-256 使用 Base64URL、无 padding，前缀 `sha256:`。Android 同时检查固定公钥指纹、证书有效期和默认主机名验证。

服务端先发送 `type:challenge`、`protocolVersion:1`、`hostId`、随机 `linkId` 和 `nonce`。签名原文严格为 UTF-8：

```text
1\n{hostId}\n{linkId}\n{nonce}\n{deviceId}
```

`deviceId` 为设备 SPKI SHA-256 的小写十六进制。客户端回 `type:authenticate`、`deviceId`、`publicKey`（SPKI Base64）、`signature`（ECDSA P-256 / SHA-256，DER Base64）、`deviceName`。首次配对另带 `pairSessionId` 和 `pairProof`（以 QR secret 对上述原文做 HMAC-SHA256，Base64）。

电脑审批后发送 `type:authenticated`、`sessionId`、`deviceId`、`permissions`、`hostId`、`hostName`。同一手机两条链路共享逻辑 sessionId，但各自完成挑战认证。六位核对码为挑战原文 SHA-256 的大写十六进制前六位。二维码仅 2 分钟有效、最多 5 次尝试、一次使用；结束配对后手机不持久化 QR secret。

## 普通消息

待办快照中的 `items[].textColor` / `backgroundColor` 为可选的 `#RRGGBB` 字符串或 null；`color` 保留用于旧数据的强调色。缺失和 null 均回退默认样式，所有颜色在电脑添加或编辑时配置，随快照同步。

所有业务请求均包含：

```json
{"protocolVersion":1,"messageType":"request","sessionId":"...","deviceId":"...","requestId":"UUID","action":"todo.subscribe","payload":{}}
```

成功响应为 `messageType:response, requestId, status:ok, result`；失败响应为 `status:error, error:{code,message,retryable:false}`。服务端推送为 `messageType:event, eventType, payload`。普通 JSON UTF-8 消息最多 65536 字节。

| action | payload / result |
|---|---|
| session.hello | 返回 capabilities 和可用时的电脑蓝牙地址 |
| session.unpair | 当前已认证手机撤销自身授权，电脑推送 `device.revoked` 后关闭所有该设备的连接 |
| session.ping | 返回 alive |
| session.visibility | foreground；进入后台停止指标/音量订阅 |
| request.status | requestId；返回原请求 state 和完整响应 result |
| screen.capture | 空对象。需要 `screen.capture` 权限。电脑将每个活动显示器保存为单独的 PNG，返回 count。支持蓝牙、USB 和 LAN，按请求 ID 去重。 |
| todo.snapshot.get / todo.subscribe | 完整快照；订阅响应中位于 snapshot |
| todo.change | 手机在拥有 todos.read 和 todos.write 时，仅可执行 setCompleted，且 completed 必须为 true。操作返回完整快照，同一个请求 ID 跨连接去重。 |
| system.snapshot.get / system.metrics.subscribe | 返回 CPU、内存、各盘符已用及总量、物理磁盘读写速率和网络收发速率。速率单位为字节每秒。 |
| app.catalog.get | items：appId、displayName、iconId、launchAvailable |
| app.icon.get | iconId；返回 mime / dataBase64 |
| app.launch | appId；只接受电脑允许项 |
| audio.state.get / audio.state.subscribe | revision、level（0～1）、muted、available |
| audio.volume.set / audio.mute.set | level / muted；返回实际音量状态 |
| media.command | command 为 playPause / previous / next |
| task.templates | 需要 tasks.read，返回允许的 items。兼容旧手机的 hotkeys 字段始终为空数组 |
| task.run | templateId、params 对象；返回 jobId 等状态 |
| task.status.get / task.cancel | jobId；只允许读取/取消所属设备的任务 |

待办快照为 `{revision,updatedAt,items:[{id,title,completed,sortOrder,color?,textColor?,backgroundColor?}]}`。revision 持久化且递增。手机仅可提交完成操作，不能新增、编辑标题或配色。

推送事件包括 `todo.snapshot`、`system.metrics`、`audio.state`、`app.catalog.changed`、`permission.changed`、`device.revoked`、`control.paused`、`task.updated`。

非幂等操作 `app.launch / task.run / media.command` 按 deviceId + requestId 去重。相同 ID 不同内容会被拒绝；完成结果保留至少 10 分钟。异常重启后的执行中请求记为 unknown。手机不自动重放结果未知的操作，只查询原请求状态。

## 电脑任务示例

任务例子（替换为电脑实际存在的程序和工作目录）：

```json
[{"id":"version","name":"查看 SDK 版本","executable":"C:\\Program Files\\dotnet\\dotnet.exe","arguments":["--version"],"workingDirectory":"E:\\02.Code\\SupperScreen","timeoutSeconds":30}]
```

可选 `parameterPatterns:{"name":"[a-z]{1,20}"}`，参数占位符使用 `${name}`，运行时手机提供 `params:{"name":"hello"}`。每个值需通过电脑配置的完整正则匹配，不经 shell 拼接。最多 4 个并行任务，日志至多 50 条且 UTF-8 总量 8KB，完成状态和有限日志按设备归属保留。重启前未完成的任务结果显示 unknown，不自动重跑。
