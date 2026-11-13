# 09｜Windows Agent 详细设计

> 本篇保留早期设计及测试编号。当前产品范围以 [产品需求](01-产品需求PRD.md) 为准，协议及验收状态分别见 [实现协议](../PROTOCOL-IMPLEMENTED.md) 与 [验收记录](../VALIDATION.md)。

## 1. 技术栈与模块

C# / .NET 10、WPF、Generic Host / DI / Logging，Kestrel 提供 WSS，SQLite 保存业务数据，DPAPI/系统证书机制保护密钥。目标平台为 Windows 11 x64。

```text
src/windows/
  MagicCompanion.Agent/       # Host、托盘和 WPF UI
  MagicCompanion.Core/        # 信任、权限、逻辑会话、动作注册
  MagicCompanion.Transport/   # LAN / USB
  MagicCompanion.System/      # CPU、内存、磁盘空间、音量、应用
  MagicCompanion.Todos/       # 本地编辑、持久化和快照发布
  MagicCompanion.WindowRemote/ # P0 窗口映射、捕获、输入和媒体
  MagicCompanion.Storage/     # SQLite、迁移和密钥保护
```

首版同进程运行，功能采用内置模块注册。UI 关闭可保留托盘，显式退出关闭监听与临时配对会话。

## 2. 启动与恢复

1. 加载配置、数据库迁移、PC 身份、唯一可信手机及权限。
2. 加载待办列表和持久化 revision；待办加载失败显示错误，不以空列表覆盖数据库。
3. 启动 USB 回环 :8765 与选定私有 LAN :8766。
4. 初始化 mDNS、选定 ADB 设备监控和按订阅采样。
5. 托盘提供工作台状态、待办、连接手机、设置、暂停控制和退出。

未连接手机仍可编辑待办。重启保持事项 ID、完成状态、顺序及 revision，不能归零导致手机拒绝新快照。

## 3. 待办编辑

列表字段为标题、完成状态和顺序；提供新增、改名、上移/下移、完成/恢复、删除及清空已完成事项。删除与清空在 PC 明确确认。

使用 TodoService 的本地接口：校验 → SQLite 单事务写列表与 revision → 提交 → 发布完整快照。任何一步失败不得显示“已保存”；已提交但推送失败仍显示“已保存，等待手机同步”。

同进程内串行处理待办写入，revision 与列表属于同一事务。订阅建立时原子取得当前版本并注册，或以等价机制保证初始化期间不漏更新。

## 4. 业务模块

| 模块 | 范围与边界 |
|---|---|
| 指标 | CPU、内存、磁盘剩余空间；数据源以官方 Windows 接口与实机核对为准 |
| 音频 | 默认输出的总音量/静音及变化事件；P1 当前媒体会话的基本控制 |
| 应用 | PC 应用选择器搜索/选择本机程序、解析稳定 ID/名称/图标，手机仅取允许目录并提交 ID |
| 待办 | 本地编辑、SQLite 保存、todos.read 授权后发布快照 |
| 预设热键 | P1，PC 保存 presetId 与固定组合键 |
| 任务 | P1，模板、允许参数、工作目录、超时、有限输出、取消进程树 |
| 诊断 | 端点、ADB、证书指纹、请求 ID、待办版本与最近同步状态 |

## 5. 线程、权限和安装

- async I/O 承担连接和采样，WPF Dispatcher 只更新界面。
- 控制与待办事件队列有界；连续待办编辑可合并为最新快照，但最终版本必须送达或重连可恢复。
- 指标无订阅时降低采样，避免阻塞操作和待办编辑。
- 默认当前登录用户权限，用户级可选自启。
- 防火墙仅对所选私有网络和必要端口放行；不改网络路由。
- Agent 暂停控制时拒绝新控制动作，停止 P0 窗口输入并释放注入状态；只读指标和待办可继续展示。

## 6. ADB 诊断

```powershell
adb devices -l
adb shell getprop ro.build.version.release
adb shell getprop ro.build.version.sdk
adb -s SERIAL reverse tcp:8765 tcp:8765
adb -s SERIAL reverse --list
```

根据选定设备建立映射；不因诊断自动执行手机命令或读取手机文件。

## 7. 已确认的实现参考

- 参考 DroidDeck 的 USB 桥接拆分：按显式配置、现有 Android SDK 与 PATH 查找 ADB；只管理选定设备的本项目端口，检测规则缺失后补建，并反馈授权/离线状态。
- 检测与定期补查分开：不直接照搬参考项目的轮询周期，拔线响应需满足本项目恢复目标。
- 参考内置动作注册：每个处理器声明 action、参数约束、所需权限与幂等属性，由 DI 注册到统一 CommandDispatcher；不引入动态加载。
- 允许的程序、固定组合键和任务模板由 PC 管理，手机只提交预设 ID 和允许的参数，不能通过面板布局改写执行定义。
- USB 转发到 PC 后即使呈现为回环地址，也按远端链路鉴权；本地待办编辑直接调用进程内服务，不凭网络来源地址获得写权限。

源码入口见文档 24。上述内容是实现机制细化，不自动增加参考项目的其他产品功能。

## 8. 已批准的体验与窗口扩展

P0：快捷键唤起待办，Enter 保存并保留连续输入焦点；快捷键冲突显示可改绑错误，输入法组合不提交。音量/静音订阅真实 PC 变化；动作卡片执行状态不把收到请求等同于成功。

P1：最近一次删除/清空保留本会话撤销记录，下次写入或退出后失效；撤销恢复原 ID 和新 revision。媒体操作优先当前系统媒体会话，无会话/不支持时提示不可用。

P0 WindowRegistry/WindowCaptureService/WindowInputRouter 依文档 12 实现：允许 appId→真实 GUI 窗口→不透明 windowId；Windows.Graphics.Capture 获取所选窗口，JPEG 经独立 WSS；输入前确认身份、前台和几何，停止时清注入状态。采用当前用户权限，不能激活或无法捕获时暂停；无用户连接时停止编码。图像和输入不持久化。
