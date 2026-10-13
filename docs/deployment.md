# 部署

交付：`TouchLock-1.0.0-Setup-x64.exe`、源码 ZIP、SHA256SUMS。安装程序嵌入 App/Service/Recovery 与完整 .NET x64 运行时，安装不联网。

支持目标 Windows 10/11 x64 的普通桌面环境。有标准触摸屏时先核对锁定/恢复和键鼠不受影响。无触摸屏时托盘会显示不可用，仍可设置/修改口令与执行口令退出。

1. 如安装过旧驱动版，先卸载旧服务和 TouchLockFilter；当前版本不卸载或替换原厂驱动。
2. 双击安装包并接受 UAC，按需修改安装位置，再设置独立口令。默认 `%ProgramFiles%\TouchLock`，也可使用本地 NTFS 磁盘的新文件夹，例如 `D:\TouchLock`。
3. 确认托盘存在，在 About 查看设备/快捷键状态。
4. 通过开始菜单启动；没有自动启动或自动更新。
5. 需要维护时运行安装包，或使用 Windows 应用卸载入口。卸载须先验证当前管理口令，再恢复全部已记录设备；错误或取消保持原安装和服务状态。忘记口令可由 Windows 管理员先重置。

冰点环境在允许保存系统修改的窗口安装并重启检查后再启用还原。驱动证书、测试签名模式、Secure Boot 和 HVCI 设置均无需修改。普通未签名应用的来源提示由 Windows 策略决定。

安装目录及新建的上级目录由 Administrators 拥有，SYSTEM/Admin 可写，Users 只读/执行；不会修改已有上级目录的权限，已有上级目录允许普通用户替换时拒绝安装。实际位置保存在 HKLM 卸载项的 InstallLocation，服务路径、快捷方式、维护及卸载使用同一位置。配置仍为 ProgramData，SYSTEM/Admin 可访问。临时维护副本保持 Admin/SYSTEM ACL，使用完安排重启清理，防止卸载时自身文件占用。
