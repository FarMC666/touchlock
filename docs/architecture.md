# TouchLock 1.0.0 架构

全部为 .NET 10 / C#。App 与 Setup 使用 WinForms，其余项目不依赖桌面 UI。

| 项目 | 职责 |
| --- | --- |
| App | 当前交互会话的托盘、单向全局快捷键、口令输入、About |
| Service | LocalSystem；唯一状态源、设备事务、口令验证、恢复监督 |
| Recovery | 独立 SYSTEM 进程；持有确切服务句柄，失联后终止旧服务并恢复 |
| Shared | V2 IPC、PBKDF2、受保护目录/互斥量、SetupAPI/Configuration Manager 后端 |
| Setup | 需要 UAC 的自包含安装包；嵌入离线负载，安装/重置/卸载 |
| UI | App 与 Setup 链接编译的小型口令对话框源文件 |

## 安装位置

默认位置为 `%ProgramFiles%\TouchLock`，首次安装可选择本地 NTFS 固定磁盘中的新目录。规范化与权限校验拒绝网络路径、系统目录、设备名称、链接和普通用户可替换的上级目录；新目录以 Administrators 所有者和受保护 ACL 原子创建，不更改已有父目录。HKLM 卸载项的 `InstallLocation` 保存实际位置，服务命令行、快捷方式和维护使用该位置；卸载先保存位置再删除注册项，避免误回退到默认目录。密码与恢复记录继续位于 ProgramData。

## 设备事务

SetupAPI 只枚举当前存在的 HID 类节点，通过兼容 ID `HID_DEVICE_UP:000D_U:0004` 精确识别 Touchscreen。混合 Usage 身份、同父节点或同 ContainerId 的鼠标集合、异常状态和不支持动态禁用的节点被拒绝。枚举不触碰任何节点。禁用前再次检查集合身份。

受保护的 `Global\TouchLock.DeviceOperations.v2` 互斥量串行化服务、恢复进程和安装程序的设备操作。恢复记录使用 WriteThrough、Flush(true) 与同目录原子替换；只记录观察到启用的触摸节点，每次禁用前先写记录。`CM_Disable_DevNode` 仅传 `CM_DISABLE_UI_NOT_OK (4)`，随后核对禁用状态；全部成功才报告 Locked。失败则回滚，回滚失败继续保留记录并重试。

`CM_Enable_DevNode` 参数为零，核对节点无问题且已启动后才清理对应记录。移除的节点保留待恢复，直到重新连接或重启后恢复确认。不会重新启用锁定前已禁用的设备。临时禁用默认不跨重启保存：[微软说明](https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_disable_devnode)。

## 认证与 IPC

管道 `TouchLock.Control.v2` 与 `TouchLock.Password.v2` 拒绝远程客户端，DACL 仅 SYSTEM 和 Interactive。服务核对客户端当前控制台会话、确切进程路径及创建时间；客户端核对管道服务 PID 与 SCM 实际 PID。普通请求仅状态/锁定/开始/取消。认证请求绑定 PID、进程创建时间、会话、一次性 GUID 与 180 秒期限；使用后立即失效。

口令通过有长度上限的二进制帧传输，与 JSON 控制消息分离。口令输入读取原生编辑控件到临时字符数组；控件、字符数组及字节数组使用后清除，不创建业务口令 string。无法保证消除 Windows 控件或操作系统内部所有历史副本。

PBKDF2-HMAC-SHA256：600,000 迭代、16 字节随机盐、32 字节哈希，恒定时间比较。记录含固定版本与算法参数，位于 `%ProgramData%\TouchLock\password.json`，SYSTEM/Admin ACL，禁止链接路径。错误五次后全局暂停 30 秒。初始设置和忘记口令重置由提升权限 Setup 执行；复位先停止服务并恢复设备。

## 状态与恢复

`Unlocked → Locking → Locked → Authenticating → Restoring → Unlocked/Stopping`。错误、取消、超时和托盘进程退出返回认证前状态。未锁定的退出认证取消仍未锁定；修改口令保留原锁定状态。恢复失败保持 Restoring，保留服务与恢复记录。

Service 主循环处理设备/会话变化后每秒续约。不是脱离业务处理的独立心跳线程。Recovery 先打开服务的精确进程句柄，再发 Ready；没有 Ready 不允许锁定。服务退出则恢复；30 秒失联则先终止并等待旧服务，再获取互斥量恢复。新服务先恢复旧记录，不恢复旧锁定。SCM 异常重启间隔 10 秒，连续失败后 60 秒。

只以设备禁用实现物理触摸停止，不承诺软件输入注入、厂商私有通道或远控被拦截。标准用户无法写安装/配置目录或停止服务；本机管理员和 SYSTEM 位于信任边界之外。口令持有人获得本软件解锁/退出权限，不代表 Windows 管理员身份。
