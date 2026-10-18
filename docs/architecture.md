# TouchLock 1.0.0 架构

全部为 .NET 10 / C#。App 与 Setup 使用 WinForms，其余项目不依赖桌面 UI。

App 使用 Windows Desktop 自带的 UI Automation 组件。App、Service、Recovery 发布到同一负载目录时统一携带完整 Windows Desktop 运行时，避免 WinForms 配置的 `WindowsBase.dll` 占位程序集覆盖监控所需的实现；安装包仍自包含，不要求用户另装运行时。

| 项目 | 职责 |
| --- | --- |
| App | 当前交互会话的托盘、单向全局快捷键、口令输入、蓝牙设置页面监控、About |
| Service | LocalSystem；唯一状态源、设备事务、口令验证、恢复监督 |
| Recovery | 独立 SYSTEM 进程；持有确切服务句柄，失联后终止旧服务并恢复 |
| Shared | V2 IPC、PBKDF2、受保护目录/互斥量、SetupAPI/Configuration Manager 后端 |
| Setup | 需要 UAC 的自包含安装包；嵌入离线负载，安装/重置/卸载 |
| UI | App 与 Setup 链接编译的小型口令对话框源文件 |

## 安装位置

默认位置为 `%ProgramFiles%\TouchLock`，首次安装可选择本地 NTFS 固定磁盘中的新目录。规范化与权限校验拒绝网络路径、系统目录、设备名称、链接和普通用户可替换的上级目录；新目录以 Administrators 所有者和受保护 ACL 原子创建，不更改已有父目录。HKLM 卸载项的 `InstallLocation` 保存实际位置，服务命令行、快捷方式和维护使用该位置；卸载先保存位置再删除注册项，避免误回退到默认目录。密码与恢复记录继续位于 ProgramData。

## 设备事务

SetupAPI 枚举当前存在的全部设备类。`DeviceSelection` 优先精确匹配全部 `Kuma - Touch Screen Extend Service`，没有 Kuma 时通过 `HID_DEVICE_UP:000D_U:0004` 识别标准 Touchscreen。Kuma 不因同容器的鼠标节点被拒绝；明确的其他输入身份、蓝牙祖先、异常状态仍拒绝。通用 HID 保留混合集合和鼠标伴随集合检查。枚举不改变设备，禁用前再次检查身份。

受保护的 `Global\TouchLock.DeviceOperations.v2` 互斥量串行化服务、恢复进程和安装程序的设备操作。恢复记录使用 WriteThrough、Flush(true) 与同目录原子替换；只记录观察到启用的触摸节点，每次禁用前先写记录。`CM_Disable_DevNode` 仅传 `CM_DISABLE_UI_NOT_OK (4)`，随后核对禁用状态；全部成功才报告 Locked。失败则回滚，回滚失败继续保留记录并重试。

`CM_Enable_DevNode` 参数为零，核对节点无问题且已启动后才清理对应记录。移除的节点保留待恢复，直到重新连接或重启后恢复确认。不会重新启用锁定前已禁用的设备。临时禁用默认不跨重启保存：[微软说明](https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_disable_devnode)。

## 认证与 IPC

管道 `TouchLock.Control.v2` 与 `TouchLock.Password.v2` 拒绝远程客户端，DACL 仅 SYSTEM 和 Interactive。服务核对客户端当前控制台会话、确切进程路径及创建时间；客户端核对管道服务 PID 与 SCM 实际 PID。普通请求仅状态/锁定/开启蓝牙设置限制/开始/取消。认证请求绑定 PID、进程创建时间、会话、一次性 GUID 与 180 秒期限；使用后立即失效。

口令通过有长度上限的二进制帧传输，与 JSON 控制消息分离。口令输入读取原生编辑控件到临时字符数组；控件、字符数组及字节数组使用后清除，不创建业务口令 string。无法保证消除 Windows 控件或操作系统内部所有历史副本。

PBKDF2-HMAC-SHA256：600,000 迭代、16 字节随机盐、32 字节哈希，恒定时间比较。记录含固定版本与算法参数，位于 `%ProgramData%\TouchLock\password.json`，SYSTEM/Admin ACL，禁止链接路径。错误五次后全局暂停 30 秒。初始设置和忘记口令重置由提升权限 Setup 执行；复位先停止服务并恢复设备。

Setup 卸载须提供当前管理口令，并在停止服务、恢复设备或删除文件前直接验证受保护的口令记录；错误、取消或配置损坏不执行卸载。密码缓冲区在对话框或测试入口结束后清除。管理员忘记口令的重置恢复入口保留，不能将 Windows 提升权限直接当作卸载口令验证成功。

## 蓝牙设置限制

Service 保存唯一的勾选状态，`block-bluetooth-settings` 无口令开启；取消勾选使用 `AllowBluetoothSettings` 认证意图，沿用一次性请求、当前管理口令和错误冷却。认证成功仅修改蓝牙限制，不恢复触摸设备、不删除恢复记录。V2 状态消息追加可选字段，既有认证意图编号保持不变。

状态以版本 1 的 JSON 原子保存到 `%ProgramData%\TouchLock\restrictions.json`，继承目录的 SYSTEM/Admin ACL。旧安装没有文件时默认未勾选；损坏时保持限制并显示错误，输入正确口令取消限制可重新写入有效配置。保存失败不更新内存勾选状态。默认卸载保留配置，显式删除配置时一并删除；重置口令保留勾选状态。

App 在独立 MTA 线程中每 250 毫秒扫描当前会话的系统设置窗口，校验 `SystemSettings.exe` 系统路径，并兼容 `ApplicationFrameHost` 承载窗口。通过蓝牙页面内容控件的 AutomationId 判断当前页面，不匹配侧栏或搜索中的“蓝牙”字样；命中后向对应窗口发送 `WM_CLOSE`，不终止进程。UI Automation 遵守[独立 MTA 线程要求](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-threading)；控件标识可能随系统版本变化，需在目标系统验收。取消勾选与发送关闭消息串行化；托盘退出停止监控，重启托盘后从服务状态继续执行。

只处理系统“设置”中的蓝牙页面，不修改蓝牙开关或配对，不覆盖快速设置、传统控制面板或第三方工具。不是 Windows 访问控制或阻止所有蓝牙操作的安全策略。

## 状态与恢复

`Unlocked → Locking → Locked → Authenticating → Restoring → Unlocked/Stopping`。硬件操作由单个串行 worker 执行，不占用 IPC 状态锁；命令成功表示接受操作，客户端轮询同一 OperationId 到完成并检查 OperationError。状态包含实际目标数、禁用数和逐阶段汇总，不能把接受操作当成设备成功。快捷键先请求新设备快照，再选择锁定或密码恢复。

认证错误、取消和超时返回认证前状态；修改口令保留锁定状态。恢复失败保持 Restoring 和恢复记录。设备事务有进度回报；worker 超过 15 秒无进度时停止守护续约，守护仍按 30 秒失联机制终止旧服务后恢复。后台枚举不覆盖前台操作的结果，完整退出等待恢复完成并短暂保留最终状态供托盘读取。

Service 主循环处理设备/会话变化后每秒续约。不是脱离业务处理的独立心跳线程。Recovery 先打开服务的精确进程句柄，再发 Ready；没有 Ready 不允许锁定。服务退出则恢复；30 秒失联则先终止并等待旧服务，再获取互斥量恢复。新服务先恢复旧记录，不恢复旧锁定。SCM 异常重启间隔 10 秒，连续失败后 60 秒。

只以设备禁用实现物理触摸停止，不承诺软件输入注入、厂商私有通道或远控被拦截。标准用户无法写安装/配置目录或停止服务；本机管理员和 SYSTEM 位于信任边界之外。口令持有人获得本软件解锁/退出权限，不代表 Windows 管理员身份。
